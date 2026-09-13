using Microsoft.Extensions.Logging;

namespace UniFlow.DMSAutoOrder.Services;

public class PitStopMonitorService
{
    private readonly ILogger<PitStopMonitorService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly Dictionary<string, Dictionary<string, DateTime>> _seen = new();

    public PitStopMonitorService(
        ILogger<PitStopMonitorService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config)
    {
        _logger = logger;
        _db = db;
        _config = config;
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
                foreach (var r in records)
                {
                    var id = r.GetValueOrDefault("idpitstop", "")?.ToString() ?? "";
                    ids.Add(id);
                    var now = DateTime.UtcNow;

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
                                // 变更追溯：记录被清理的任务明细
                                var detail = records.Select(r => r.GetValueOrDefault("idpitstop", "")?.ToString() ?? "")
                                    .Where(x => x != "").ToList();
                                await _db.DeletePitstopRecordsAsync(table);
                                _logger.LogWarning("PitStop stuck >{Timeout}m, cleaned table {Table}, ids=[{Ids}]",
                                    _config.PitStop.TimeoutMinutes, table, string.Join(",", detail));
                            _seen[table].Clear();
                            break;
                        }
                    }
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
