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
    private AptioAutoExportConfig _ec = new();
    private bool _exportedToday;

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
            if (doc.TryGetValue("Aptio", out var ap)) _aptio = JsonSerializer.Deserialize<AptioConfig>(ap.GetRawText()) ?? new();
            if (doc.TryGetValue("AptioAutoProcess", out var aa))
            {
                var aap = JsonSerializer.Deserialize<AptioAutoProcessConfig>(aa.GetRawText());
                if (aap != null) _ec = aap.Export ?? new();
            }
        }
        catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SrmExport started, node={NodeId}, time={Time}",
            _aptio.SrmNodeIds.FirstOrDefault() ?? "?", _ec.ExportTime);

        while (!ct.IsCancellationRequested)
        {
            RefreshConfig();
            try
            {
                var now = DateTime.Now;
                var target = ParseTimeOnly(_ec.ExportTime);

                if (!_exportedToday && now.Hour == target.Hour && now.Minute == target.Minute)
                {
                    await RunExportAsync(ct);
                    _exportedToday = true;
                }

                if (now.Hour == 0 && now.Minute == 0)
                    _exportedToday = false;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Export error"); try { await _health.RecordHealthAsync("SrmExport", "degraded", ex.Message); await _health.RecordErrorAsync("SrmExport", "ERROR", ex.Message); } catch { } }

            await Task.Delay(TimeSpan.FromSeconds(_ec.LoopIntervalSeconds), ct);
            try { await _health.RecordHealthAsync("SrmExport", "healthy"); } catch { }
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