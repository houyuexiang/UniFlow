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
        Microsoft.Extensions.Options.IOptions<Models.DmsOrderConfig> config)
    {
        _logger = logger;
        _pitStop = pitStop;
        _cleanup = cleanup;
        _status = status;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DMSAutoOrder started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pitStop.ExecuteAsync();
                await _status.ExecuteAsync();
                await _cleanup.ExecuteAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "DMSAutoOrder error"); }

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
        }
    }
}
