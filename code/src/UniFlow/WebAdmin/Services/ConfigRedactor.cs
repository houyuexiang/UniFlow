using System.Text.Json;

namespace UniFlow.WebAdmin.Services;

/// <summary>
/// 配置打码器：递归遮蔽**所有层级**（对象 / 数组 / 字典）中 key 含 <c>Password</c> 或 <c>Pwd</c> 的值。
/// <para>
/// 为什么必须递归（P0）：<c>GET /api/config?path=&lt;父节点&gt;</c> 会把整段子对象返回，
/// 若只判断「path 自身是不是密码键」，则
/// <c>?path=WebAdmin</c> 会吐明文 <c>AdminPassword</c>、<c>?path=Aptio:Database</c> 会吐明文 <c>Password</c>、
/// <c>?path=DMS</c> 会吐明文 <c>DbPassword</c> —— 等于绕过管理鉴权直接泄露口令。
/// </para>
/// <para>
/// 全量分支（无 path）与单 path 分支**共用本类**，避免两套逻辑漂移。
/// 数组也会递归（旧实现不递归数组，数组内的 password 键会漏）。
/// </para>
/// </summary>
public static class ConfigRedactor
{
    /// <summary>打码后的占位符。</summary>
    public const string Mask = "***";

    /// <summary>敏感键判定：key 含 <c>Password</c> 或 <c>Pwd</c>（大小写不敏感）。</summary>
    public static bool IsSecretKey(string? key)
        => !string.IsNullOrEmpty(key)
           && (key.Contains("Password", StringComparison.OrdinalIgnoreCase)
               || key.Contains("Pwd", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 打码任意配置值：支持 <see cref="JsonElement"/>、<c>Dictionary&lt;string,object?&gt;</c>、
    /// 其它字典、数组/列表（<see cref="System.Collections.IEnumerable"/>）。
    /// 返回**新对象**，不修改入参。
    /// </summary>
    public static object? Redact(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement je:
                return RedactElement(je);
            case Dictionary<string, object?> dict:
                return RedactDictionary(dict);
            case IDictionary<string, object?> idict:
                {
                    var d = new Dictionary<string, object?>();
                    foreach (var kv in idict)
                        d[kv.Key] = IsSecretKey(kv.Key) ? Mask : Redact(kv.Value);
                    return d;
                }
            case System.Collections.IDictionary dict:
                {
                    var d = new Dictionary<string, object?>();
                    foreach (System.Collections.DictionaryEntry kv in dict)
                    {
                        var key = Convert.ToString(kv.Key) ?? "";
                        d[key] = IsSecretKey(key) ? Mask : Redact(kv.Value);
                    }
                    return d;
                }
            case string s:
                // string 实现 IEnumerable<char>，必须先于 IEnumerable 分支命中，
                // 否则字符串会被拆成字符数组（'a','b','c'）
                return s;
            case System.Collections.IEnumerable en:
                {
                    var list = new List<object?>();
                    foreach (var item in en)
                        list.Add(Redact(item));
                    return list;
                }
            default:
                return value;   // 标量（string/int/bool/null）原样保留
        }
    }

    /// <summary>打码字典（返回新字典，键序保持）。</summary>
    public static Dictionary<string, object?> RedactDictionary(Dictionary<string, object?> dict)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kv in dict)
            result[kv.Key] = IsSecretKey(kv.Key) ? Mask : Redact(kv.Value);
        return result;
    }

    /// <summary>
    /// 打码 <see cref="JsonElement"/>（保持 JSON 结构：对象仍是对象、数组仍是数组、标量原样）。
    /// </summary>
    public static JsonElement RedactElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var d = new Dictionary<string, object?>();
                    foreach (var prop in element.EnumerateObject())
                        d[prop.Name] = IsSecretKey(prop.Name) ? Mask : RedactElement(prop.Value);
                    return JsonSerializer.SerializeToElement(d);
                }
            case JsonValueKind.Array:
                {
                    var arr = new List<JsonElement>();
                    foreach (var item in element.EnumerateArray())
                        arr.Add(RedactElement(item));
                    return JsonSerializer.SerializeToElement(arr);
                }
            default:
                return element;   // string / number / bool / null 原样
        }
    }
}
