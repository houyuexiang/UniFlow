using Dapper;
using UniFlow.Common.Models;
using UniFlow.SrmExport.Models;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace UniFlow.SrmExport.Services;

public class ExportDatabaseService : IExportDatabaseService
{
    private readonly ILogger<ExportDatabaseService> _logger;
    private readonly DatabaseConfig _config;

    public ExportDatabaseService(ILogger<ExportDatabaseService> logger, DatabaseConfig config)
    {
        _logger = logger;
        _config = config;
    }

    private string ConnectionString =>
        $"Server={_config.Host};Port={_config.Port};Database={_config.Database};" +
        $"User={_config.User};Password={_config.Password};CharSet=latin1;AllowUserVariables=True;";

    public async Task<bool> PingAsync()
    {
        try
        {
            using var conn = new MySqlConnection(ConnectionString);
            await conn.OpenAsync();
            await conn.PingAsync();
            return true;
        }
        catch
        {
            _logger.LogWarning("Export DB ping failed");
            return false;
        }
    }

    public async Task<List<DisposedSample>> GetDisposedSamplesAsync(string nodeId)
    {
        using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();
        var location = $"&3-{nodeId}-%";
        return (await conn.QueryAsync<DisposedSample>("""
            SELECT sample_id COLLATE latin1_swedish_ci AS Barcode,
                   t_location COLLATE latin1_swedish_ci AS Location,
                   update_time AS UpdateTime
            FROM t_sample
            WHERE update_time >= DATE_FORMAT(curdate()-1,'%Y%m%d')
              AND update_time < DATE_FORMAT(curdate(),'%Y%m%d')
              AND t_location LIKE @Loc
            """, new { Loc = location })).AsList();
    }
}
