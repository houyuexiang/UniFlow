using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.WorkListCleaner.Models;
using UniFlow.WorkListCleaner.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.WorkListCleaner.Workers;

public class WorkListCleanerWorker : BackgroundService
{
    private readonly ILogger<WorkListCleanerWorker> _logger;
    private readonly IEnumerable<IWorkListCleaner> _cleaners;
    private readonly int _loopInterval;
    private readonly HealthStore _health;
    private readonly IOptionsMonitor<FeatureConfig> _features;
    private bool _pauseReported;

    public WorkListCleanerWorker(
        ILogger<WorkListCleanerWorker> logger,
        IEnumerable<IWorkListCleaner> cleaners,
        IEnumerable<CleanerConfig> configs,
        HealthStore health,
        IOptionsMonitor<FeatureConfig> features)
    {
        _logger = logger;
        _cleaners = cleaners;
        _health = health;
        _loopInterval = configs.FirstOrDefault()?.LoopIntervalSeconds ?? 60;
        _features = features;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var enabled = _cleaners.ToList();
        if (enabled.Count == 0)
        {
            _logger.LogInformation("No cleaners registered");
            return;
        }

        _logger.LogInformation("WorkListCleaner started with {Count} cleaners", enabled.Count);
        foreach (var c in enabled)
            _logger.LogInformation("  Cleaner: {Name}", c.Name);

        try { await Task.Delay(5000, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            if (!_features.CurrentValue.Immulite.WorkListCleaner)
            {
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("WorkListCleaner", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                foreach (var cleaner in enabled)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await cleaner.CleanAsync(ct);
                        try { await _health.RecordHealthAsync(cleaner.Name, "healthy"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Cleaner {Name} failed", cleaner.Name);
                        try { await _health.RecordHealthAsync(cleaner.Name, "degraded", ex.Message); await _health.RecordErrorAsync(cleaner.Name, "ERROR", ex.Message); } catch { }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "WorkListCleaner error"); try { await _health.RecordHealthAsync("WorkListCleaner", "degraded", ex.Message); await _health.RecordErrorAsync("WorkListCleaner", "ERROR", ex.Message); } catch { } }

            try { await Task.Delay(TimeSpan.FromSeconds(_loopInterval), ct); }
            catch (OperationCanceledException) { break; }
            try { await _health.RecordHealthAsync("WorkListCleaner", "healthy"); } catch { }
        }
    }
}