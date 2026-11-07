using System.Text.Json;
using UniFlow.Common.Models;
using UniFlow.SrmExport.Models;
using UniFlow.WebAdmin.Services;

namespace UniFlow.SrmExport.Workers;

public class SrmExportWorker : BackgroundService
{
    private readonly ILogger<SrmExportWorker> _logger;
    private readonly Services.IExportDatabaseService _db;
    private readonly Services.IExportFileService _export;
    private readonly HealthStore _health;
    private AptioConfig _aptio = new();
    private AptioSrmExportConfig _ec = new();
    private FeatureConfig _features = new();
    // 已完成导出的日期（每天只导一次）；null = 当天还没导过
    // 注意：不再用"now.Hour==0 && now.Minute==0 才复位"的老写法（整点错过一次就永远不导）
    private DateTime? _lastExportDate;
    private bool _pauseReported;

    public SrmExportWorker(
        ILogger<SrmExportWorker> logger,
        Services.IExportDatabaseService db,
        Services.IExportFileService export,
        HealthStore health)
    {
        _logger = logger;
        _db = db;
        _export = export;
        _health = health;
        RefreshConfig();
    }

    private void RefreshConfig()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc == null) return;
            if (doc.TryGetValue("Aptio", out var ap))
            {
                // 解析失败保留上一次可用的配置（绝不静默回退出厂默认）
                try
                {
                    var parsed = JsonSerializer.Deserialize<AptioConfig>(ap.GetRawText());
                    if (parsed != null) _aptio = parsed;
                }
                catch (Exception ex) { _logger.LogError(ex, "Aptio config parse failed, keeping previous values: {Raw}", ap.GetRawText()); }
                _ec = _aptio.SrmExport ?? new();   // SrmExport 属性可能为 null（或首启解析失败时 _aptio 未赋值）的防护
            }
            if (doc.TryGetValue("Features", out var fe))
            {
                try { _features = JsonSerializer.Deserialize<FeatureConfig>(fe.GetRawText()) ?? new(); }
                catch { _features = new(); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SrmExport config reload failed, keeping previous values");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SrmExport started, node={NodeId}, time={Time}",
            _aptio.SrmNodeIds.FirstOrDefault() ?? "?", _ec.ExportTime);

        while (!ct.IsCancellationRequested)
        {
            RefreshConfig();
            if (!_features.Aptio.SrmExport)
            {
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("SrmExport", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                var now = DateTime.Now;
                var target = ParseTimeOnly(_ec.ExportTime);

                // 每次循环检查数据库连通性，反映真实健康状态
                if (!await _db.PingAsync())
                {
                    _logger.LogWarning("SrmExport DB unavailable");
                    try { await _health.RecordHealthAsync("SrmExport", "degraded", "Database unavailable"); } catch { }
                    await Task.Delay(TimeSpan.FromSeconds(_ec.LoopIntervalSeconds), ct);
                    continue;
                }

                await TryExportAsync(now, target, ct);

                try { await _health.RecordHealthAsync("SrmExport", "healthy"); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Export error"); try { await _health.RecordHealthAsync("SrmExport", "degraded", ex.Message); await _health.RecordErrorAsync("SrmExport", "ERROR", ex.Message); } catch { } }

            try { await Task.Delay(TimeSpan.FromSeconds(_ec.LoopIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// 一轮"是否该导出"的处理（抽出为 public 以便单测）。
    /// <para>返回 true = 本轮真的执行了导出；false = 不该导（已导过 / 未到时间）或导出未成功（DB 不可用）。</para>
    /// </summary>
    public async Task<bool> TryExportAsync(DateTime now, TimeOnly target, CancellationToken ct = default)
    {
        if (!ShouldExport(now, target, _lastExportDate)) return false;
        // 只有真正跑完导出流程才记"当天已导出"（DB 不可用 → 不记，下一轮重试）
        if (!await RunExportAsync(ct)) return false;
        _lastExportDate = now.Date;
        return true;
    }

    /// <summary>
    /// 导出时机判定（抽出为 public static 以便单测）：
    /// <list type="bullet">
    /// <item>当天已导出过（lastExportDate 与 now 同一天）→ false（每天只导一次）；</item>
    /// <item>还没到目标时刻（now &lt; target）→ false；</item>
    /// <item>已到/超过目标时刻 → true。</item>
    /// </list>
    /// <para>
    /// 历史缺陷（P3）：老口径要求 <c>now.Hour==target.Hour &amp;&amp; now.Minute==target.Minute</c>
    /// —— 循环延迟 / DB 慢 / 进程重启都可能错过那一分钟 → 当天再也不导。
    /// 现在用"时间窗 + 每日一次标记"：错过整点当天仍会补，重启后当天也会补。
    /// </para>
    /// </summary>
    public static bool ShouldExport(DateTime now, TimeOnly target, DateTime? lastExportDate)
    {
        if (lastExportDate.HasValue && lastExportDate.Value.Date == now.Date) return false;
        return now.TimeOfDay >= target.ToTimeSpan();
    }

    /// <summary>
    /// 执行一次导出。返回 false = 本轮未导出（DB 不可用，调用方**不要**记"当天已导出"，下一轮重试）；
    /// 返回 true = 导出流程已正常走完（含"当天没有可导出的样本"）。
    /// </summary>
    private async Task<bool> RunExportAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting daily export...");
        if (!await _db.PingAsync())
        {
            _logger.LogWarning("DB unavailable, skipping export");
            return false;
        }
var allSamples = new List<UniFlow.SrmExport.Models.DisposedSample>();
        foreach (var nodeId in _aptio.SrmNodeIds)
        {
            var samples = await _db.GetDisposedSamplesAsync(nodeId);
            allSamples.AddRange(samples);
        }

        if (allSamples.Count == 0)
        {
            _logger.LogInformation("No disposed samples from yesterday");
            return true;
        }

        await _export.ExportAsync(allSamples);
        _export.CleanOld(_ec.LogRetentionDays);
        return true;
    }

    private static TimeOnly ParseTimeOnly(string time) =>
        TimeOnly.TryParse(time, out var r) ? r : new TimeOnly(8, 30);
}