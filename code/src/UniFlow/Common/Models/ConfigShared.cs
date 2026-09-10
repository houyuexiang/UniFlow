namespace UniFlow.Common.Models;

public class FeatureConfig
{
    public AptioFeatures Aptio { get; set; } = new();
    public ImmuliteFeatures Immulite { get; set; } = new();
    public DmsFeatures Dms { get; set; } = new();

    // 兼容旧配置：从平铺结构迁移
    public bool AptioAutoProcess { get; set; } = true;
    public bool ImmuliteWorkOrderClean { get; set; }
    public bool DmsAutoOrder { get; set; }
    public bool Delivery { get; set; }
    public bool Priority { get; set; }
    public bool TestNameDispose { get; set; }
}

public class AptioFeatures
{
    public bool DisposeSample { get; set; } = true;
    public bool SrmExport { get; set; } = true;
    public bool Delivery { get; set; }
    public bool Priority { get; set; }
    public bool TestNameDispose { get; set; }
}

public class ImmuliteFeatures
{
    public bool WorkListCleaner { get; set; }
}

public class DmsFeatures
{
    public bool PitStopMonitor { get; set; }
    public bool StatusCorrection { get; set; }
    public bool SampleCleanup { get; set; }
}

// ===== Infrastructure Configs =====

public class AptioConfig
{
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2055;
    public List<string> SrmNodeIds { get; set; } = new() { "09" };
    public AptioDatabaseConfig? Database { get; set; }
}

public class AptioDatabaseConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "root";
    public string Database { get; set; } = "flexlab";
}

// ===== AptioAutoProcess Feature Config =====

public class AptioAutoProcessConfig
{
    public AptioAutoDisposeConfig? Dispose { get; set; }
    public AptioAutoDeliverConfig? Deliver { get; set; }
    public AptioAutoPriorityConfig? Priority { get; set; }
    public AptioAutoExportConfig? Export { get; set; }
}

public class AptioAutoDisposeConfig
{
    public int CommandType { get; set; } = 0;
    public string CommandName { get; set; } = "view_overtimestoragesample";
    public string DiscardRunDate { get; set; } = "1,2,3,4,5,6,7";
    public string DiscardTimeRange { get; set; } = "01:30-05:45,3800;18:00-07:00,7500;";
    public int LoopIntervalSeconds { get; set; } = 3;
    public int MaxWaitDiscardCount { get; set; } = 2;
    public int MaxOnetimeSelectDiscardCount { get; set; } = 8;
    public string AllowSrmErrorCode { get; set; } = "0000,0F0A,0E59,AAAA";
    public bool EnableTestNameDispose { get; set; }
    public string DisposeTestName { get; set; } = "";
    public bool SkipOnUnknownNode { get; set; } = true;
}

public class AptioAutoDeliverConfig
{
    public bool Enabled { get; set; }
    public string TestName { get; set; } = "";
    public string DeliveryListFilePath { get; set; } = "";
}

public class AptioAutoPriorityConfig
{
    public bool Enabled { get; set; }
    public string TestName { get; set; } = "";
}

public class AptioAutoExportConfig
{
    public string ExportTime { get; set; } = "08:30";
    public int LoopIntervalSeconds { get; set; } = 60;
    public int LogRetentionDays { get; set; } = 30;
    public string OutputPath { get; set; } = "DisposeFile";
}

public class WebAdminConfig
{
    public bool Enabled { get; set; } = true;
    public string BindIp { get; set; } = "localhost";
    public int Port { get; set; } = 5100;
    public int ErrorRetentionDays { get; set; } = 30;
    public int HealthCheckIntervalSeconds { get; set; } = 60;
}