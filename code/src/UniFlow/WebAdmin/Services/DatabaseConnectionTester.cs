using System.Diagnostics;
using MySqlConnector;

namespace UniFlow.WebAdmin.Services;

/// <summary>待测试的数据库连接参数（来自**已保存**的配置，绝不来自请求体）。</summary>
public sealed class DatabaseTestPlan
{
    public string Target { get; init; } = "aptio";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 3306;
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
    public string Database { get; init; } = "";
    /// <summary>非 null 表示配置缺失/非法，测试直接失败且不发起连接。</summary>
    public string? Error { get; init; }
    public bool IsValid => Error == null;
}

/// <summary>数据库连接测试结果（不含密码）。</summary>
public sealed class DatabaseTestResult
{
    public bool Success { get; init; }
    public string Target { get; init; } = "";
    public long ElapsedMs { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>连接探针（默认实现连 MySQL；测试可替换为假实现）。</summary>
public interface IDatabaseConnectionProbe
{
    Task ProbeAsync(DatabaseTestPlan plan, CancellationToken cancellationToken);
}

/// <summary>真实 MySQL 探针：5 秒连接超时，OpenAsync + PingAsync。</summary>
public sealed class MySqlDatabaseConnectionProbe : IDatabaseConnectionProbe
{
    public async Task ProbeAsync(DatabaseTestPlan plan, CancellationToken cancellationToken)
    {
        var csb = new MySqlConnectionStringBuilder
        {
            Server = plan.Host,
            Port = (uint)plan.Port,
            UserID = plan.User,
            Password = plan.Password,
            Database = plan.Database,
            ConnectionTimeout = 5,          // 连接超时 5 秒
            DefaultCommandTimeout = 5,
            AllowUserVariables = true,
        };
        await using var conn = new MySqlConnection(csb.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await conn.PingAsync(cancellationToken);
    }
}

/// <summary>
/// 数据库连接测试服务：只测**已保存配置**（appsettings.json），
/// 不修改任何配置、不读取请求体中的新值、错误消息不含密码。
/// </summary>
public sealed class DatabaseConnectionTester
{
    private readonly IDatabaseConnectionProbe _probe;

    public DatabaseConnectionTester(IDatabaseConnectionProbe probe)
    {
        _probe = probe;
    }

    /// <summary>从已保存配置里取出待测连接参数（Aptio / DMS）。</summary>
    public static DatabaseTestPlan BuildPlan(ConfigService config, string target)
    {
        target = (target ?? "").Trim().ToLowerInvariant();
        if (target != "aptio" && target != "dms")
            return new DatabaseTestPlan { Target = target, Error = "target must be 'aptio' or 'dms'" };

        string host, user, password, database;
        int port;
        if (target == "aptio")
        {
            host = ReadString(config, "Aptio:Database:Host");
            port = ReadInt(config, "Aptio:Database:Port") ?? 3306;
            user = ReadString(config, "Aptio:Database:User");
            password = ReadString(config, "Aptio:Database:Password");
            database = ReadString(config, "Aptio:Database:Database");
        }
        else
        {
            host = ReadString(config, "DMS:DbHost");
            port = ReadInt(config, "DMS:DbPort") ?? 3306;
            user = ReadString(config, "DMS:DbUser");
            password = ReadString(config, "DMS:DbPassword");
            database = ReadString(config, "DMS:DbName");
        }

        if (string.IsNullOrWhiteSpace(host))
            return new DatabaseTestPlan
            {
                Target = target,
                Error = target == "aptio"
                    ? "Saved configuration 'Aptio:Database:Host' is missing"
                    : "Saved configuration 'DMS:DbHost' is missing"
            };

        return new DatabaseTestPlan
        {
            Target = target,
            Host = host.Trim(),
            Port = port,
            User = user,
            Password = password,
            Database = database
        };
    }

    /// <summary>执行连接测试；失败时返回错误消息（已剔除密码）。</summary>
    public async Task<DatabaseTestResult> TestAsync(DatabaseTestPlan plan, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!plan.IsValid)
        {
            sw.Stop();
            return new DatabaseTestResult
            {
                Success = false,
                Target = plan.Target,
                ElapsedMs = sw.ElapsedMilliseconds,
                Message = "Test failed: " + plan.Error
            };
        }

        try
        {
            await _probe.ProbeAsync(plan, cancellationToken);
            sw.Stop();
            return new DatabaseTestResult
            {
                Success = true,
                Target = plan.Target,
                ElapsedMs = sw.ElapsedMilliseconds,
                Message = "Connection succeeded"
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            var safe = Sanitize(ex.Message, plan.Password);
            return new DatabaseTestResult
            {
                Success = false,
                Target = plan.Target,
                ElapsedMs = sw.ElapsedMilliseconds,
                Message = "Connection failed: " + safe
            };
        }
    }

    /// <summary>剔除消息中的密码（含连接串形态），保证错误信息可安全回显/记录。</summary>
    public static string Sanitize(string message, string? password)
    {
        var msg = message ?? "";
        if (!string.IsNullOrEmpty(password))
            msg = msg.Replace(password, "***", StringComparison.Ordinal);
        // 连接串形态：Password=xxx → Password=***
        // 注意：替换后文本里仍含 "Password="，必须从替换点之后继续搜，否则死循环
        var searchFrom = 0;
        while (true)
        {
            var idx = msg.IndexOf("Password=", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            var end = idx + "Password=".Length;
            var stop = end;
            while (stop < msg.Length && msg[stop] != ';' && msg[stop] != ' ' && msg[stop] != '\n' && msg[stop] != '\r')
                stop++;
            msg = msg[..end] + "***" + msg[stop..];
            searchFrom = end + 3;   // 跳过刚替换出来的 "***"
        }
        return msg;
    }

    private static string ReadString(ConfigService config, string path)
    {
        var v = config.ReadPath(path);
        if (v == null) return "";
        return v.Value.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.Value.GetString() ?? ""
            : v.Value.ToString();
    }

    private static int? ReadInt(ConfigService config, string path)
    {
        var v = config.ReadPath(path);
        if (v == null) return null;
        if (v.Value.ValueKind == System.Text.Json.JsonValueKind.Number && v.Value.TryGetInt32(out var n))
            return n;
        if (v.Value.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(v.Value.GetString(), out var n2))
            return n2;
        return null;
    }
}
