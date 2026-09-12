using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DMSAutoOrder.Workers;

public class DmsEmptyResultCleanupWorker : BackgroundService
{
    private readonly ILogger<DmsEmptyResultCleanupWorker> _logger;
    private readonly Services.EmptyResultCleanupService _svc;
    private readonly Models.DmsOrderConfig _config;
    private readonly HealthStore _health;
    private readonly IOptionsMonitor<FeatureConfig> _features;
    private bool _pauseReported;

    public DmsEmptyResultCleanupWorker(
        ILogger<DmsEmptyResultCleanupWorker> logger,
        Services.EmptyResultCleanupService svc,
        Models.DmsOrderConfig config,
        HealthStore health,
        IOptionsMonitor<FeatureConfig> features)
    {
        _logger = logger;
        _svc = svc;
        _config = config;
        _health = health;
        _features = features;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DmsEmptyResultCleanup started, interval={Interval}s", _config.LoopIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            if (!_features.CurrentValue.Dms.EmptyResultCleanup)
            {
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("DmsEmptyResultCleanup", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                await _svc.ExecuteAsync(ct);
                try { await _health.RecordHealthAsync("DmsEmptyResultCleanup", "healthy"); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DmsEmptyResultCleanup error");
                try { await _health.RecordHealthAsync("DmsEmptyResultCleanup", "degraded", ex.Message); await _health.RecordErrorAsync("DmsEmptyResultCleanup", "ERROR", ex.Message); } catch { }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_config.LoopIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}