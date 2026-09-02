using Dapper;
using UniFlow.Common.Services;
using UniFlow.SrmExport.Models;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace UniFlow.SrmExport.Services;

public class ExportDatabaseService : IExportDatabaseService
{
    private readonly ILogger<ExportDatabaseService> _logger;
    private readonly MySqlConnectionFactory _factory;

    public ExportDatabaseService(ILogger<ExportDatabaseService> logger, MySqlConnectionFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

    private MySqlConnection NewConnection() => _factory.Create();

    public async Task<bool> PingAsync()
    {
        try
        {
            using var conn = NewConnection();
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
        using var conn = NewConnection();
        await conn.OpenAsync();
        var location = $"&3-{nodeId}-%";
        return (await conn.QueryAsync<DisposedSample>("""
            SELECT sample_id COLLATE latin1_swedish_ci AS Barcode,
                   t_location COLLATE latin1_swedish_ci AS Location,
                   update_time AS UpdateTime
            FROM t_sample
            WHERE update_time >= DATE_FORMAT(DATE_SUB(CURDATE(), INTERVAL 1 DAY),'%Y%m%d')
              AND update_time < DATE_FORMAT(CURDATE(),'%Y%m%d')
              AND t_location LIKE @Loc
            """, new { Loc = location })).AsList();
    }
}
