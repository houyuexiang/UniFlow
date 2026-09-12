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
    private readonly IErrorReporter _errors;

    public DmsDatabaseService(ILogger<DmsDatabaseService> logger, MySqlConnectionFactory factory, Models.DmsOrderConfig config, IErrorReporter errors)
    {
        _logger = logger;
        _factory = factory;
        _config = config;
        _errors = errors;
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

    public async Task<int> ExecuteSqlAsync(string sql)
    {
        using var conn = NewConnection();
        return await conn.ExecuteAsync(sql);
    }

    public async Task<int> ExecuteSqlAsync(string sql, object parameters)
    {
        using var conn = NewConnection();
        return await conn.ExecuteAsync(sql, parameters);
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

    public async Task<int> DeleteSampleAsync(string sid, string oid, List<string> deleteTableSqlTemplates)
    {
        using var conn = NewConnection();
        var total = 0;
        var okTables = 0;
        var failedTables = 0;
        foreach (var template in deleteTableSqlTemplates)
        {
            var sql = template;
            try
            {
                var affected = await conn.ExecuteAsync(sql, new { sid, oid });
                okTables++;
                if (affected > 0)
                {
                    total += affected;
                    _logger.LogInformation("Delete rows: {Count} | {Sql}", affected, sql);
                }
            }
            catch (Exception ex)
            {
                failedTables++;
                _logger.LogError(ex, "Delete sample failed: Sid={Sid}, Oid={Oid}, SQL={Sql}", sid, oid, sql);
                _errors.Report("DmsCleanup", "ERROR", $"Delete sample failed: Sid={sid}, Oid={oid}, SQL={sql} | {ex.Message}");
            }
        }

        if (failedTables == 0)
            _logger.LogInformation("Sample deleted: Sid={Sid}, Oid={Oid}, TotalRows={Total}, Tables={Ok}",
                sid, oid, total, okTables);
        else
            _logger.LogWarning("Sample delete incomplete: Sid={Sid}, Oid={Oid}, TotalRows={Total}, OkTables={Ok}, FailedTables={Failed}",
                sid, oid, total, okTables, failedTables);
        return total;
    }

    public async Task<string> GetOidBySidAsync(string sid)
    {
        using var conn = NewConnection();
        var oid = await conn.QueryFirstOrDefaultAsync<string>(
            $"SELECT codoid FROM {_config.DbName}.reqtube " +
            $"WHERE codsid = @Sid AND codoid IS NOT NULL AND codoid <> '' LIMIT 1",
            new { Sid = sid });
        return oid ?? "";
    }
}
