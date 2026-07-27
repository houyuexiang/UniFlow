using Microsoft.Extensions.Options;
using UniFlow.SrmExport.Models;

namespace UniFlow.SrmExport.Workers;

public class SrmExportWorker : BackgroundService
{
    private readonly ILogger<SrmExportWorker> _logger;
    private readonly Services.IExportDatabaseService _db;
    private readonly Services.IExportFileService _export;
    private readonly SrmExportConfig _config;
    private bool _exportedToday;

    public SrmExportWorker(
        ILogger<SrmExportWorker> logger,
        Services.IExportDatabaseService db,
        Services.IExportFileService export,
        IOptions<SrmExportConfig> config)
    {
        _logger = logger;
        _db = db;
        _export = export;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SrmExport started, node={NodeId}, time={Time}",
            _config.SrmNodeId, _config.ExportTime);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                var target = ParseTimeOnly(_config.ExportTime);

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

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
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

        var samples = await _db.GetDisposedSamplesAsync(_config.SrmNodeId);
        if (samples.Count == 0)
        {
            _logger.LogInformation("No disposed samples from yesterday");
            return;
        }

        await _export.ExportAsync(samples);
        _export.CleanOld(_config.LogRetentionDays);
    }

    private static TimeOnly ParseTimeOnly(string time) =>
        TimeOnly.TryParse(time, out var r) ? r : new TimeOnly(8, 30);
}
