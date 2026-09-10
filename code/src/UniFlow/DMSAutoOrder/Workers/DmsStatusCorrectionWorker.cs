using UniFlow.WebAdmin.Services;

namespace UniFlow.DMSAutoOrder.Workers;

public class DmsStatusCorrectionWorker : BackgroundService
{
    private readonly ILogger<DmsStatusCorrectionWorker> _logger;
    private readonly Services.StatusCorrectionService _svc;
    private readonly Models.DmsOrderConfig _config;
    private readonly HealthStore _health;

    public DmsStatusCorrectionWorker(
        ILogger<DmsStatusCorrectionWorker> logger,
        Services.StatusCorrectionService svc,
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
        _logger.LogInformation("DmsStatusCorr started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _svc.ExecuteAsync(ct);
                try { await _health.RecordHealthAsync("DmsStatusCorr", "healthy"); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DmsStatusCorr error");
                try { await _health.RecordHealthAsync("DmsStatusCorr", "degraded", ex.Message); await _health.RecordErrorAsync("DmsStatusCorr", "ERROR", ex.Message); } catch { }
            }

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
        }
    }
}