namespace UniFlow.DMSAutoOrder.Models;

public class DmsOrderConfig
{
    public bool Enabled { get; set; }
    public string DbHost { get; set; } = "127.0.0.1";
    public int DbPort { get; set; } = 3306;
    public string DbUser { get; set; } = "root";
    public string DbPassword { get; set; } = "";
    public string DbName { get; set; } = "dms";
    public int LoopIntervalSeconds { get; set; } = 60;

    public bool EnablePitStopMonitor { get; set; }
    public int PitStopTimeoutMinutes { get; set; } = 1;

    public string AutoModifyTestStatus { get; set; } = "1";
    public string IgnoreFlagList { get; set; } = "";
    public string TestTriggerSampleDeletion { get; set; } = "";
    public string DeleteTableList { get; set; } = "";
    public bool SendCancelMessageToAptio { get; set; }
    public string AptioConnectString { get; set; } = "";
}
