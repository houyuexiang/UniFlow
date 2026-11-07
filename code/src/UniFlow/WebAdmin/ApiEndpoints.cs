using System.IO.Compression;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using UniFlow.Common.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.WebAdmin;

public static class ApiEndpoints
{
    /// <summary>
    /// DB 不可达判定：连接失败 / 超时 / 网络错误（MySqlException、TimeoutException、SocketException、IOException，
    /// 含被包装在 InnerException 里的情形）。
    /// <para>其它异常（SQL 语法错、映射错等）**不算** DB 不可达 → 仍按 500 处理。</para>
    /// </summary>
    public static bool IsDatabaseUnavailable(Exception? ex)
    {
        if (ex == null) return false;
        if (ex is MySqlException or TimeoutException or SocketException or IOException) return true;
        return ex.InnerException != null && ex.InnerException != ex && IsDatabaseUnavailable(ex.InnerException);
    }

    /// <summary>
    /// DB 调用统一兜底（public static → 便于单测）：
    /// <list type="bullet">
    /// <item>DB 不可达（连接失败/超时）→ <b>503</b> {success:false, error:"Database unavailable"}（服务暂时不可用，可重试）；</item>
    /// <item>其它异常 → <b>500</b> {success:false, error:ex.Message}（不泄漏堆栈）。</li>
    /// </list>
    /// </summary>
    public static async Task<IResult> RunDisposeDbCallAsync(Func<Task<IResult>> dbCall)
    {
        try { return await dbCall(); }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            return Results.Json(new { success = false, error = "Database unavailable" }, statusCode: 503);
        }
        catch (Exception ex)
        {
            return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
        }
    }

    public static void Map(WebApplication app, HealthStore health, ConfigService config, RestartManager restart)
    {
        var api = app.MapGroup("/api");

        // ===== 管理鉴权（一次性 nonce + 哈希；每个破坏性动作独立鉴权）=====
        // 口径：前端 GET /api/auth/challenge 取一次性 nonce（60 秒有效、一次性），
        // 纯 JS 算 SHA-256(密码 + nonce) 十六进制小写，带 X-Admin-Nonce / X-Admin-Hash 两个头。
        // 管理密码未配置（空）→ 403；nonce/hash 缺失、过期或不匹配 → 401。
        // 需要鉴权的端点统一用 .AddEndpointFilter<AdminAuthFilter>() 挂载。

        // GET /api/auth/challenge —— 免鉴权，返回一次性 nonce
        api.MapGet("/auth/challenge", (AdminAuthService auth) =>
            Results.Ok(new
            {
                nonce = auth.CreateChallenge(),
                expiresInSeconds = AdminAuthService.NonceTtlSeconds
            }));

        // ===== Restart =====
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
        }).AddEndpointFilter<AdminAuthFilter>();

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
        }).AddEndpointFilter<AdminAuthFilter>();

        // 重启状态（前端轮询恢复用；LastFailure 非空表示上次重启失败、可重试）
        api.MapGet("/restart/status", (RestartManager rm) =>
            Results.Ok(new { ready = rm.IsReady, restarting = rm.IsRestarting, lastFailure = rm.LastFailure }));

        // ===== Dispose Queue Management =====

        // DB 调用统一兜底：ResendColumnReady 默认 true，但 worker 首轮 EnsureSamDisposeTableAsync
        // 之前表/列可能尚未就绪（或列缺失且属性尚未置 false），查询会抛异常；
        // 捕获后只回 ex.Message（不泄漏堆栈），保留端点各自的 503 前置分支。
        static async Task<IResult> RunDisposeDbAsync(Func<Task<IResult>> dbCall)
            => await RunDisposeDbCallAsync(dbCall);

        // GET /api/dispose/pending?minResend=24&limit=200
        api.MapGet("/dispose/pending", async (RestartManager rm, ConfigService config, int? minResend, int? limit) =>
        {
            var svc = rm.GetInnerService<IDisposeDatabaseService>();
            if (svc == null)
                return Results.Json(new { success = false, error = "Business host not ready" }, statusCode: 503);
            if (!svc.ResendColumnReady)
                return Results.Json(new { success = false, error = "sam_dispose_status.resend_count 缺失，自动重发与失败标记已停用" }, statusCode: 503);

            var min = Math.Clamp(minResend ?? 24, 0, 100000);
            var lim = Math.Clamp(limit ?? 200, 1, 500);

            var result = await RunDisposeDbAsync(async () =>
            {
                var records = await svc.GetPendingRecordsAsync(min, lim);
                var pendingSend = await svc.CountPendingSendAsync();
                var pendingConfirm = await svc.CountPendingConfirmAsync();
                var failedTotal = await svc.CountFailedAsync();

                int maxResendCount = 24;
                int resendIntervalMinutes = 5;    // 重排队检查间隔（分钟）；配置缺失时的兜底默认
                int windowSize = 10;   // MaxOnetimeSelectDiscardCount：同时是发送窗口（未丢弃总数上限）
                var mr = config.ReadPath("Aptio:DisposeSample:MaxResendCount");
                if (mr != null && mr.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    maxResendCount = mr.Value.GetInt32();
                var ri = config.ReadPath("Aptio:DisposeSample:ResendIntervalMinutes");
                if (ri != null && ri.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    resendIntervalMinutes = ri.Value.GetInt32();
                var ws = config.ReadPath("Aptio:DisposeSample:MaxOnetimeSelectDiscardCount");
                if (ws != null && ws.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    windowSize = ws.Value.GetInt32();

                return Results.Ok(new
                {
                    minResend = min,
                    count = records.Count,
                    pendingSend,
                    pendingConfirm,
                    failedTotal,
                    maxResendCount,
                    resendIntervalMinutes,
                    windowSize,
                    windowFull = windowSize > 0 && pendingConfirm >= windowSize,
                    items = records.Select(r => new
                    {
                        barcode = r.Barcode,
                        patient = r.Patient,
                        stype = r.Stype,
                        location = r.Location,
                        send = r.Send,
                        disposed = r.Disposed,
                        checkcount = r.Checkcount,
                        resendCount = r.ResendCount,
                        sendTime = r.SendTime,
                        disposedTime = r.DisposedTime
                    })
                });
            });
            return result;
        });

        // POST /api/dispose/pending/recheck
        api.MapPost("/dispose/pending/recheck", async (RestartManager rm, BarcodeBatchRequest req) =>
        {
            var svc = rm.GetInnerService<IDisposeDatabaseService>();
            if (svc == null)
                return Results.Json(new { success = false, error = "Business host not ready" }, statusCode: 503);
            if (!svc.ResendColumnReady)
                return Results.Json(new { success = false, error = "sam_dispose_status.resend_count 缺失，自动重发与失败标记已停用" }, statusCode: 503);

            var barcodes = (req.barcodes ?? Array.Empty<string>())
                .Select(b => b?.Trim()).Where(b => !string.IsNullOrEmpty(b)).Select(b => b!).Distinct().ToList();
            if (barcodes.Count == 0)
                return Results.BadRequest(new { success = false, error = "barcodes must not be empty" });
            if (barcodes.Count > 200)
                return Results.BadRequest(new { success = false, error = "barcodes must not exceed 200" });

            var result = await RunDisposeDbAsync(async () =>
            {
                var details = new List<object>();
                int disposedCount = 0;
                foreach (var bc in barcodes)
                {
                    var hit = await svc.RecheckRecordAsync(bc);
                    if (hit) disposedCount++;
                    details.Add(new { barcode = bc, disposed = hit });
                }
                return Results.Ok(new
                {
                    @checked = barcodes.Count,
                    disposed = disposedCount,
                    remaining = barcodes.Count - disposedCount,
                    details
                });
            });
            return result;
        });

        // POST /api/dispose/pending/reset
        api.MapPost("/dispose/pending/reset", async (RestartManager rm, BarcodeBatchRequest req) =>
        {
            var svc = rm.GetInnerService<IDisposeDatabaseService>();
            if (svc == null)
                return Results.Json(new { success = false, error = "Business host not ready" }, statusCode: 503);
            if (!svc.ResendColumnReady)
                return Results.Json(new { success = false, error = "sam_dispose_status.resend_count 缺失，自动重发与失败标记已停用" }, statusCode: 503);

            var barcodes = (req.barcodes ?? Array.Empty<string>())
                .Select(b => b?.Trim()).Where(b => !string.IsNullOrEmpty(b)).Select(b => b!).Distinct().ToList();
            if (barcodes.Count == 0)
                return Results.BadRequest(new { success = false, error = "barcodes must not be empty" });
            if (barcodes.Count > 200)
                return Results.BadRequest(new { success = false, error = "barcodes must not exceed 200" });

            var result = await RunDisposeDbAsync(async () =>
            {
                var reset = await svc.ResetRecordsAsync(barcodes);
                return Results.Ok(new { reset });
            });
            return result;
        }).AddEndpointFilter<AdminAuthFilter>();

        // POST /api/dispose/pending/delete
        api.MapPost("/dispose/pending/delete", async (RestartManager rm, BarcodeDeleteRequest req) =>
        {
            var svc = rm.GetInnerService<IDisposeDatabaseService>();
            if (svc == null)
                return Results.Json(new { success = false, error = "Business host not ready" }, statusCode: 503);
            if (!svc.ResendColumnReady)
                return Results.Json(new { success = false, error = "sam_dispose_status.resend_count 缺失，自动重发与失败标记已停用" }, statusCode: 503);

            if (req.confirm != true)
                return Results.BadRequest(new { success = false, error = "confirm=true required" });

            var barcodes = (req.barcodes ?? Array.Empty<string>())
                .Select(b => b?.Trim()).Where(b => !string.IsNullOrEmpty(b)).Select(b => b!).Distinct().ToList();
            if (barcodes.Count == 0)
                return Results.BadRequest(new { success = false, error = "barcodes must not be empty" });
            if (barcodes.Count > 200)
                return Results.BadRequest(new { success = false, error = "barcodes must not exceed 200" });

            var result = await RunDisposeDbAsync(async () =>
            {
                var deleted = await svc.DeleteRecordsAsync(barcodes);
                return Results.Ok(new { deleted });
            });
            return result;
        }).AddEndpointFilter<AdminAuthFilter>();

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
        }).AddEndpointFilter<AdminAuthFilter>();

        // ===== Config =====
        // 两个分支共用 ConfigRedactor（递归打码所有层级，含数组）：
        // 单 path 分支若只判断「path 自身是不是密码键」，?path=WebAdmin 会吐明文 AdminPassword（P0）。
        api.MapGet("/config", (string? path) =>
        {
            // ?path=Aptio:Port → 返回单项配置；否则返回全量
            if (string.IsNullOrWhiteSpace(path))
            {
                var cfg = config.ReadAll();
                return Results.Ok(ConfigRedactor.RedactDictionary(cfg));
            }
            var val = config.ReadPath(path);
            if (val == null)
                return Results.NotFound(new { success = false, error = $"Configuration path not found: '{path}'" });
            // path 自身就是密码键 → "***"；否则递归打码（父节点里的子密码键同样遮蔽）
            // 注：打码结果保持 JSON 类型（"***" 是合法 JSON 字符串，可直接序列化）
            object valueOut = ConfigRedactor.IsSecretKey(path)
                ? ConfigRedactor.Mask
                : ConfigRedactor.RedactElement(val.Value);
            return Results.Ok(new { path, value = valueOut });
        });

        // 配置文件下载（**原文**备份，含敏感字段）——需管理鉴权
        // 鉴权通过后返回完整原文（含数据库密码与管理密码），供现场备份/取证使用。
        api.MapGet("/config/download", () =>
        {
            var path = config.FilePath;
            if (!File.Exists(path)) return Results.NotFound("appsettings.json not found");
            return Results.File(File.ReadAllBytes(path), "application/json",
                $"appsettings-{DateTime.Now:yyyyMMddHHmmss}.json");
        }).AddEndpointFilter<AdminAuthFilter>();

        // PUT /api/config —— 单条保存（需管理鉴权）
        api.MapPut("/config", (ConfigUpdateRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { success = false, error = "Missing required field: path" });

            // 管理密码只能后台改 appsettings.json，API 不接受写入
            if (RestartLevelResolver.IsProtectedPath(req.Path))
                return Results.BadRequest(new { success = false, error = $"Configuration path '{req.Path}' cannot be changed via the API (edit appsettings.json instead)" });

            // 参数合法性校验：path 必须已知（防拼错/影子键），value 类型必须匹配模型属性
            var (level, known, kind) = RestartLevelResolver.ResolveWithKind(req.Path);
            if (!known && kind == "unknown")
                return Results.BadRequest(new { success = false, error = $"Unknown configuration path: '{req.Path}'. Valid paths are listed on the API Docs page." });
            if (req.Value == null || (req.Value is System.Text.Json.JsonElement jeN && jeN.ValueKind == System.Text.Json.JsonValueKind.Null))
                return Results.BadRequest(new { success = false, error = "Value must not be null" });

            // 段节点保护：不能把对象/数组节点替换成标量（否则配置结构损坏，如 Logging:LogLevel）
            var nodeError = ConfigWriteGuard.CheckNodeKind(
                req.Path, config.ReadPath(req.Path), (System.Text.Json.JsonElement)req.Value);
            if (nodeError != null)
                return Results.BadRequest(new { success = false, error = nodeError });

            // 密码字段：值是 "***" 或空串 → 保持现有值不变（页面回显的掩码不能覆盖真值）
            if (RestartLevelResolver.IsPasswordPath(req.Path)
                && req.Value is System.Text.Json.JsonElement jeMask
                && jeMask.ValueKind == System.Text.Json.JsonValueKind.String
                && (jeMask.GetString() == "***" || jeMask.GetString() == ""))
            {
                var restartKeep = new { level = level.ToString(), message = RestartLevelResolver.Message(level) };
                return Results.Ok(new { success = true, changed = false, restart = restartKeep });
            }

            var (normalized, error) = RestartLevelResolver.ValidateValue(kind, (System.Text.Json.JsonElement)req.Value);
            if (error != null)
                return Results.BadRequest(new { success = false, error });

            var ok = config.Update(req.Path, normalized ?? req.Value);
            if (!ok) return Results.Problem("Update failed", statusCode: 500);
            // 响应携带生效方式提示（模型 [Restart] 特性即真相）
            var restart = new { level = level.ToString(), message = RestartLevelResolver.Message(level) };
            return Results.Ok(new { success = true, changed = true, restart });
        }).AddEndpointFilter<AdminAuthFilter>();

        // PUT /api/config/batch —— 多字段一次保存（一次请求、一次鉴权）
        // 全部校验通过才写盘；任一非法 → 400 且**一个字段都不写**（不落半截状态）。
        // 密码字段规则：值为 "***" 或空串 → 保持现有值不变（不覆盖）。
        api.MapPut("/config/batch", (ConfigBatchRequest req) =>
        {
            var changes = req.Changes ?? new List<ConfigBatchChange>();
            if (changes.Count == 0)
                return Results.BadRequest(new { success = false, error = "Missing required field: changes" });
            if (changes.Count > 100)
                return Results.BadRequest(new { success = false, error = "changes must not exceed 100 items" });

            var applied = new List<(string Path, object? Value)>();
            var skipped = 0;
            var level = RestartLevel.Hot;

            // 段节点保护需要"当前值"：一次性读盘（不逐条读文件）
            var currentValues = config.ReadPaths(changes.Select(c => c.Path?.Trim() ?? "").Distinct().ToList());

            foreach (var ch in changes)
            {
                var path = ch.Path?.Trim();
                if (string.IsNullOrWhiteSpace(path))
                    return Results.BadRequest(new { success = false, error = "Missing required field: path" });

                // 管理密码只能后台改 appsettings.json
                if (RestartLevelResolver.IsProtectedPath(path))
                    return Results.BadRequest(new { success = false, error = $"Configuration path '{path}' cannot be changed via the API (edit appsettings.json instead)" });

                var (lvl, known, kind) = RestartLevelResolver.ResolveWithKind(path);
                if (!known && kind == "unknown")
                    return Results.BadRequest(new { success = false, error = $"Unknown configuration path: '{path}'. Valid paths are listed on the API Docs page." });

                // 密码字段：掩码/空串 → 保持原值（跳过，不计入写入数）
                if (RestartLevelResolver.IsPasswordPath(path) && IsMaskValue(ch.Value))
                {
                    skipped++;
                    continue;
                }

                if (ch.Value == null || (ch.Value is System.Text.Json.JsonElement jeNull && jeNull.ValueKind == System.Text.Json.JsonValueKind.Null))
                    return Results.BadRequest(new { success = false, error = $"Value must not be null (path: '{path}')" });

                var valueEl = ToElement(ch.Value);

                // 段节点保护：不能把对象/数组节点替换成标量（否则配置结构损坏）
                currentValues.TryGetValue(path, out var currentVal);
                var nodeError = ConfigWriteGuard.CheckNodeKind(path, currentVal, valueEl);
                if (nodeError != null)
                    return Results.BadRequest(new { success = false, error = nodeError });

                var (normalized, error) = RestartLevelResolver.ValidateValue(kind, valueEl);
                if (error != null)
                    return Results.BadRequest(new { success = false, error = $"{path}: {error}" });

                applied.Add((path, normalized ?? ch.Value));
                if (lvl > level) level = lvl;
            }

            if (applied.Count > 0)
            {
                var ok = config.UpdateBatch(applied);
                if (!ok) return Results.Problem("Update failed", statusCode: 500);
            }
            var restart = new { level = level.ToString(), message = RestartLevelResolver.Message(level) };
            return Results.Ok(new { success = true, count = applied.Count, skipped, restart });
        }).AddEndpointFilter<AdminAuthFilter>();

        // POST /api/config/test-db?target=aptio|dms —— 只测**已保存配置**的数据库连通性（免鉴权）
        // 不修改任何配置、不读取请求体里的新值；返回成功/失败/耗时/错误，错误信息不含密码。
        api.MapPost("/config/test-db", async ([FromQuery] string? target, DatabaseConnectionTester tester) =>
        {
            var t = (target ?? "aptio").Trim().ToLowerInvariant();
            if (t != "aptio" && t != "dms")
                return Results.BadRequest(new { success = false, error = "target must be 'aptio' or 'dms'" });

            var plan = DatabaseConnectionTester.BuildPlan(config, t);
            if (!plan.IsValid)
                return Results.Json(new { success = false, target = t, elapsedMs = 0, message = "Test failed: " + plan.Error },
                    statusCode: StatusCodes.Status400BadRequest);

            var result = await tester.TestAsync(plan);
            return Results.Ok(new
            {
                success = result.Success,
                target = result.Target,
                elapsedMs = result.ElapsedMs,
                message = result.Message
            });
        });

        // 值是否为密码掩码 "***" 或空串（→ 保持原值不变）
        static bool IsMaskValue(object? value)
        {
            if (value is not System.Text.Json.JsonElement je) return false;
            if (je.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            var s = je.GetString();
            return s == "***" || s == "";
        }

        // 把请求里的值统一成 JsonElement（object 反序列化后即为 JsonElement；兼容直接构造的对象）
        static System.Text.Json.JsonElement ToElement(object? value)
            => value is System.Text.Json.JsonElement je
                ? je
                : System.Text.Json.JsonSerializer.SerializeToElement(value);

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
        }).AddEndpointFilter<AdminAuthFilter>();

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
        }).AddEndpointFilter<AdminAuthFilter>();

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
    // 批量保存：{ "changes": [ { "path": "Aptio:Ip", "value": "..." } ] }
    public record ConfigBatchRequest(List<ConfigBatchChange>? Changes);
    public record ConfigBatchChange(string? Path, object? Value);
    public record BarcodeBatchRequest(string[]? barcodes);
    public record BarcodeDeleteRequest(string[]? barcodes, bool? confirm);
}