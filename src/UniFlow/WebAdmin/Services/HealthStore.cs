using Microsoft.Data.Sqlite;

namespace UniFlow.WebAdmin.Services;

public class HealthStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<HealthStore> _logger;
    private readonly int _retentionDays;

    public HealthStore(string dbPath, ILogger<HealthStore> logger, int retentionDays = 30)
    {
        _logger = logger;
        _retentionDays = retentionDays;
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitDatabase();
    }

    private void InitDatabase()
    {
        using var cmd = _conn.CreateCommand();
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
            var cmd = _conn.CreateCommand();
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

    public async Task RecordErrorAsync(string module, string level, string message)
    {
        try
        {
            var cmd = _conn.CreateCommand();
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

        var cmd = _conn.CreateCommand();
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

        var cmd = _conn.CreateCommand();
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

    public async Task<Dictionary<string, object>> GetLatestModuleStatusAsync()
    {
        var result = new Dictionary<string, object>();
        var cmd = _conn.CreateCommand();
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
            result[reader.GetString(0)] = new Dictionary<string, object>
            {
                ["status"] = reader.GetString(1),
                ["timestamp"] = reader.GetString(2),
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
            else dn++;
        }
        return (h, d, dn);
    }

    public async Task CleanupOldAsync()
    {
        var cutoff = DateTime.Now.AddDays(-_retentionDays).ToString("yyyy-MM-dd HH:mm:ss");
        var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM health_records WHERE timestamp < @cut";
        cmd.Parameters.AddWithValue("@cut", cutoff);
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "DELETE FROM error_records WHERE timestamp < @cut";
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose() => _conn.Dispose();
}