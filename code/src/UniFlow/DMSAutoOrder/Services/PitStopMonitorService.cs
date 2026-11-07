using Microsoft.Extensions.Logging;

namespace UniFlow.DMSAutoOrder.Services;

public class PitStopMonitorService
{
    private readonly ILogger<PitStopMonitorService> _logger;
    private readonly IDmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly Func<DateTime> _nowProvider;
    private readonly Dictionary<string, Dictionary<string, DateTime>> _seen = new();

    public PitStopMonitorService(
        ILogger<PitStopMonitorService> logger,
        IDmsDatabaseService db,
        Models.DmsOrderConfig config)
        : this(logger, db, config, () => DateTime.UtcNow)
    {
    }

    /// <summary>
    /// 测试可注入时钟（生产用 <see cref="DateTime.UtcNow"/>）——让"超时"判定可确定性地复现。
    /// </summary>
    public PitStopMonitorService(
        ILogger<PitStopMonitorService> logger,
        IDmsDatabaseService db,
        Models.DmsOrderConfig config,
        Func<DateTime> nowProvider)
    {
        _logger = logger;
        _db = db;
        _config = config;
        _nowProvider = nowProvider;
    }

    // 说明：是否启用由功能开关（Features.Dms.PitStopMonitor）决定——
    // 该开关控制 DmsPitStopWorker 是否注册；Worker 运行即执行监控。
    //（原 PitStop.EnableMonitor 为旧版单 Worker 时代的双层开关遗留，已废弃）
    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            var tables = await _db.GetPitStopTablesAsync();
            var tableSet = tables.ToHashSet();

            // Clean up tables that no longer exist
            foreach (var t in _seen.Keys.Where(t => !tableSet.Contains(t)).ToList())
                _seen.Remove(t);

            foreach (var table in tables)
            {
                var records = await _db.GetRunningPitstopRecordsAsync(table);
                if (records.Count == 0) continue;

                var ids = new HashSet<string>();
                var timedOut = new List<string>();
                foreach (var r in records)
                {
                    var id = r.GetValueOrDefault("idpitstop", "")?.ToString() ?? "";
                    if (string.IsNullOrEmpty(id)) continue;
                    ids.Add(id);
                    var now = _nowProvider();

                    if (!_seen.ContainsKey(table))
                        _seen[table] = new Dictionary<string, DateTime>();

                    if (!_seen[table].ContainsKey(id))
                    {
                        _seen[table][id] = now;
                    }
                    else
                    {
                        var elapsed = now - _seen[table][id];
                        if (elapsed.TotalMinutes > _config.PitStop.TimeoutMinutes)
                        {
                            // 只把「这一条」记为超时；删除时**只删这些 id**（不再清空整表）
                            timedOut.Add(id);
                        }
                    }
                }

                if (timedOut.Count > 0)
                {
                    // 定点删除：只删超时的那几条 id；同一张表里其它 running 记录**保留**
                    var affected = await _db.DeletePitstopRecordsAsync(table, timedOut);
                    // 计时表也只移除对应 id（其它 id 继续计时，不清整表）
                    if (_seen.ContainsKey(table))
                        foreach (var id in timedOut) _seen[table].Remove(id);
                    _logger.LogWarning("PitStop stuck >{Timeout}m, deleted {Affected} record(s) in table {Table}, ids=[{Ids}]",
                        _config.PitStop.TimeoutMinutes, affected, table, string.Join(",", timedOut));
                }

                if (_seen.ContainsKey(table))
                {
                    var stale = _seen[table].Keys.Where(k => !ids.Contains(k)).ToList();
                    foreach (var k in stale) _seen[table].Remove(k);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PitStop monitoring error");
        }
    }
}
