using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.SrmExport.Models;

namespace UniFlow.SrmExport.Workers;

public class SrmExportWorker : BackgroundService
{
    private readonly ILogger<SrmExportWorker> _logger;
    private readonly Services.IExportDatabaseService _db;
    private readonly Services.IExportFileService _export;
    private readonly AptioConfig _aptio;
    private readonly AptioAutoExportConfig _ec;
    private bool _exportedToday;

    public SrmExportWorker(
        ILogger<SrmExportWorker> logger,
        Services.IExportDatabaseService db,
        Services.IExportFileService export,
        IOptions<AptioConfig> aptioConfig,
        IOptions<AptioAutoProcessConfig> autoProcessConfig)
    {
        _logger = logger;
        _db = db;
        _export = export;
        _aptio = aptioConfig.Value;
        _ec = autoProcessConfig.Value.Export ?? new();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SrmExport started, node={NodeId}, time={Time}",
            _aptio.SrmNodeIds.FirstOrDefault() ?? "?", _ec.ExportTime);

        while (!ct.IsCancellationRequested)
        {
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
            catch (Exception ex) { _logger.LogError(ex, "Export error"); }

            await Task.Delay(TimeSpan.FromSeconds(_ec.LoopIntervalSeconds), ct);
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