using System.Reflection;
using UniFlow.Common.Models;
using UniFlow.Common.Services.Logging;
using UniFlow.DMSAutoOrder.Models;

namespace UniFlow.WebAdmin.Services;

// 配置 path → 重启级别 解析器（模型 [Restart] 特性即真相）
public static class RestartLevelResolver
{
    // 顶层键 → 根模型类型（Logging.LogLevel 是字典无强类型，特判 hot）
    private static readonly Dictionary<string, Type> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Aptio"] = typeof(AptioConfig),
        ["DMS"] = typeof(DmsOrderConfig),
        ["Features"] = typeof(FeatureConfig),
        ["WebAdmin"] = typeof(WebAdminConfig),
        ["Logging:UniFlowFile"] = typeof(UniFlowLoggerConfig),
    };

    public static (RestartLevel level, bool known) Resolve(string jsonPath)
        => ResolveWithKind(jsonPath) is var r ? (r.level, r.known) : default;

    // 解析 path → 重启级别 + 期望值类型（kind 用于 PUT 参数校验）
    public static (RestartLevel level, bool known, string kind) ResolveWithKind(string jsonPath)
    {
        // 特判：日志级别规则热生效
        if (jsonPath.StartsWith("Logging:LogLevel", StringComparison.OrdinalIgnoreCase))
            return (RestartLevel.Hot, true, "string");

        // 先尝试多段根（Logging:UniFlowFile），再单段根
        foreach (var root in Roots.Keys.OrderByDescending(k => k.Count(c => c == ':')))
        {
            if (!jsonPath.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase)
                && !jsonPath.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = jsonPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                ? "" : jsonPath[(root.Length + 1)..];
            var type = Roots[root];
            return Navigate(type, rest);
        }
        return (RestartLevel.InnerRestart, false, "unknown");   // 未知路径：拒绝写入
    }

    private static (RestartLevel, bool, string) Navigate(Type type, string rest)
    {
        var level = RestartLevel.InnerRestart;
        var known = false;
        var cur = type;
        var segments = string.IsNullOrEmpty(rest) ? Array.Empty<string>()
            : rest.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var lastKind = "unknown";
        foreach (var seg in segments)
        {
            if (cur == null) break;
            // 数组元素段（如 0）→ 切换到元素类型（kind 不变，仍是该数组类型）
            if (int.TryParse(seg, out _))
            {
                if (cur.IsArray) { cur = cur.GetElementType(); continue; }
                if (cur.IsGenericType && cur.GetGenericTypeDefinition() == typeof(List<>))
                { cur = cur.GetGenericArguments()[0]; continue; }
            }
            var prop = cur.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));
            if (prop == null) return (RestartLevel.InnerRestart, known, "unknown");
            var attr = prop.GetCustomAttribute<RestartAttribute>();
            if (attr != null) { level = attr.Level; known = true; }
            // kind 基于属性的真实类型（解包前）——List<X> 应返回对应 array kind
            var ptype = prop.PropertyType;
            if (ptype.IsGenericType && ptype.GetGenericTypeDefinition() == typeof(List<>))
            {
                var arg = ptype.GetGenericArguments()[0];
                lastKind = arg == typeof(string) ? "stringArray"
                    : arg == typeof(int) ? "intArray"
                    : arg.Name == "TriggerRule" ? "triggerRules"
                    : arg.Name == "TimeRangeDto" ? "timeRanges"
                    : "objectArray";
                cur = arg;
            }
            else
            {
                lastKind = KindOf(ptype);
                cur = ptype;
            }
        }
        return (level, known, known ? lastKind : "unknown");
    }

    // 末属性类型 → 预期 kind
    private static string KindOf(Type? t)
    {
        if (t == null) return "unknown";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(int)) return "int";
        if (t == typeof(string)) return "string";
        if (t == typeof(List<string>)) return "stringArray";
        if (t == typeof(List<int>)) return "intArray";
        if (t == typeof(List<TriggerRule>)) return "triggerRules";
        if (t == typeof(List<TimeRangeDto>)) return "timeRanges";
        return "unknown";
    }

    // PUT 参数校验：kind 与 value 匹配则返回规范化 value，否则返回错误信息
    public static (object? normalized, string? error) ValidateValue(string kind, System.Text.Json.JsonElement value)
    {
        switch (kind)
        {
            case "bool":
                if (value.ValueKind == System.Text.Json.JsonValueKind.True) return (true, null);
                if (value.ValueKind == System.Text.Json.JsonValueKind.False) return (false, null);
                if (value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    if (bool.TryParse(value.GetString(), out var b)) return (b, null);
                    return (null, $"Value must be true or false, got '{value.GetString()}'");
                }
                return (null, "Value must be a boolean (true/false)");
            case "int":
                if (value.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    if (value.TryGetInt32(out var n)) return (n, null);
                    return (null, $"Value '{value.GetRawText()}' is not a valid 32-bit integer");
                }
                if (value.ValueKind == System.Text.Json.JsonValueKind.String
                    && int.TryParse(value.GetString(), out var n2)) return (n2, null);
                return (null, "Value must be an integer");
            case "string":
                // 严格：只收字符串（数字/布尔会被拒绝——历史上 BindIp 曾被写成 1 导致 Kestrel 绑定失败崩溃循环）
                if (value.ValueKind == System.Text.Json.JsonValueKind.String) return (value.GetString(), null);
                return (null, "Value must be a string (wrap it in double quotes in the JSON body)");
            case "stringArray":
                if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return (null, "Value must be an array of strings, e.g. [\"0000\",\"0F0A\"]");
                var sa = new List<string>();
                foreach (var e in value.EnumerateArray())
                {
                    if (e.ValueKind != System.Text.Json.JsonValueKind.String)
                        return (null, "Array elements must be strings, e.g. [\"0000\",\"0F0A\"]");
                    if (!string.IsNullOrWhiteSpace(e.GetString())) sa.Add(e.GetString()!.Trim());
                }
                return (sa, null);
            case "intArray":
                if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return (null, "Value must be an array of integers, e.g. [1,3,5]");
                var ia = new List<int>();
                foreach (var e in value.EnumerateArray())
                {
                    if (e.ValueKind != System.Text.Json.JsonValueKind.Number || !e.TryGetInt32(out var iv))
                        return (null, "Array elements must be integers, e.g. [1,3,5]");
                    ia.Add(iv);
                }
                return (ia, null);
            case "triggerRules":
                if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return (null, "Value must be an array of objects, e.g. [{\"TestName\":\"RMSMP\",\"TimeoutMinutes\":1}]");
                var tr = new List<DmsOrderConfigPair.TriggerRuleDto>();
                foreach (var e in value.EnumerateArray())
                {
                    if (e.ValueKind != System.Text.Json.JsonValueKind.Object)
                        return (null, "Array elements must be objects like {\"TestName\":\"RMSMP\",\"TimeoutMinutes\":1}");
                    var tn = e.TryGetProperty("TestName", out var tnP) ? tnP.GetString() : null;
                    int tm = 1;
                    var okTm = e.TryGetProperty("TimeoutMinutes", out var tmP) && tmP.TryGetInt32(out tm);
                    if (string.IsNullOrWhiteSpace(tn))
                        return (null, "Each rule requires a non-empty \"TestName\"");
                    if (!okTm || tm < 0)
                        return (null, "Each rule requires \"TimeoutMinutes\" (integer >= 0)");
                    tr.Add(new DmsOrderConfigPair.TriggerRuleDto { TestName = tn.Trim(), TimeoutMinutes = tm });
                }
                return (tr, null);
            case "timeRanges":
                if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return (null, "Value must be an array of objects, e.g. [{\"Start\":\"01:30\",\"End\":\"05:45\",\"Threshold\":3800}]");
                var trng = new List<DmsOrderConfigPair.TimeRangeDto2>();
                foreach (var e in value.EnumerateArray())
                {
                    if (e.ValueKind != System.Text.Json.JsonValueKind.Object)
                        return (null, "Array elements must be objects like {\"Start\":\"01:30\",\"End\":\"05:45\",\"Threshold\":3800}");
                    var st = e.TryGetProperty("Start", out var sP) ? sP.GetString() : null;
                    var en = e.TryGetProperty("End", out var eP) ? eP.GetString() : null;
                    int th = 0;
                    var okTh = e.TryGetProperty("Threshold", out var tP) && tP.TryGetInt32(out th);
                    if (string.IsNullOrWhiteSpace(st) || string.IsNullOrWhiteSpace(en))
                        return (null, "Each range requires \"Start\" and \"End\" (HH:mm)");
                    if (!okTh || th < 0)
                        return (null, "Each range requires \"Threshold\" (integer >= 0)");
                    trng.Add(new DmsOrderConfigPair.TimeRangeDto2 { Start = st.Trim(), End = en.Trim(), Threshold = th });
                }
                return (trng, null);
            default:
                return (null, "Unknown configuration path (cannot validate value type)");
        }
    }

    // 生效方式的人类可读说明（API 响应用英文，WebUI 显示用前端本地映射）
    public static string Message(RestartLevel level) => level switch
    {
        RestartLevel.Hot => "Saved. Effective immediately (hot reload).",
        RestartLevel.InnerRestart => "Saved. Requires business-worker restart to take effect - use the 'Restart Service' button on the page, or POST /api/restart.",
        RestartLevel.ProcessRestart => "Saved. Requires a full service process restart (Linux: systemctl restart uniflow / Windows: Restart-Service UniFlow). The in-page restart button will NOT apply this change.",
        _ => ""
    };
}

// 校验用 DTO（与配置模型结构对齐；TimeRangeDto 带字符串时间便于 JSON 解析）
public static class DmsOrderConfigPair
{
    public class TriggerRuleDto { public string TestName { get; set; } = ""; public int TimeoutMinutes { get; set; } }
    public class TimeRangeDto2 { public string Start { get; set; } = ""; public string End { get; set; } = ""; public int Threshold { get; set; } }
}
