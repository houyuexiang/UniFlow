using UniFlow.Common.Models;
using UniFlow.WebAdmin.Services;

namespace UniFlow.WebAdmin;

public static class ApiEndpoints
{
    public static void Map(WebApplication app, HealthStore health, ConfigService config)
    {
        var api = app.MapGroup("/api");

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

        // ===== Config =====
        api.MapGet("/config", () =>
        {
            var cfg = config.ReadAll();
            RedactPasswords(cfg);
            return Results.Ok(cfg);
        });

        static void RedactPasswords(Dictionary<string, object?> dict)
        {
            foreach (var key in dict.Keys.ToList())
            {
                if (dict[key] is Dictionary<string, object?> subDict)
                    RedactPasswords(subDict);
                else if (key.Contains("Password", StringComparison.OrdinalIgnoreCase)
                      || key.Contains("Pwd", StringComparison.OrdinalIgnoreCase))
                    dict[key] = "***";
            }
        }

        api.MapPut("/config", (ConfigUpdateRequest req) =>
        {
            var ok = config.Update(req.Path, req.Value);
            return ok ? Results.Ok(new { success = true }) : Results.Problem("Update failed");
        });

        // ===== Log Files =====
        api.MapGet("/logs/list", (int? days) =>
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(logDir)) return Results.Ok(new List<object>());

            var cutoff = DateTime.Now.AddDays(-(days ?? 7));
            var files = Directory.GetFiles(logDir, "*.log")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime >= cutoff)
                .Select(f => new
                {
                    name = f.Name,
                    size = f.Length,
                    lastModified = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                })
                .OrderByDescending(f => f.lastModified)
                .ToList();
            return Results.Ok(files);
        });

        api.MapGet("/logs/view", (string file, int? tail) =>
        {
            var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            var path = Path.Combine(logDir, Path.GetFileName(file));
            if (!File.Exists(path)) return Results.NotFound();

            var lines = File.ReadAllLines(path).ToList();
            if (tail.HasValue && tail.Value > 0)
                lines = lines.TakeLast(tail.Value).ToList();
            return Results.Ok(new { lines, total = lines.Count });
        });
    }

    public record ConfigUpdateRequest(string Path, object Value);
}