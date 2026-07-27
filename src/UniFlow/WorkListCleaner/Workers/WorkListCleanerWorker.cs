using UniFlow.WorkListCleaner.Services;

namespace UniFlow.WorkListCleaner.Workers;

public class WorkListCleanerWorker : BackgroundService
{
    private readonly ILogger<WorkListCleanerWorker> _logger;
    private readonly IEnumerable<IWorkListCleaner> _cleaners;
    private readonly int _loopInterval;

    public WorkListCleanerWorker(
        ILogger<WorkListCleanerWorker> logger,
        IEnumerable<IWorkListCleaner> cleaners,
        IEnumerable<Models.CleanerConfig> configs)
    {
        _logger = logger;
        _cleaners = cleaners;
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
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Cleaner {Name} failed", cleaner.Name);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "WorkListCleaner error"); }

            await Task.Delay(TimeSpan.FromSeconds(_loopInterval), ct);
        }
    }
}
