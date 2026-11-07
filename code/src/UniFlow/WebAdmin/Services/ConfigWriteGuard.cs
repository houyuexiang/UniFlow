using System.Text.Json;

namespace UniFlow.WebAdmin.Services;

/// <summary>
/// 配置写入保护：禁止把「段节点」（对象 / 数组）替换成标量，避免配置文件结构损坏。
/// <para>
/// 现象：<c>PUT /api/config {"path":"Logging:LogLevel","value":"Warning"}</c> 会把
/// <c>Logging:LogLevel</c>（对象，含 Default / UniFlow 等子键）整个替换成字符串，
/// 重启后子键全部丢失、配置结构损坏。
/// </para>
/// <para>
/// 正确做法：写**叶子路径**（<c>Logging:LogLevel:Default</c>）。
/// 本保护只拦「段节点 ← 标量 / 异类节点」；叶子之间的正常改值不受影响
/// （字符串 / 数字 / 布尔 / 数组字段照常可写）。
/// </para>
/// </summary>
public static class ConfigWriteGuard
{
    /// <summary>
    /// 段节点冲突检查：返回 <c>null</c> 表示允许写入；否则返回错误原因（含 path）。
    /// </summary>
    /// <param name="path">配置路径（冒号分隔）。</param>
    /// <param name="current">该路径**当前**的值（不存在 → null）。</param>
    /// <param name="newValue">待写入的新值。</param>
    public static string? CheckNodeKind(string path, JsonElement? current, JsonElement newValue)
    {
        if (current == null) return null;                 // 当前无此节点 → 新建，允许
        var cur = current.Value.ValueKind;
        if (cur is not (JsonValueKind.Object or JsonValueKind.Array))
            return null;                                  // 当前是叶子（标量）→ 由类型校验负责

        var nw = newValue.ValueKind;
        if (cur == nw) return null;                       // 对象→对象 / 数组→数组：结构不变，允许

        if (nw is JsonValueKind.Object or JsonValueKind.Array)
            return $"Configuration path '{path}' is an object/array node; cannot change its node type - edit a leaf path instead";

        return $"Configuration path '{path}' is an object/array node; " +
               "cannot replace an object/array node with a scalar; edit a leaf path instead";
    }
}
