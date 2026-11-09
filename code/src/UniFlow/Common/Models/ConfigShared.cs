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
    // SRM 节点号（批量丢弃/导出监控的存储模块）：业务宿主启动时绑定 → 改后需重建业务宿主
    [Restart(RestartLevel.InnerRestart)] public List<string> SrmNodeIds { get; set; } = new() { "09" };
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
    [Restart(RestartLevel.Hot)] public int MaxOnetimeSelectDiscardCount { get; set; } = 10;
    [Restart(RestartLevel.Hot)] public bool SkipOnUnknownNode { get; set; } = true;

    // ===== 快速填窗（可选，默认关闭）=====
    // EnableFastFill：默认关闭。关闭时行为与 1.0.6.7 完全一致（一条一条发、Ready→Ack→Dispose 逐条确认）。
    // 开启后：在一个 tick 内连续发 COMMENT 直到发送窗口满（窗口上限仍是 MaxOnetimeSelectDiscardCount，默认 10），
    //          不再每条等确认；确认改为批量（FastFillConfirmIntervalSeconds 秒一次，一条 SQL 确认所有在途记录）。
    // 注意：FastFillIntervalMs = 0 只是「每条之间不等待」，**不是**关闭快速填窗——开关只有 EnableFastFill。
    [Restart(RestartLevel.Hot)] public bool EnableFastFill { get; set; } = false;
    // 每条 COMMENT 之间的间隔（毫秒，范围 0~5000）：0 = 不等待（连发）。仅在 EnableFastFill=true 时生效。
    [Restart(RestartLevel.Hot)] public int FastFillIntervalMs { get; set; } = 1000;
    // 批量确认间隔（秒，范围 1~60）：每该秒用一条 SQL 确认所有 send=1 & disposed=0 的在途记录，命中即释放窗口。
    // 仅在 EnableFastFill=true 时生效。
    [Restart(RestartLevel.Hot)] public int FastFillConfirmIntervalSeconds { get; set; } = 5;

    // ===== 测试开关：允许 Stand-by 丢弃 =====
    // false（默认）= 保持现状：可丢弃 = Mode=="ON" && Status∈{G,Y} && 错误码在允许列表
    // true            = 额外允许 Mode=="STB"（Stand-by）与 Status=="P"（Pink）参与丢弃判断
    // GTO / PA / OFF / FR / SH / EXE 始终不允许（与本开关无关）。
    // 业务含义：STB 不允许进新样本，但允许执行丢弃；本开关只管「是否允许 STB/P 参与丢弃判断」，
    // 不涉及进样本逻辑。热生效（Hot），现场测试后再决定是否长期开启。
    [Restart(RestartLevel.Hot)] public bool AllowStandbyDispose { get; set; } = false;

    // ===== 重发 / 失败处置 =====
    // 重排队检查间隔（分钟）：每该分钟检查一次 send=1 且未确认丢弃的记录；
    // 命中（节点 ON + 状态 G/Y + 错误码在允许列表）时把记录放回待发队列（send=0），
    // 由正常状态机按发送窗口一条条下发（本环节绝不直接下发丢弃命令）；0 = 关闭重排队
    [Restart(RestartLevel.Hot)] public int ResendIntervalMinutes { get; set; } = 5;
    // 重发上限（次）：单条记录最多重发多少次；超过则标记 send=3（失败，不再自动重发）；0 = 不限
    [Restart(RestartLevel.Hot)] public int MaxResendCount { get; set; } = 24;
    // 待处理队列硬上限：send=0 或 (send=1 且 disposed=0) 的记录数上限；0 = 不限；达到上限时不再插入新记录
    [Restart(RestartLevel.Hot)] public int MaxPendingRecords { get; set; } = 0;
    // 待处理队列告警阈值：超过该数量打 Warning（每 10 分钟最多一次）；0 = 不告警
    [Restart(RestartLevel.Hot)] public int PendingWarnThreshold { get; set; } = 1000;

    // ===== 按架排序（默认开启）=====
    // true：视图模式下按"架"排序——合格样本最多、最老的架优先，窗口 N 条尽量来自同一架；
    //       &1-{节点}-{架号}、&2-{节点}-{槽} 为架键；&3（已丢弃）/&0/NULL 排除。
    // false：恢复原顺序 SELECT * FROM view LIMIT N（可随时回退）。
    //
    // 作用范围：**只改"候选顺序"**，不改窗口/发送/重排队逻辑；
    // 窗口仍是 MaxOnetimeSelectDiscardCount（默认 10）。
    // 仅对 CommandType=0（视图）生效；CommandType=1（存储过程）由数据库侧自行排序，本开关不生效。
    [Restart(RestartLevel.Hot)] public bool DisposeByRack { get; set; } = true;

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

    // 管理密码：Web 管理界面**破坏性动作**（重启/改配置/升级/删错误/删队列/下载配置）的一次性鉴权口令。
    // - 读取时机：热生效（每次动作从 appsettings.json 读，后台改完立即生效，无需重启）；
    // - 页面**不展示、不可改**：/api/config GET 会把本字段打码成 "***"，API 也不接受写入本字段；
    // - 只能后台改 appsettings.json 的 WebAdmin:AdminPassword；
    // - 置空（""）→ 所有破坏性端点拒绝（403 未配置管理密码）。
    [Restart(RestartLevel.Hot)] public string AdminPassword { get; set; } = "Advia1";
}