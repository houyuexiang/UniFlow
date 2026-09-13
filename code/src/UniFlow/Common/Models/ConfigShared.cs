using UniFlow.Common.Services;

namespace UniFlow.Common.Models;

public class FeatureConfig
{
    public AptioFeatures Aptio { get; set; } = new();
    public ImmuliteFeatures Immulite { get; set; } = new();
    public DmsFeatures Dms { get; set; } = new();
}

public class AptioFeatures
{
    [Restart(RestartLevel.InnerRestart)] public bool DisposeSample { get; set; } = true;
    [Restart(RestartLevel.InnerRestart)] public bool SrmExport { get; set; } = true;
    [Restart(RestartLevel.InnerRestart)] public bool Delivery { get; set; }
    [Restart(RestartLevel.InnerRestart)] public bool DeliveryFile { get; set; }
    [Restart(RestartLevel.InnerRestart)] public bool Priority { get; set; }
    [Restart(RestartLevel.InnerRestart)] public bool TestNameDispose { get; set; }
}

public class ImmuliteFeatures
{
    public bool WorkListCleaner { get; set; }
}

public class DmsFeatures
{
    [Restart(RestartLevel.InnerRestart)] public bool PitStopMonitor { get; set; }
    [Restart(RestartLevel.InnerRestart)] public bool StatusCorrection { get; set; }
    [Restart(RestartLevel.InnerRestart)] public bool SampleCleanup { get; set; }
    [Restart(RestartLevel.InnerRestart)] public bool EmptyResultCleanup { get; set; }
}

// ===== Infrastructure Configs =====

public class AptioConfig
{
    // 公共配置
    [Restart(RestartLevel.InnerRestart)] public string Ip { get; set; } = "127.0.0.1";
    [Restart(RestartLevel.InnerRestart)] public int Port { get; set; } = 2055;
    public List<string> SrmNodeIds { get; set; } = new() { "09" };
    [Restart(RestartLevel.InnerRestart)] public AptioDatabaseConfig? Database { get; set; }

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
    [Restart(RestartLevel.InnerRestart)] public string Host { get; set; } = "127.0.0.1";
    [Restart(RestartLevel.InnerRestart)] public int Port { get; set; } = 3306;
    [Restart(RestartLevel.InnerRestart)] public string User { get; set; } = "root";
    [Restart(RestartLevel.InnerRestart)] public string Password { get; set; } = "root";
    [Restart(RestartLevel.InnerRestart)] public string Database { get; set; } = "flexlab";
}

public class AptioDisposeSampleConfig
{
    [Restart(RestartLevel.Hot)] public int CommandType { get; set; } = 0;
    [Restart(RestartLevel.Hot)] public string CommandName { get; set; } = "view_overtimestoragesample";

    // ===== 新 JSON 格式（表单保存写这里）=====
    // 运行日（1=周一…7=周日）
    [Restart(RestartLevel.Hot)] public List<int>? RunDays { get; set; }
    // 运行时间段与阈值
    [Restart(RestartLevel.Hot)] public List<TimeRangeDto>? TimeRanges { get; set; }
    // 允许的 SRM 错误码
    [Restart(RestartLevel.Hot)] public List<string>? AllowedSrmErrorCodes { get; set; }

    // ===== 旧格式（兼容，分号/逗号拼接）=====
    public string DiscardRunDate { get; set; } = "1,2,3,4,5,6,7";
    public string DiscardTimeRange { get; set; } = "01:30-05:45,3800;18:00-07:00,7500;";
    public string AllowSrmErrorCode { get; set; } = "0000,0F0A,0E59,AAAA";

    [Restart(RestartLevel.Hot)] public int LoopIntervalSeconds { get; set; } = 3;
    [Restart(RestartLevel.Hot)] public int MaxWaitDiscardCount { get; set; } = 2;
    [Restart(RestartLevel.Hot)] public int MaxOnetimeSelectDiscardCount { get; set; } = 8;
    [Restart(RestartLevel.Hot)] public bool SkipOnUnknownNode { get; set; } = true;

    // ===== 统一读取入口 =====
    // 语义：新格式属性 != null 即权威（空数组 = 显式清空/禁用），null 才回退旧格式字符串。
    // 三层（前端回显 / 前端变更检测 / 运行时）对该语义保持一致——见改动记录。
    public HashSet<int> GetRunDays()
    {
        if (RunDays != null)
            return RunDays.Where(d => d >= 1 && d <= 7).ToHashSet();
        return WorkTimeChecker.ParseRunDays(DiscardRunDate);
    }

    public List<TimeRange> GetTimeRanges()
    {
        if (TimeRanges != null)
        {
            var result = new List<TimeRange>();
            foreach (var r in TimeRanges)
            {
                if (TimeOnly.TryParse(r.Start, System.Globalization.CultureInfo.InvariantCulture, out var st) &&
                    TimeOnly.TryParse(r.End, System.Globalization.CultureInfo.InvariantCulture, out var et) &&
                    r.Threshold >= 0)
                    result.Add(new TimeRange(st, et, r.Threshold));
            }
            return result;  // 非空配置即权威：全部条目非法 → 空列表（禁用丢弃，安全方向），不回退旧串
        }
        return WorkTimeChecker.ParseTimeRanges(DiscardTimeRange);
    }

    public HashSet<string> GetAllowedErrors()
    {
        if (AllowedSrmErrorCodes != null)
            return AllowedSrmErrorCodes.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim()).ToHashSet();
        return new HashSet<string>(
            AllowSrmErrorCode.Split(',', StringSplitOptions.TrimEntries)
                .Where(c => !string.IsNullOrEmpty(c)));
    }
}

// 时间段 DTO（JSON 存储用字符串时间，避免 TimeOnly 绑定问题）
public record TimeRangeDto(string Start, string End, int Threshold);

public class AptioSrmExportConfig
{
    [Restart(RestartLevel.Hot)] public string ExportTime { get; set; } = "08:30";
    [Restart(RestartLevel.Hot)] public int LoopIntervalSeconds { get; set; } = 60;
    [Restart(RestartLevel.Hot)] public int LogRetentionDays { get; set; } = 30;
    [Restart(RestartLevel.InnerRestart)] public string OutputPath { get; set; } = "DisposeFile";
}

public class AptioDeliveryConfig
{
    [Restart(RestartLevel.InnerRestart)] public string TestName { get; set; } = "";
}

public class AptioDeliveryFileConfig
{
    [Restart(RestartLevel.InnerRestart)] public string DeliveryListFilePath { get; set; } = "";
    [Restart(RestartLevel.InnerRestart)] public int LoopIntervalSeconds { get; set; } = 60;
}

public class AptioPriorityConfig
{
    [Restart(RestartLevel.InnerRestart)] public string TestName { get; set; } = "";
}

public class AptioTestNameDisposeConfig
{
    [Restart(RestartLevel.InnerRestart)] public string DisposeTestName { get; set; } = "";
}

public class AptioBatchScanConfig
{
    [Restart(RestartLevel.InnerRestart)] public int LoopIntervalSeconds { get; set; } = 60;
    [Restart(RestartLevel.InnerRestart)] public int MaxOnetimeScanCount { get; set; } = 500;
}

public class WebAdminConfig
{
    [Restart(RestartLevel.ProcessRestart)] public bool Enabled { get; set; } = true;
    [Restart(RestartLevel.ProcessRestart)] public string BindIp { get; set; } = "localhost";
    [Restart(RestartLevel.ProcessRestart)] public int Port { get; set; } = 5100;
    [Restart(RestartLevel.ProcessRestart)] public int ErrorRetentionDays { get; set; } = 30;
    [Restart(RestartLevel.ProcessRestart)] public int HealthCheckIntervalSeconds { get; set; } = 60;
}