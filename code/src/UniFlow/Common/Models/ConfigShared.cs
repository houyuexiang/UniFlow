namespace UniFlow.Common.Models;

public class FeatureConfig
{
    public AptioFeatures Aptio { get; set; } = new();
    public ImmuliteFeatures Immulite { get; set; } = new();
    public DmsFeatures Dms { get; set; } = new();
}

public class AptioFeatures
{
    public bool DisposeSample { get; set; } = true;
    public bool SrmExport { get; set; } = true;
    public bool Delivery { get; set; }
    public bool DeliveryFile { get; set; }
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
    public bool EmptyResultCleanup { get; set; }
}

// ===== Infrastructure Configs =====

public class AptioConfig
{
    // 公共配置
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2055;
    public List<string> SrmNodeIds { get; set; } = new() { "09" };
    public AptioDatabaseConfig? Database { get; set; }

    // 功能分组配置
    public AptioDisposeSampleConfig DisposeSample { get; set; } = new();
    public AptioSrmExportConfig SrmExport { get; set; } = new();
    public AptioDeliveryConfig Delivery { get; set; } = new();
    public AptioDeliveryFileConfig DeliveryFile { get; set; } = new();
    public AptioPriorityConfig Priority { get; set; } = new();
    public AptioTestNameDisposeConfig TestNameDispose { get; set; } = new();
    public AptioBatchScanConfig BatchScan { get; set; } = new();
}

public class AptioDatabaseConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "root";
    public string Password { get; set; } = "root";
    public string Database { get; set; } = "flexlab";
}

public class AptioDisposeSampleConfig
{
    public int CommandType { get; set; } = 0;
    public string CommandName { get; set; } = "view_overtimestoragesample";
    public string DiscardRunDate { get; set; } = "1,2,3,4,5,6,7";
    public string DiscardTimeRange { get; set; } = "01:30-05:45,3800;18:00-07:00,7500;";
    public int LoopIntervalSeconds { get; set; } = 3;
    public int MaxWaitDiscardCount { get; set; } = 2;
    public int MaxOnetimeSelectDiscardCount { get; set; } = 8;
    public string AllowSrmErrorCode { get; set; } = "0000,0F0A,0E59,AAAA";
    public bool SkipOnUnknownNode { get; set; } = true;
}

public class AptioSrmExportConfig
{
    public string ExportTime { get; set; } = "08:30";
    public int LoopIntervalSeconds { get; set; } = 60;
    public int LogRetentionDays { get; set; } = 30;
    public string OutputPath { get; set; } = "DisposeFile";
}

public class AptioDeliveryConfig
{
    public string TestName { get; set; } = "";
}

public class AptioDeliveryFileConfig
{
    public string DeliveryListFilePath { get; set; } = "";
    public int LoopIntervalSeconds { get; set; } = 60;
}

public class AptioPriorityConfig
{
    public string TestName { get; set; } = "";
}

public class AptioTestNameDisposeConfig
{
    public string DisposeTestName { get; set; } = "";
}

public class AptioBatchScanConfig
{
    public int LoopIntervalSeconds { get; set; } = 60;
    public int MaxOnetimeScanCount { get; set; } = 500;
}

public class WebAdminConfig
{
    public bool Enabled { get; set; } = true;
    public string BindIp { get; set; } = "localhost";
    public int Port { get; set; } = 5100;
    public int ErrorRetentionDays { get; set; } = 30;
    public int HealthCheckIntervalSeconds { get; set; } = 60;
}