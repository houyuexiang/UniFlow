namespace UniFlow.DMSAutoOrder.Models;

public class DmsOrderConfig
{
    // ===== 公共配置（三个功能共用） =====
    public bool Enabled { get; set; }
    public string DbHost { get; set; } = "127.0.0.1";
    public int DbPort { get; set; } = 3306;
    public string DbUser { get; set; } = "root";
    public string DbPassword { get; set; } = "";
    public string DbName { get; set; } = "dms";
    public int LoopIntervalSeconds { get; set; } = 60;

    // ===== 功能分组配置 =====
    public DmsPitStopConfig PitStop { get; set; } = new();
    public DmsStatusCorrectionConfig StatusCorrection { get; set; } = new();
    public DmsSampleCleanupConfig SampleCleanup { get; set; } = new();
}

public class DmsPitStopConfig
{
    public bool EnableMonitor { get; set; }
    public int TimeoutMinutes { get; set; } = 1;
}

public class DmsStatusCorrectionConfig
{
    public string AutoModifyTestStatus { get; set; } = "1";
    public string IgnoreFlagList { get; set; } = "";
}

public class DmsSampleCleanupConfig
{
    public string TestTriggerSampleDeletion { get; set; } = "";
    public bool SendCancelMessageToAptio { get; set; }
}