using System.Text.Json;
using System.Text.Json.Nodes;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin;

/// <summary>测试用临时配置文件（基于 DefaultConfig，改动互不影响）。</summary>
internal static class TestConfigFile
{
    /// <summary>在临时目录生成一份 appsettings.json，返回其绝对路径。</summary>
    public static string Create(string? adminPassword = "Advia1", Action<JsonObject>? customize = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "uniflow-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appsettings.json");

        var node = JsonNode.Parse(JsonSerializer.Serialize(DefaultConfig.Build()))!.AsObject();
        node["WebAdmin"]!["AdminPassword"] = JsonValue.Create(adminPassword ?? "");
        customize?.Invoke(node);

        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>读取配置里的某个值（按 ':' 分隔）。</summary>
    private static JsonNode? FindNode(string configPath, string colonPath)
    {
        var node = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        JsonNode? cur = node;
        foreach (var seg in colonPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not JsonObject obj) return null;
            if (!obj.TryGetPropertyValue(seg, out cur)) return null;
        }
        return cur;
    }

    /// <summary>读取配置里的字符串值（按 ':' 分隔）。</summary>
    public static string? ReadString(string configPath, string colonPath)
        => FindNode(configPath, colonPath)?.GetValue<string>();

    /// <summary>读取配置里的布尔值（按 ':' 分隔）。</summary>
    public static bool? ReadBool(string configPath, string colonPath)
        => FindNode(configPath, colonPath)?.GetValue<bool>();

    /// <summary>读取配置里的整数值（按 ':' 分隔）。</summary>
    public static int? ReadInt(string configPath, string colonPath)
        => FindNode(configPath, colonPath)?.GetValue<int>();
}
