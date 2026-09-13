using UniFlow.Common.Models;

namespace UniFlow.DMSAutoOrder.Models;

public class DmsOrderConfig
{
    // ===== 公共配置（三个功能共用） =====
    [Restart(RestartLevel.InnerRestart)] public bool Enabled { get; set; }
    [Restart(RestartLevel.InnerRestart)] public string DbHost { get; set; } = "127.0.0.1";
    [Restart(RestartLevel.InnerRestart)] public int DbPort { get; set; } = 3306;
    [Restart(RestartLevel.InnerRestart)] public string DbUser { get; set; } = "root";
    [Restart(RestartLevel.InnerRestart)] public string DbPassword { get; set; } = "";
    [Restart(RestartLevel.InnerRestart)] public string DbName { get; set; } = "dms";
    [Restart(RestartLevel.InnerRestart)] public int LoopIntervalSeconds { get; set; } = 60;

    // ===== 功能分组配置 =====
    public DmsPitStopConfig PitStop { get; set; } = new();
    public DmsStatusCorrectionConfig StatusCorrection { get; set; } = new();
    public DmsSampleCleanupConfig SampleCleanup { get; set; } = new();
}

public class DmsPitStopConfig
{
    // 是否启用 PitStop 监控由功能开关 Features.Dms.PitStopMonitor 决定（此处无重复开关）
    [Restart(RestartLevel.InnerRestart)] public int TimeoutMinutes { get; set; } = 1;
}

public class TriggerRule
{
    public string TestName { get; set; } = "";
    public int TimeoutMinutes { get; set; } = 1;
}

public class DmsStatusCorrectionConfig
{
    [Restart(RestartLevel.InnerRestart)] public string AutoModifyTestStatus { get; set; } = "1";
    // 新 JSON 格式：IgnoreFlags 数组（表单保存写这里）
    [Restart(RestartLevel.InnerRestart)] public List<string>? IgnoreFlags { get; set; }
    // 旧格式（兼容）：逗号分隔 "flag1,flag2"
    public string IgnoreFlagList { get; set; } = "";

    // 统一读取入口：新格式优先，旧格式宽容解析
    public string[] GetIgnoreFlags()
    {
        if (IgnoreFlags != null)  // 空数组=已显式清空，权威
            return IgnoreFlags.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToArray();
        return IgnoreFlagList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public class DmsSampleCleanupConfig
{
    // 新 JSON 格式：TriggerRules 数组（表单保存写这里）
    [Restart(RestartLevel.InnerRestart)] public List<TriggerRule>? TriggerRules { get; set; }
    // 旧格式（兼容）：分号拼接 "RMSMP:1;XXX:5"，TriggerRules 为空时使用
    [Restart(RestartLevel.InnerRestart)] public string TestTriggerSampleDeletion { get; set; } = "";
    [Restart(RestartLevel.InnerRestart)] public bool SendCancelMessageToAptio { get; set; }

    // 统一读取入口：新格式优先，旧格式宽容解析
    public List<TriggerRule> GetRules()
    {
        if (TriggerRules != null)  // 空数组=已显式清空，权威；null 才回退旧格式
            return TriggerRules.Where(r => !string.IsNullOrWhiteSpace(r.TestName)).ToList();

        var rules = new List<TriggerRule>();
        foreach (var item in TestTriggerSampleDeletion.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split(':');
            if (parts.Length < 2) continue;
            if (!int.TryParse(parts[1].Trim(), out var min) || min < 0) continue;
            rules.Add(new TriggerRule { TestName = parts[0].Trim(), TimeoutMinutes = min });
        }
        return rules;
    }
}