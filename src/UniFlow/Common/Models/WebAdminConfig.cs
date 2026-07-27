namespace UniFlow.Common.Models;

public class WebAdminConfig
{
    public bool Enabled { get; set; } = true;
    public string BindIp { get; set; } = "localhost";
    public int Port { get; set; } = 5100;
    public int ErrorRetentionDays { get; set; } = 30;
    public int HealthCheckIntervalSeconds { get; set; } = 60;
}