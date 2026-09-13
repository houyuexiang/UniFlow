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
            // 文件损坏/不存在 → 备份坏文件 + 生成全新默认配置
            if (!File.Exists(_filePath))
            {
                _logger.LogWarning("appsettings.json not found, generating a fresh default config");
                var fresh = DefaultConfig.Build();
                SaveDictionary(fresh, _filePath);
                MigrateLegacyKeys(fresh);
                return fresh;
            }
            try
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
                if (loaded == null) throw new JsonException("deserialized to null");
                MigrateLegacyKeys(loaded);
                return loaded;
            }
            catch (Exception ex)
            {
                // JSON 损坏（手工编辑出错/写入中断）→ 备份坏文件，生成全新默认，业务可继续
                var backup = _filePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
                try { File.Copy(_filePath, backup, overwrite: true); } catch { }
                _logger.LogError(ex, "appsettings.json corrupted (backed up to {Backup}) — generating a fresh default config", backup);
                var fresh = DefaultConfig.Build();
                SaveDictionary(fresh, _filePath);
                MigrateLegacyKeys(fresh);
                return fresh;
            }
        }
    }

    // 旧版本配置结构自动迁移：旧键 → 新分组键，写入文件（一次性）
    private void MigrateLegacyKeys(Dictionary<string, object?> config)
    {
        var migrated = false;
        try
        {
            // 1. Features 旧平铺 → 新分组
            if (TryGetDict(config, "Features", out var f))
            {
                // AptioAutoProcess: true → DisposeSample+SrmExport
                if (PopBool(f, "AptioAutoProcess", out var aapOn))
                {
                    EnsureFlag(f, "Aptio:DisposeSample", aapOn);
                    EnsureFlag(f, "Aptio:SrmExport", aapOn);
                }
                // DmsAutoOrder: true → DMS 三+一
                if (PopBool(f, "DmsAutoOrder", out var daoOn))
                {
                    EnsureFlag(f, "Dms:StatusCorrection", daoOn);
                    EnsureFlag(f, "Dms:SampleCleanup", daoOn);
                    EnsureFlag(f, "Dms:EmptyResultCleanup", daoOn);
                }
                // 顶层平铺开关 → 分组
                var legacyMap = new (string Old, string New)[]
                {
                    ("Delivery", "Aptio:Delivery"), ("Priority", "Aptio:Priority"),
                    ("TestNameDispose", "Aptio:TestNameDispose"), ("ImmuliteWorkOrderClean", "Immulite:WorkListCleaner")
                };
                foreach (var (oldKey, newKey) in legacyMap)
                    if (PopFlag(f, oldKey, out var on)) EnsureFlag(f, newKey, on);

                config["Features"] = f;
                migrated = true;
            }

            // 2. AptioAutoProcess 嵌套段 → 新 Aptio 子节点
            if (TryGetDict(config, "AptioAutoProcess", out var aap))
            {
                if (!TryGetDict(config, "Aptio", out var aptio))
                {
                    aptio = new Dictionary<string, object?>();
                }
                CopyDict(aap, "Dispose", aptio, "DisposeSample");
                CopyDict(aap, "Deliver", aptio, "Delivery", ("TestName", "TestName"), ("DeliveryListFilePath", null));
                CopyDict(aap, "Priority", aptio, "Priority");
                CopyDict(aap, "Export", aptio, "SrmExport");
                // 旧 Dispose.DisposeTestName → 新 TestNameDispose.DisposeTestName
                if (TryGetDict(aap, "Dispose", out var dOld) && dOld.TryGetValue("DisposeTestName", out var dtn2))
                    FSet(aptio, "TestNameDispose:DisposeTestName", dtn2);

                config["Aptio"] = aptio;
                config.Remove("AptioAutoProcess");
                migrated = true;
            }

            // 3. DMS 旧平铺键 → 新子配置
            if (TryGetDict(config, "DMS", out var dms))
            {
                if (PopValue(dms, "EnablePitStopMonitor", out var epm))
                    FSet(dms, "PitStop:EnableMonitor", epm);
                if (PopValue(dms, "PitStopTimeoutMinutes", out var ptm))
                    FSet(dms, "PitStop:TimeoutMinutes", ptm);
                if (PopValue(dms, "AutoModifyTestStatus", out var ams) && !dms.ContainsKey("StatusCorrection"))
                {
                    dms["StatusCorrection"] = new Dictionary<string, object?> { ["AutoModifyTestStatus"] = ams, ["IgnoreFlagList"] = "" };
                }
                if (PopValue(dms, "TestTriggerSampleDeletion", out var ttsd) && !dms.ContainsKey("SampleCleanup"))
                {
                    dms["SampleCleanup"] = new Dictionary<string, object?> { ["TestTriggerSampleDeletion"] = ttsd, ["SendCancelMessageToAptio"] = false };
                }
                if (PopValue(dms, "DeleteTableList", out _))
                {
                    // DeleteTableList 已废弃（删除表列表动态生成），直接删除
                }
                config["DMS"] = dms;
                migrated = true;
            }

            // 4. ConfigVersion 补齐
            if (!config.ContainsKey("ConfigVersion") || config["ConfigVersion"]?.ToString() == "")
            {
                config["ConfigVersion"] = "1.0";
                migrated = true;
            }

            if (migrated)
            {
                SaveDictionary(config, _filePath);
                _logger.LogInformation("Legacy configuration migrated to current format (one-time)");
                _migratedFlag = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Config migration failed (non-fatal): {Msg}", ex.Message);
        }
    }

    private bool _migratedFlag;

    private static bool TryGetDict(Dictionary<string, object?> parent, string key, out Dictionary<string, object?>? value)
    {
        if (!parent.TryGetValue(key, out var v)) { value = null; return false; }
        if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
        { value = JsonToDict(je); return true; }
        value = v as Dictionary<string, object?>;
        return value != null;
    }

    private static bool PopBool(Dictionary<string, object?> dict, string key, out bool value)
    {
        value = false;
        if (!dict.TryGetValue(key, out var v)) return false;
        dict.Remove(key);
        var s = v?.ToString() ?? "";
        value = s == "True" || s == "true" || (int.TryParse(s, out var i) && i != 0);
        return true;
    }

    private static bool PopFlag(Dictionary<string, object?> dict, string key, out bool value)
        => PopBool(dict, key, out value);

    private static bool PopValue(Dictionary<string, object?> dict, string key, out object? value)
    {
        if (!dict.TryGetValue(key, out var v)) { value = null; return false; }
        dict.Remove(key);
        value = v;
        return true;
    }

    private static void EnsureFlag(Dictionary<string, object?> dict, string dotPath, bool on)
    {
        // 仅当目标未显式设置时才用旧值填充（新值优先）
        var parts = dotPath.Split(':');
        var cur = dict;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!cur.TryGetValue(parts[i], out var sub) || sub is not Dictionary<string, object?> sd)
            { sd = new Dictionary<string, object?>(); cur[parts[i]] = sd; }
            cur = sd;
        }
        var lastKey = parts[^1];
        if (!cur.ContainsKey(lastKey)) cur[lastKey] = on;
    }

    private static void FSet(Dictionary<string, object?> dict, string dotPath, object? value)
    {
        var parts = dotPath.Split(':');
        var cur = dict;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!cur.TryGetValue(parts[i], out var sub) || sub is not Dictionary<string, object?> sd)
            { sd = new Dictionary<string, object?>(); cur[parts[i]] = sd; }
            cur = sd;
        }
        cur[parts[^1]] = value;
    }

    private static void CopyDict(Dictionary<string, object?> from, string fromKey,
        Dictionary<string, object?> to, string toKey, params (string Old, string New)[] overrides)
    {
        if (!TryGetDict(from, fromKey, out var src)) return;
        if (!TryGetDict(to, toKey, out var dst))
        {
            dst = new Dictionary<string, object?>();
            to[toKey] = dst;
        }
        foreach (var kv in src)
        {
            var targetKey = kv.Key;
            foreach (var o in overrides)
                if (o.Old == kv.Key && !string.IsNullOrEmpty(o.New)) { targetKey = o.New; break; }
            FSet(to, toKey + ":" + targetKey, kv.Value);
        }
    }

    private static Dictionary<string, object?> JsonToDict(System.Text.Json.JsonElement je)
        => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText()) ?? new();

    private static void FEnsure(Dictionary<string, object?> dict, string dotPath, object? value)
    {
        var parts = dotPath.Split(':');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!dict.TryGetValue(parts[i], out var sub) || sub is not Dictionary<string, object?> sd)
            {
                sd = new Dictionary<string, object?>();
                dict[parts[i]] = sd;
            }
            dict = sd;
        }
        var lastKey = parts[^1];
        if (!dict.ContainsKey(lastKey) || dict[lastKey] is null) dict[lastKey] = value;
    }

    private static void SaveDictionary(Dictionary<string, object?> config, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, options));
        File.Move(tmp, path, overwrite: true);
    }

    public bool Update(string jsonPath, object value)
    {
        lock (_lock)
        {
            try
            {
                // 拒绝 null（数字字段被清空时，null 会让 ConfigurationBinder/STJ 产生 0 或解析异常，后果严重）
                if (value == null || (value is JsonElement je && je.ValueKind == JsonValueKind.Null))
                {
                    _logger.LogWarning("Update config rejected: {Path} = null", jsonPath);
                    return false;
                }
                var config = ReadAll();
                // SrmNodeIds 期望为字符串数组，统一格式处理
                if (jsonPath.EndsWith("SrmNodeIds", StringComparison.OrdinalIgnoreCase))
                    value = NormalizeSrmNodeIds(value);
                // 层级分隔符只认 ':'（.NET 配置约定）；键名可含 '.'（如 Microsoft.Hosting.Lifetime）
                var parts = jsonPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
                SetNested(config, parts, value);
                var options = new JsonSerializerOptions { WriteIndented = true };
                // 原子写：先写临时文件再替换，避免写一半崩溃导致配置文件损坏
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(config, options));
                File.Move(tmp, _filePath, overwrite: true);
                // 日志打码：路径含密码相关键时不记录值
                var safeValue = jsonPath.Contains("Password", StringComparison.OrdinalIgnoreCase)
                    || jsonPath.Contains("Pwd", StringComparison.OrdinalIgnoreCase) ? "***" : value;
                _logger.LogInformation("Config updated: {Path} = {Value}", jsonPath, safeValue);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Update config failed: {Path} - {Msg}", jsonPath, ex.Message);
                return false;
            }
        }
    }

    // 读取单项配置（按 ':' 逐级导航；未命中返回 null）
    public System.Text.Json.JsonElement? ReadPath(string jsonPath)
    {
        lock (_lock)
        {
            try
            {
                var config = ReadAll();
                var raw = System.Text.Json.JsonSerializer.Serialize(config);
                using var doc = System.Text.Json.JsonDocument.Parse(raw);
                var cur = doc.RootElement;
                foreach (var seg in jsonPath.Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (cur.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                    if (!cur.TryGetProperty(seg, out cur)) return null;
                }
                return cur.Clone();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReadPath failed: {Path} - {Msg}", jsonPath, ex.Message);
                return null;
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
            // 大小写不敏感查找既有键：命中则复用其实际键名（防 ip/Ip 影子键 → binder 重启抛 FormatException）
            var key = FindKeyIgnoreCase(dict, parts[i]);
            if (key == null) key = parts[i];
            var existing = dict[key];
            var isContainer = existing is Dictionary<string, object?>
                || (existing is JsonElement jeObj && jeObj.ValueKind == JsonValueKind.Object);
            if (!isContainer)
                dict[key] = new Dictionary<string, object?>();
            var inner = dict[key];
            if (inner is JsonElement je)
            {
                var innerDict = JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText()) ?? new();
                dict[key] = innerDict;
                inner = innerDict;
            }
            dict = (Dictionary<string, object?>)inner!;
        }
        // 末级同样大小写不敏感：命中既有键则复用其实际键名覆盖
        var lastKey = FindKeyIgnoreCase(dict, parts[^1]);
        dict[lastKey ?? parts[^1]] = value;
    }

    private static string? FindKeyIgnoreCase(Dictionary<string, object?> dict, string key)
    {
        foreach (var k in dict.Keys)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }
}
