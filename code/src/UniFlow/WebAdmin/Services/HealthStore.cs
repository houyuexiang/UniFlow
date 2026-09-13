using Microsoft.Data.Sqlite;
using UniFlow.Common.Services;

namespace UniFlow.WebAdmin.Services;

// 健康与错误存储。注意：连接非线程安全，且本类被外壳（API/收集器/健康检查）
// 与业务 Worker（经由外壳单例注入）并发使用——因此每次操作使用独立连接。
public class HealthStore : IDisposable
{
    private readonly string _connString;
    private readonly ILogger<HealthStore> _logger;
    private readonly int _retentionDays;
    private readonly ErrorReporter _reporter;

    public HealthStore(string dbPath, ILogger<HealthStore> logger, int retentionDays = 30, ErrorReporter? reporter = null)
    {
        _logger = logger;
        _retentionDays = retentionDays;
        _reporter = reporter ?? new ErrorReporter();
        _connString = $"Data Source={dbPath}";
        InitDatabase();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        return conn;
    }

    private void InitDatabase()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS health_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                module TEXT NOT NULL,
                status TEXT NOT NULL,
                message TEXT
            );
            CREATE TABLE IF NOT EXISTS error_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                module TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_health_ts ON health_records(timestamp);
            CREATE INDEX IF NOT EXISTS idx_error_ts ON error_records(timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task RecordHealthAsync(string module, string status, string? message = null)
    {
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO health_records (timestamp, module, status, message) VALUES (@ts, @m, @s, @msg)";
            cmd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@m", module);
            cmd.Parameters.AddWithValue("@s", status);
            cmd.Parameters.AddWithValue("@msg", message ?? "");
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Health record failed: {Msg}", ex.Message);
        }
    }

    // 入队：由专属 ErrorCollectorWorker 统一写库，避免业务线程阻塞
    public Task RecordErrorAsync(string module, string level, string message)
    {
        _reporter.Report(module, level, message);
        return Task.CompletedTask;
    }

    // 实际写入错误库（仅供 ErrorCollectorWorker 调用）
    public async Task WriteErrorAsync(string module, string level, string message)
    {
        try
        {
            using var conn = Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO error_records (timestamp, module, level, message) VALUES (@ts, @m, @l, @msg)";
            cmd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@m", module);
            cmd.Parameters.AddWithValue("@l", level);
            cmd.Parameters.AddWithValue("@msg", message);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error record failed: {Msg}", ex.Message);
        }
    }

    public async Task<List<Dictionary<string, object>>> GetHealthHistoryAsync(int days, string? module = null)
    {
        var results = new List<Dictionary<string, object>>();
        var cutoff = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
        var sql = "SELECT id, timestamp, module, status, message FROM health_records WHERE timestamp >= @cut";
        if (!string.IsNullOrEmpty(module)) sql += " AND module = @mod";
        sql += " ORDER BY timestamp DESC LIMIT 500";

        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@cut", cutoff);
        if (!string.IsNullOrEmpty(module)) cmd.Parameters.AddWithValue("@mod", module);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new Dictionary<string, object>
            {
                ["id"] = reader.GetInt64(0),
                ["timestamp"] = reader.GetString(1),
                ["module"] = reader.GetString(2),
                ["status"] = reader.GetString(3),
                ["message"] = reader.IsDBNull(4) ? "" : reader.GetString(4)
            });
        }
        return results;
    }

    public async Task<List<Dictionary<string, object>>> GetErrorsAsync(int days, string? module = null, string? level = null)
    {
        var results = new List<Dictionary<string, object>>();
        var cutoff = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
        var sql = "SELECT id, timestamp, module, level, message FROM error_records WHERE timestamp >= @cut";
        if (!string.IsNullOrEmpty(module)) sql += " AND module = @mod";
        if (!string.IsNullOrEmpty(level)) sql += " AND level = @lvl";
        sql += " ORDER BY timestamp DESC LIMIT 500";

        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@cut", cutoff);
        if (!string.IsNullOrEmpty(module)) cmd.Parameters.AddWithValue("@mod", module);
        if (!string.IsNullOrEmpty(level)) cmd.Parameters.AddWithValue("@lvl", level);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new Dictionary<string, object>
            {
                ["id"] = reader.GetInt64(0),
                ["timestamp"] = reader.GetString(1),
                ["module"] = reader.GetString(2),
                ["level"] = reader.GetString(3),
                ["message"] = reader.GetString(4)
            });
        }
        return results;
    }

    // 超过该时长未上报的模块视为已停止（如已停用的功能），不参与异常统计
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    public async Task<Dictionary<string, object>> GetLatestModuleStatusAsync()
    {
        var result = new Dictionary<string, object>();
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT h1.module, h1.status, h1.timestamp, h1.message
            FROM health_records h1
            INNER JOIN (
                SELECT module, MAX(timestamp) as max_ts
                FROM health_records GROUP BY module
            ) h2 ON h1.module = h2.module AND h1.timestamp = h2.max_ts
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var status = reader.GetString(1);
            var timestamp = reader.GetString(2);
            if (DateTime.TryParse(timestamp, out var t) && DateTime.Now - t > StaleThreshold)
                status = "stopped";
            result[reader.GetString(0)] = new Dictionary<string, object>
            {
                ["status"] = status,
                ["timestamp"] = timestamp,
                ["message"] = reader.IsDBNull(3) ? "" : reader.GetString(3)
            };
        }
        return result;
    }

    public async Task<(int healthy, int degraded, int down)> GetAggregatedStatusAsync()
    {
        var statuses = await GetLatestModuleStatusAsync();
        int h = 0, d = 0, dn = 0;
        foreach (var kv in statuses)
        {
            var s = ((Dictionary<string, object>)kv.Value)["status"]?.ToString();
            if (s == "healthy") h++;
            else if (s == "degraded") d++;
            else if (s != "stopped") dn++;   // stopped（已停用/陈旧）不计入异常
        }
        return (h, d, dn);
    }

    // 清空全部错误记录
    public async Task ClearErrorsAsync()
    {
        using var conn = Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM error_records";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task CleanupOldAsync()
    {
        using var conn = Open();
        // 按时间清理（保留 _retentionDays 天）
        var cutoff = DateTime.Now.AddDays(-_retentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM health_records WHERE timestamp < @cut";
        cmd.Parameters.AddWithValue("@cut", cutoff);
        await cmd.ExecuteNonQueryAsync();

        // 按数量封顶：每个模块最多保留 5000 条最新记录
        cmd.CommandText = """
            DELETE FROM health_records WHERE id IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY module ORDER BY id DESC) rn
                    FROM health_records
                ) t WHERE rn > 5000
            )
            """;
        await cmd.ExecuteNonQueryAsync();

        // 错误记录与 health_records 同保留期（_retentionDays 统一控制管理库记录保留）
        //（@cut 仍绑定上面的 cutoff，直接复用）
        cmd.CommandText = "DELETE FROM error_records WHERE timestamp < @cut";
        await cmd.ExecuteNonQueryAsync();
        cmd.Parameters.Clear();
    }

    public void Dispose() { /* 连接按操作创建，无共享句柄 */ }
}