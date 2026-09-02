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

    public WorkListCleanerWorker(
        ILogger<WorkListCleanerWorker> logger,
        IEnumerable<IWorkListCleaner> cleaners,
        IEnumerable<CleanerConfig> configs,
        HealthStore health)
    {
        _logger = logger;
        _cleaners = cleaners;
        _health = health;
        _loopInterval = configs.FirstOrDefault()?.LoopIntervalSeconds ?? 60;
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

        await Task.Delay(5000, ct);

        while (!ct.IsCancellationRequested)
        {
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

            await Task.Delay(TimeSpan.FromSeconds(_loopInterval), ct);
            try { await _health.RecordHealthAsync("WorkListCleaner", "healthy"); } catch { }
        }
    }
}