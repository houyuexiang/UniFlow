using UniFlow.WebAdmin.Services;

namespace UniFlow.DMSAutoOrder.Workers;

public class DmsPitStopWorker : BackgroundService
{
    private readonly ILogger<DmsPitStopWorker> _logger;
    private readonly Services.PitStopMonitorService _svc;
    private readonly Models.DmsOrderConfig _config;
    private readonly HealthStore _health;

    public DmsPitStopWorker(
        ILogger<DmsPitStopWorker> logger,
        Services.PitStopMonitorService svc,
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
        _logger.LogInformation("DmsPitStop started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _svc.ExecuteAsync(ct);
                try { await _health.RecordHealthAsync("DmsPitStop", "healthy"); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DmsPitStop error");
                try { await _health.RecordHealthAsync("DmsPitStop", "degraded", ex.Message); await _health.RecordErrorAsync("DmsPitStop", "ERROR", ex.Message); } catch { }
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
        }
    }
}