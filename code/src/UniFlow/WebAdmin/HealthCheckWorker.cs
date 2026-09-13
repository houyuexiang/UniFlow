using UniFlow.WebAdmin.Services;

namespace UniFlow.WebAdmin;

public class HealthCheckWorker : BackgroundService
{
    private readonly HealthStore _health;
    private readonly Common.Models.WebAdminConfig _config;
    private readonly RestartManager _restart;
    private readonly ILogger<HealthCheckWorker> _logger;

    public HealthCheckWorker(HealthStore health, Common.Models.WebAdminConfig config, RestartManager restart, ILogger<HealthCheckWorker> logger)
    {
        _health = health;
        _config = config;
        _restart = restart;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("HealthCheck started, interval={Interval}s", _config.HealthCheckIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 业务宿主死亡（如重启失败）时如实上报，避免仪表盘假显示"正常"
                if (!_restart.IsReady)
                    await _health.RecordHealthAsync("System", "degraded", "Business host not running");
                else
                    await _health.RecordHealthAsync("System", "healthy", "All modules running");
                await _health.CleanupOldAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HealthCheck error: {Msg}", ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}