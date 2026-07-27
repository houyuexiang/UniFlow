namespace UniFlow.Common.Models;

public class FeatureConfig
{
    public bool DisposeSampleEnabled { get; set; } = true;
    public bool SrmExportEnabled { get; set; } = true;
    public bool WorkListCleanerEnabled { get; set; }
    public bool DmsAutoOrderEnabled { get; set; }
}

public class AptioConfig
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2055;
}

public class DatabaseConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "root";
    public string Database { get; set; } = "flexlab";
}
