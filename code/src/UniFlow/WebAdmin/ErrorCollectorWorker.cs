using UniFlow.Common.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.WebAdmin;

// 专属错误收集 Worker：从队列收集各模块上报的错误，统一写入数据库
public class ErrorCollectorWorker : BackgroundService
{
    private readonly ErrorReporter _reporter;
    private readonly HealthStore _health;
    private readonly ILogger<ErrorCollectorWorker> _logger;

    public ErrorCollectorWorker(
        ErrorReporter reporter,
        HealthStore health,
        ILogger<ErrorCollectorWorker> logger)
    {
        _reporter = reporter;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("ErrorCollector started");
        try
        {
            await foreach (var e in _reporter.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _health.WriteErrorAsync(e.Module, e.Level, e.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error write failed: {Msg}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}