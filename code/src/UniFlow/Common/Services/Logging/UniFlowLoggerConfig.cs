namespace UniFlow.Common.Services.Logging;

using UniFlow.Common.Models;

public class UniFlowLoggerConfig
{
    [Restart(RestartLevel.ProcessRestart)] public bool FileLoggingEnabled { get; set; } = true;
    [Restart(RestartLevel.ProcessRestart)] public string LogDirectory { get; set; } = "logs";
    [Restart(RestartLevel.ProcessRestart)] public int MaxFileSizeMb { get; set; } = 10;
    [Restart(RestartLevel.ProcessRestart)] public int RetentionDays { get; set; } = 30;
    [Restart(RestartLevel.ProcessRestart)] public string LogLevel { get; set; } = "Information";
}
