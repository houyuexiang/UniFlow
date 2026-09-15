using System.IO.Compression;
using UniFlow.Common.Models;
using UniFlow.WebAdmin.Services;

namespace UniFlow.WebAdmin;

public static class ApiEndpoints
{
    public static void Map(WebApplication app, HealthStore health, ConfigService config, RestartManager restart)
    {
        var api = app.MapGroup("/api");

        // ===== Restart =====
        // TODO(鉴权预留): 上 HTTPS 后在此校验 X-Api-Token 请求头，当前内网明文环境暂不启用。
        // POST /api/restart —— 内部重启：停止并按最新配置重建业务宿主，Web 不中断
        api.MapPost("/restart", (RestartManager rm) =>
        {
            if (rm.IsRestarting)
                return Results.Conflict(new { restarting = true, message = "Restart already in progress" });
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(1500); await rm.RestartAsync("web requested"); }
                catch (Exception ex) { Console.WriteLine($"[UniFlow] restart failed: {ex.Message}"); }
            });
            return Results.Accepted(value: new { restarting = true, level = "inner", hint = "Business workers rebuilding, ~2-5s (web keeps running)" });
        });

        // POST /api/restart/process —— 进程级整体重启：Exit(1) 由 systemd(on-failure)/Windows 服务恢复 自动拉起
        // 用于：升级新版生效、process 级配置生效（WebAdmin.BindIp/Port 等）
        api.MapPost("/restart/process", (RestartManager rm) =>
        {
            if (rm.IsRestarting)
                return Results.Conflict(new { restarting = true, message = "Restart already in progress" });
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(1500); await rm.RestartAsync("process restart requested"); }
                catch (Exception ex) { Console.WriteLine($"[UniFlow] process restart host-rebuild failed: {ex.Message}"); }
                Environment.Exit(1);
            });
            return Results.Accepted(value: new { restarting = true, level = "process", hint = "Process will exit and be auto-relaunched by service supervisor (~5s downtime)" });
        });

        // 重启状态（前端轮询恢复用；LastFailure 非空表示上次重启失败、可重试）
        api.MapGet("/restart/status", (RestartManager rm) =>
            Results.Ok(new { ready = rm.IsReady, restarting = rm.IsRestarting, lastFailure = rm.LastFailure }));

        // ===== Version =====
        api.MapGet("/version", () =>
        {
            var version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";
            var informational = typeof(ApiEndpoints).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? version;
            return Results.Ok(new { version, informational });
        });

        // ===== Health =====
        api.MapGet("/health", async () =>
        {
            var latest = await health.GetLatestModuleStatusAsync();
            var agg = await health.GetAggregatedStatusAsync();
            return Results.Ok(new
            {
                status = agg.down > 0 ? "degraded" : agg.degraded > 0 ? "degraded" : "healthy",
                modules = latest,
                summary = new { healthy = agg.healthy, degraded = agg.degraded, down = agg.down }
            });
        });

        api.MapGet("/health/history", async (int? days, string? module) =>
        {
            var records = await health.GetHealthHistoryAsync(days ?? 7, module);
            return Results.Ok(records);
        });

        // ===== Errors =====
        api.MapGet("/errors", async (int? days, string? module, string? level) =>
        {
            var records = await health.GetErrorsAsync(days ?? 7, module, level);
            return Results.Ok(records);
        });

        api.MapGet("/errors/summary", async (int? days) =>
        {
            var records = await health.GetErrorsAsync(days ?? 7);
            var byModule = records.GroupBy(r => r["module"]?.ToString() ?? "")
                .Select(g => new { module = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count);
            var byLevel = records.GroupBy(r => r["level"]?.ToString() ?? "")
                .Select(g => new { level = g.Key, count = g.Count() });
            return Results.Ok(new { total = records.Count, byModule, byLevel });
        });

        api.MapDelete("/errors", async () =>
        {
            await health.ClearErrorsAsync();
            return Results.Ok(new { success = true });
        });

        // ===== Config =====
        api.MapGet("/config", (string? path) =>
        {
            // ?path=Aptio:Port → 返回单项配置；否则返回全量
            if (string.IsNullOrWhiteSpace(path))
            {
                var cfg = config.ReadAll();
                RedactPasswords(cfg);
                return Results.Ok(cfg);
            }
            var val = config.ReadPath(path);
            if (val == null)
                return Results.NotFound(new { success = false, error = $"Configuration path not found: '{path}'" });
            // 密码键直接返回掩码字符串（"***" 不是合法 JSON，切勿再 Deserialize，否则 500）；非密码才反序列化保持类型
            object valueOut;
            if (path.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Pwd", StringComparison.OrdinalIgnoreCase))
                valueOut = "***";
            else
                valueOut = System.Text.Json.JsonSerializer.Deserialize<object>(val.Value.GetRawText())!;
            return Results.Ok(new { path, value = valueOut });
        });

        // ReadAll 用 STJ 反序列化 Dictionary<string,object?>，嵌套值是 JsonElement 而非 Dictionary，
        // 故必须展开 JsonElement.Object 才能触达深层 Password 键（否则打码是死代码）。
        static void RedactPasswords(Dictionary<string, object?> dict)
        {
            foreach (var key in dict.Keys.ToList())
            {
                var v = dict[key];
                if (v is Dictionary<string, object?> subDict)
                    RedactPasswords(subDict);
                else if (v is System.Text.Json.JsonElement je)
                {
                    if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText());
                        if (d != null) { RedactPasswords(d); dict[key] = d; }   // 必须写回，否则丢内容
                    }
                    else if (key.Contains("Password", StringComparison.OrdinalIgnoreCase)
                          || key.Contains("Pwd", StringComparison.OrdinalIgnoreCase))
                        dict[key] = "***";
                }
                else if (v is string && (key.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                      || key.Contains("Pwd", StringComparison.OrdinalIgnoreCase)))
                    dict[key] = "***";
            }
        }

        // 配置文件下载（原文备份，含敏感字段；内网环境，见鉴权预留说明）
        api.MapGet("/config/download", () =>
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return Results.NotFound("appsettings.json not found");
            return Results.File(File.ReadAllBytes(path), "application/json",
                $"appsettings-{DateTime.Now:yyyyMMddHHmmss}.json");
        });

        api.MapPut("/config", (ConfigUpdateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { success = false, error = "Missing required field: path" });

            // 参数合法性校验：path 必须已知（防拼错/影子键），value 类型必须匹配模型属性
            var (level, known, kind) = RestartLevelResolver.ResolveWithKind(req.Path);
            if (!known && kind == "unknown")
                return Results.BadRequest(new { success = false, error = $"Unknown configuration path: '{req.Path}'. Valid paths are listed on the API Docs page." });
            if (req.Value == null || (req.Value is System.Text.Json.JsonElement jeN && jeN.ValueKind == System.Text.Json.JsonValueKind.Null))
                return Results.BadRequest(new { success = false, error = "Value must not be null" });

            var (normalized, error) = RestartLevelResolver.ValidateValue(kind, (System.Text.Json.JsonElement)req.Value);
            if (error != null)
                return Results.BadRequest(new { success = false, error });

            var ok = config.Update(req.Path, normalized ?? req.Value);
            if (!ok) return Results.Problem("Update failed", statusCode: 500);
            // 响应携带生效方式提示（模型 [Restart] 特性即真相）
            var restart = new { level = level.ToString(), message = RestartLevelResolver.Message(level) };
            return Results.Ok(new { success = true, restart });
        });

        // ===== Log Files =====
        api.MapGet("/logs/list", (int? days) =>
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDir)) return Results.Ok(new List<object>());

            var cutoff = DateTime.Now.AddDays(-(days ?? 7));
            var files = Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime >= cutoff)
                .Select(f =>
                {
                    var rel = Path.GetRelativePath(logDir, f.FullName).Replace('\\', '/');
                    return new
                    {
                        path = rel,
                        name = f.Name,
                        date = f.Directory?.Name ?? "",
                        size = f.Length,
                        lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                    };
                })
                .OrderByDescending(f => f.lastModified)
                .ToList();
            return Results.Ok(files);
        });

        api.MapGet("/logs/view", (string file, int? tail) =>
        {
            if (string.IsNullOrWhiteSpace(file))
                return Results.Problem("Invalid log path", statusCode: 400);

            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            string full;
            try
            {
                // 防路径穿越: 必须解析到 logs 目录内
                full = Path.GetFullPath(Path.Combine(logDir, file));
            }
            catch
            {
                return Results.Problem("Invalid log path", statusCode: 400);
            }

            if (!full.StartsWith(Path.GetFullPath(logDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(Path.GetFullPath(logDir), StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Invalid log path", statusCode: 400);

            if (!File.Exists(full)) return Results.NotFound();

            try
            {
                // 使用 FileShare.ReadWrite 允许读取正在被日志 provider 写入的文件
                var lines = new List<string>();
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                        lines.Add(line);
                }
                if (tail.HasValue && tail.Value > 0)
                    lines = lines.TakeLast(tail.Value).ToList();
                return Results.Ok(new { lines, total = lines.Count });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Read log failed: {ex.Message}", statusCode: 500);
            }
        });

        // ===== 日志下载 =====
        // 单文件下载（路径校验同 logs/view，防穿越）
        api.MapGet("/logs/download", (string file) =>
        {
            if (string.IsNullOrWhiteSpace(file)) return Results.Problem("Invalid log path", statusCode: 400);
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            string full;
            try { full = Path.GetFullPath(Path.Combine(logDir, file)); }
            catch { return Results.Problem("Invalid log path", statusCode: 400); }
            if (!full.StartsWith(Path.GetFullPath(logDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(Path.GetFullPath(logDir), StringComparison.OrdinalIgnoreCase))
                return Results.Problem("Invalid log path", statusCode: 400);
            if (!File.Exists(full)) return Results.NotFound();
            try
            {
                // 日志文件可能正被文件日志 StreamWriter 占用（Windows 尤其常见），
                // 用 FileShare.ReadWrite 流式读取，避免共享冲突导致 500
                var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Results.File(stream, "application/octet-stream", Path.GetFileName(full));
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to read log file: {ex.Message}", statusCode: 500);
            }
        });

        // 打包下载：按天数筛选（可选按模块过滤）的全部日志（zip）
        api.MapGet("/logs/bundle", (int? days, string? module) =>
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDir)) return Results.NotFound("no logs directory");
            var cutoff = DateTime.Now.AddDays(-(days ?? 7));
            var files = Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime >= cutoff)
                .Where(f => string.IsNullOrEmpty(module)
                            || f.Name.StartsWith(module + "_", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (files.Count == 0) return Results.NotFound("no matching logs");

            var zipName = $"UniFlow-logs-{(string.IsNullOrEmpty(module) ? "all" : module)}-{DateTime.Now:yyyyMMddHHmmss}.zip";
            try
            {
                using var ms = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    foreach (var f in files)
                    {
                        var rel = Path.GetRelativePath(logDir, f.FullName).Replace('\\', '/');
                        // 日志文件可能正被文件日志 StreamWriter 占用（Windows 尤其常见），
                        // 用 FileShare.ReadWrite 读取，避免共享冲突导致 500
                        using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var entry = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                        using var es = entry.Open();
                        fs.CopyTo(es);
                    }
                }
                return Results.File(ms.ToArray(), "application/zip", zipName);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to bundle logs: {ex.Message}", statusCode: 500);
            }
        });

        // ===== Update =====
        // ① 上传新版本安装包
        api.MapPost("/update/upload", async (HttpRequest request) =>
        {
            if (request.ContentLength is not > 0)
                return Results.BadRequest(new { success = false, error = "Empty package" });

            var staging = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_staging");
            Directory.CreateDirectory(staging);
            // 清空旧 staging
            foreach (var f in Directory.GetFiles(staging)) File.Delete(f);

            var zipPath = Path.Combine(staging, "update.zip");
            using (var fs = File.Create(zipPath))
                await request.Body.CopyToAsync(fs);

            // 校验 zip 合法性
            var binaryName = OperatingSystem.IsWindows() ? "UniFlow.exe" : "UniFlow";
            List<string> entries;
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                entries = archive.Entries.Select(e => e.FullName).ToList();
                if (!entries.Any(e => e == binaryName || e.EndsWith("/" + binaryName)))
                    return Results.BadRequest(new { success = false, error = $"Package missing entry: {binaryName}" });
            }

            // 解压
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            // 兼容带单层包装目录的安装包（如官方 uniflow-*.tar.gz/zip 里的 uniflow/）：
            // 若主二进制位于唯一顶层目录下，则把该目录内容提升到 staging 根
            var topLevelDirs = Directory.GetDirectories(staging, "*", SearchOption.TopDirectoryOnly);
            if (!File.Exists(Path.Combine(staging, binaryName)) && topLevelDirs.Length == 1)
            {
                var sub = topLevelDirs[0];
                if (File.Exists(Path.Combine(sub, binaryName)))
                {
                    foreach (var dir in Directory.GetDirectories(sub, "*", SearchOption.TopDirectoryOnly))
                        Directory.Move(dir, Path.Combine(staging, Path.GetFileName(dir)));
                    foreach (var f in Directory.GetFiles(sub, "*", SearchOption.TopDirectoryOnly))
                        File.Move(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true);
                    Directory.Delete(sub, recursive: true);
                }
            }

            var md5Bytes = System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(zipPath));
            var md5 = Convert.ToHexString(md5Bytes).Replace("-", "").ToLowerInvariant();

            return Results.Ok(new
            {
                staged = true,
                files = entries,
                md5,
                hint = "Staged successfully. POST /api/update/apply to apply the upgrade."
            });
        });

        // ② 应用升级：rename-replace（无文件锁）+ wwwroot 覆盖 + appsettings 不动
        api.MapPost("/update/apply", () =>
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var staging = Path.Combine(baseDir, "update_staging");
            if (!Directory.Exists(staging))
                return Results.BadRequest(new { success = false, error = "No staged package. POST /api/update/upload first." });

            var binaryName = OperatingSystem.IsWindows() ? "UniFlow.exe" : "UniFlow";
            var stagedBinary = Path.Combine(staging, binaryName);
            if (!File.Exists(stagedBinary))
                return Results.BadRequest(new { success = false, error = $"Staged package missing {binaryName}" });

            var currentBinary = Path.Combine(baseDir, binaryName);
            var oldFile = currentBinary + ".old";
            var backupDir = Path.Combine(baseDir, "backup", DateTime.Now.ToString("yyyyMMddHHmmss"));

            // 备份当前主文件 + wwwroot
            Directory.CreateDirectory(backupDir);
            if (File.Exists(currentBinary))
                File.Copy(currentBinary, Path.Combine(backupDir, binaryName), overwrite: true);
            if (Directory.Exists(Path.Combine(baseDir, "wwwroot")))
            {
                foreach (var f in Directory.GetFiles(Path.Combine(baseDir, "wwwroot"), "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(Path.Combine(baseDir, "wwwroot"), f);
                    var dest = Path.Combine(backupDir, "wwwroot", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f, dest, overwrite: true);
                }
            }

            // rename-replace 主文件（Linux / Windows / macOS 通用）
            if (File.Exists(oldFile)) File.Delete(oldFile);
            if (File.Exists(currentBinary)) File.Move(currentBinary, oldFile);  // 运行中 exe 允许 rename
            File.Move(stagedBinary, currentBinary);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(currentBinary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            // wwwroot 直接覆盖（Kestrel 无锁）
            var stagedWww = Path.Combine(staging, "wwwroot");
            if (Directory.Exists(stagedWww))
            {
                var targetWww = Path.Combine(baseDir, "wwwroot");
                Directory.CreateDirectory(targetWww);
                foreach (var f in Directory.GetFiles(stagedWww, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(stagedWww, f);
                    var dest = Path.Combine(targetWww, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f, dest, overwrite: true);
                }
            }

            // appsettings.json 跳过（staging 根目录的 appsettings.json 不会被复制到 baseDir，自然跳过）

            // 清理 staging
            Directory.Delete(staging, recursive: true);

            return Results.Ok(new
            {
                applied = true,
                backup = backupDir,
                hint = "Upgrade applied. POST /api/restart to activate — the process will be relaunched by systemd (Linux) or Service Recovery (Windows)."
            });
        });

        // ④ 升级状态
        api.MapGet("/update/status", () =>
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var staging = Path.Combine(baseDir, "update_staging");
            var binaryName = OperatingSystem.IsWindows() ? "UniFlow.exe" : "UniFlow";
            var currentVersion = typeof(ApiEndpoints).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "unknown";
            return Results.Ok(new
            {
                staged = Directory.Exists(staging) && File.Exists(Path.Combine(staging, binaryName)),
                hasOldFile = File.Exists(Path.Combine(baseDir, binaryName + ".old")),
                currentVersion,
                backupAvailable = Directory.Exists(Path.Combine(baseDir, "backup")),
                moreInfo = "POST /api/update/upload to stage, then POST /api/update/apply, then POST /api/restart to activate"
            });
        });
    }

    public record ConfigUpdateRequest(string Path, object Value);
}