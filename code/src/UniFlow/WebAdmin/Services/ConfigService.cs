using System.Text.Json;

namespace UniFlow.WebAdmin.Services;

public class ConfigService
{
    private readonly string _filePath;
    private readonly ILogger<ConfigService> _logger;
    private readonly object _lock = new();

    public ConfigService(ILogger<ConfigService> logger)
    {
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
        _logger = logger;
    }

    public Dictionary<string, object?> ReadAll()
    {
        lock (_lock)
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Read config failed: {Msg}", ex.Message);
                return new();
            }
        }
    }

    public bool Update(string jsonPath, object value)
    {
        lock (_lock)
        {
            try
            {
                var config = ReadAll();
                // SrmNodeIds 期望为字符串数组，统一格式处理
                if (jsonPath.EndsWith("SrmNodeIds", StringComparison.OrdinalIgnoreCase))
                    value = NormalizeSrmNodeIds(value);
                // 路径支持 : 和 . 两种分隔符（如 "Features:Dms.SampleCleanup"）
                var parts = jsonPath.Split(new[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
                SetNested(config, parts, value);
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(_filePath, JsonSerializer.Serialize(config, options));
                _logger.LogInformation("Config updated: {Path} = {Value}", jsonPath, value);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Update config failed: {Path} - {Msg}", jsonPath, ex.Message);
                return false;
            }
        }
    }

    private static object NormalizeSrmNodeIds(object value)
    {
        // 数字 -> ["13"]；字符串 -> ["13"]；数组 -> 保持字符串数组
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()).Cast<string>().ToList();
            return new List<string> { je.ValueKind == JsonValueKind.String ? je.GetString()! : je.ToString() };
        }
        if (value is System.Collections.IEnumerable enumerable and not string)
            return enumerable.Cast<object?>().Select(v => v?.ToString() ?? "").ToList();
        return new List<string> { value?.ToString() ?? "" };
    }

    private static void SetNested(Dictionary<string, object?> dict, string[] parts, object value)
    {
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!dict.ContainsKey(parts[i]) || dict[parts[i]] is not JsonElement)
            {
                dict[parts[i]] = new Dictionary<string, object?>();
            }
            var inner = dict[parts[i]];
            if (inner is JsonElement je)
            {
                var innerDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText()) ?? new();
                dict[parts[i]] = innerDict;
                inner = innerDict;
            }
            dict = (Dictionary<string, object?>)inner!;
        }
        dict[parts[^1]] = value;
    }
}