using UniFlow.WebAdmin.Services;

namespace UniFlow.DMSAutoOrder.Workers;

public class DmsAutoOrderWorker : BackgroundService
{
    private readonly ILogger<DmsAutoOrderWorker> _logger;
    private readonly Services.PitStopMonitorService _pitStop;
    private readonly Services.SampleCleanupService _cleanup;
    private readonly Services.StatusCorrectionService _status;
    private readonly Models.DmsOrderConfig _config;
    private readonly HealthStore _health;

    public DmsAutoOrderWorker(
        ILogger<DmsAutoOrderWorker> logger,
        Services.PitStopMonitorService pitStop,
        Services.SampleCleanupService cleanup,
        Services.StatusCorrectionService status,
        Models.DmsOrderConfig config,
        HealthStore health)
    {
        _logger = logger;
        _pitStop = pitStop;
        _cleanup = cleanup;
        _status = status;
        _config = config;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DMSAutoOrder started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await _pitStop.ExecuteAsync(ct); try { await _health.RecordHealthAsync("DmsPitStop", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("PitStop error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DmsPitStop", "degraded", ex.Message); } catch { } }

                try { await _status.ExecuteAsync(ct); try { await _health.RecordHealthAsync("DmsStatusCorr", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("StatusCorr error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DmsStatusCorr", "degraded", ex.Message); } catch { } }

                try { await _cleanup.ExecuteAsync(ct); try { await _health.RecordHealthAsync("DmsCleanup", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("Cleanup error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DmsCleanup", "degraded", ex.Message); } catch { } }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "DMSAutoOrder error"); try { await _health.RecordHealthAsync("DmsAutoOrder", "degraded", ex.Message); await _health.RecordErrorAsync("DmsAutoOrder", "ERROR", ex.Message); } catch { } }

            await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct);
            try { await _health.RecordHealthAsync("DmsAutoOrder", "healthy"); } catch { }
        }
    }
}
