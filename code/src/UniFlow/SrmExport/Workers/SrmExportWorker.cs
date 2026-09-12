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
    private AptioConfig _aptio = default!;
    private AptioSrmExportConfig _ec = new();
    private FeatureConfig _features = new();
    private bool _exportedToday;
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
                try { _aptio = JsonSerializer.Deserialize<AptioConfig>(ap.GetRawText()) ?? new(); }
                catch (Exception ex) { _logger.LogError(ex, "Aptio config parse failed, using defaults: {Raw}", ap.GetRawText()); _aptio = new(); }
                _ec = _aptio.SrmExport;
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

                if (!_exportedToday && now.Hour == target.Hour && now.Minute == target.Minute)
                {
                    await RunExportAsync(ct);
                    _exportedToday = true;
                }

                if (now.Hour == 0 && now.Minute == 0)
                    _exportedToday = false;

                try { await _health.RecordHealthAsync("SrmExport", "healthy"); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Export error"); try { await _health.RecordHealthAsync("SrmExport", "degraded", ex.Message); await _health.RecordErrorAsync("SrmExport", "ERROR", ex.Message); } catch { } }

            try { await Task.Delay(TimeSpan.FromSeconds(_ec.LoopIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunExportAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting daily export...");
        if (!await _db.PingAsync())
        {
            _logger.LogWarning("DB unavailable, skipping export");
            return;
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
            return;
        }

        await _export.ExportAsync(allSamples);
        _export.CleanOld(_ec.LogRetentionDays);
    }

    private static TimeOnly ParseTimeOnly(string time) =>
        TimeOnly.TryParse(time, out var r) ? r : new TimeOnly(8, 30);
}