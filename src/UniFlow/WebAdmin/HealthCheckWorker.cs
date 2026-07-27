using UniFlow.WebAdmin.Services;

namespace UniFlow.WebAdmin;

public class HealthCheckWorker : BackgroundService
{
    private readonly HealthStore _health;
    private readonly Common.Models.WebAdminConfig _config;
    private readonly ILogger<HealthCheckWorker> _logger;

    public HealthCheckWorker(HealthStore health, Common.Models.WebAdminConfig config, ILogger<HealthCheckWorker> logger)
    {
        _health = health;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("HealthCheck started, interval={Interval}s", _config.HealthCheckIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _health.RecordHealth("System", "healthy", "All modules running");
                _health.CleanupOld();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HealthCheck error: {Msg}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds), ct);
        }
    }
}