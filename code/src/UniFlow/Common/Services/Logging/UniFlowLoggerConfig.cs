namespace UniFlow.Common.Services.Logging;

public class UniFlowLoggerConfig
{
    public bool FileLoggingEnabled { get; set; } = true;
    public string LogDirectory { get; set; } = "logs";
    public int MaxFileSizeMb { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;
    public string LogLevel { get; set; } = "Information";
}
