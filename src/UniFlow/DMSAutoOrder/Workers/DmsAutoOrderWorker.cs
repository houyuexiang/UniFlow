namespace UniFlow.DMSAutoOrder.Workers;

public class DmsAutoOrderWorker : BackgroundService
{
    private readonly ILogger<DmsAutoOrderWorker> _logger;
    private readonly Services.PitStopMonitorService _pitStop;
    private readonly Services.SampleCleanupService _cleanup;
    private readonly Services.StatusCorrectionService _status;
    private readonly Models.DmsOrderConfig _config;

    public DmsAutoOrderWorker(
        ILogger<DmsAutoOrderWorker> logger,
        Services.PitStopMonitorService pitStop,
        Services.SampleCleanupService cleanup,
        Services.StatusCorrectionService status,
        Models.DmsOrderConfig config)
    {
        _logger = logger;
        _pitStop = pitStop;
        _cleanup = cleanup;
        _status = status;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DMSAutoOrder started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pitStop.ExecuteAsync(ct);
                await _status.ExecuteAsync(ct);
                await _cleanup.ExecuteAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "DMSAutoOrder error"); }

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
        }
    }
}
