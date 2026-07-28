using Dapper;
using UniFlow.Common.Services;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace UniFlow.DMSAutoOrder.Services;

public class DmsDatabaseService
{
    private readonly ILogger<DmsDatabaseService> _logger;
    private readonly MySqlConnectionFactory _factory;
    private readonly Models.DmsOrderConfig _config;

    public DmsDatabaseService(ILogger<DmsDatabaseService> logger, MySqlConnectionFactory factory, Models.DmsOrderConfig config)
    {
        _logger = logger;
        _factory = factory;
        _config = config;
    }

    public MySqlConnection NewConnection() => _factory.Create();

    public async Task PingAsync()
    {
        using var conn = NewConnection();
        await conn.PingAsync();
    }

    public async Task<List<string>> GetPitStopTablesAsync()
    {
        using var conn = NewConnection();
        var rows = await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = @Db AND table_name LIKE 'pitstop%'",
            new { Db = _config.DbName });
        return rows.ToList();
    }

    public async Task<List<Dictionary<string, object>>> GetRunningPitstopRecordsAsync(string tableName)
    {
        using var conn = NewConnection();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {_config.DbName}.{tableName} WHERE flgrunning <> ''");
        var result = new List<Dictionary<string, object>>();
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in row.GetType().GetProperties())
                dict[prop.Name] = prop.GetValue(row) ?? "";
            result.Add(dict);
        }
        return result;
    }

    public async Task DeletePitstopRecordsAsync(string tableName)
    {
        using var conn = NewConnection();
        await conn.ExecuteAsync($"DELETE FROM {_config.DbName}.{tableName} WHERE flgrunning <> ''");
    }

    public async Task<List<(string codsid, string codoid)>> GetUnknownPatientOrdersAsync()
    {
        using var conn = NewConnection();
        var sql = $"SELECT r.codsid, r.codoid FROM {_config.DbName}.reqtube r " +
                  $"WHERE r.codoid IN (SELECT o.codoid FROM {_config.DbName}.orders o, {_config.DbName}.anagpatient a " +
                  $"WHERE o.codaid = a.codaid AND a.codpid = 'UNKNOWN') " +
                  $"AND r.codsid IN (SELECT t.codsid FROM {_config.DbName}.reqtest t WHERE t.codtest <> 'UNKN') " +
                  $"OR r.codoid NOT IN (SELECT o.codoid FROM {_config.DbName}.orders o)";
        var rows = await conn.QueryAsync<(string, string)>(sql);
        return rows.ToList();
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        using var conn = NewConnection();
        await conn.ExecuteAsync(sql);
    }

    public async Task ExecuteSqlAsync(string sql, object parameters)
    {
        using var conn = NewConnection();
        await conn.ExecuteAsync(sql, parameters);
    }

    public async Task<List<string>> GetTableColumnsAsync(string columnName)
    {
        using var conn = NewConnection();
        return (await conn.QueryAsync<string>($@"
            SELECT DISTINCT table_name FROM information_schema.columns
            WHERE column_name = @Col AND table_schema = @Db
              AND table_name IN (SELECT table_name FROM information_schema.tables
              WHERE table_type = 'BASE TABLE' AND table_schema = @Db)
            ORDER BY table_name", new { Col = columnName, Db = _config.DbName })).ToList();
    }

    public async Task DeleteSampleAsync(string sid, string oid, List<string> deleteTableSqlTemplates)
    {
        using var conn = NewConnection();
        foreach (var template in deleteTableSqlTemplates)
        {
            var sql = string.Format(template, sid, oid);
            await conn.ExecuteAsync(sql);
        }
    }
}
