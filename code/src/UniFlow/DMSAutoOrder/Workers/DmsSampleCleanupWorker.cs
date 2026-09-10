using UniFlow.WebAdmin.Services;

namespace UniFlow.DMSAutoOrder.Workers;

public class DmsSampleCleanupWorker : BackgroundService
{
    private readonly ILogger<DmsSampleCleanupWorker> _logger;
    private readonly Services.SampleCleanupService _svc;
    private readonly Models.DmsOrderConfig _config;
    private readonly HealthStore _health;

    public DmsSampleCleanupWorker(
        ILogger<DmsSampleCleanupWorker> logger,
        Services.SampleCleanupService svc,
        Models.DmsOrderConfig config,
        HealthStore health)
    {
        _logger = logger;
        _svc = svc;
        _config = config;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DmsCleanup started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _svc.ExecuteAsync(ct);
                try { await _health.RecordHealthAsync("DmsCleanup", "healthy"); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DmsCleanup error");
                try { await _health.RecordHealthAsync("DmsCleanup", "degraded", ex.Message); await _health.RecordErrorAsync("DmsCleanup", "ERROR", ex.Message); } catch { }
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
        }
    }
}