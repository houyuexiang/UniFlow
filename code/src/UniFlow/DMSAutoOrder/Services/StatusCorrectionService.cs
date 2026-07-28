using Microsoft.Extensions.Logging;

namespace UniFlow.DMSAutoOrder.Services;

public class StatusCorrectionService
{
    private readonly ILogger<StatusCorrectionService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;

    public StatusCorrectionService(
        ILogger<StatusCorrectionService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config)
    {
        _logger = logger;
        _db = db;
        _config = config;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            // Clean error/empty results
            await _db.ExecuteSqlAsync(
                $"DELETE FROM {_config.DbName}.reqtestresult WHERE flgstatus IN ('E','R','P') OR valresult1 = ''");

            if (_config.AutoModifyTestStatus == "1") return;

            if (_config.AutoModifyTestStatus == "2")
            {
                await _db.ExecuteSqlAsync(
                    $"UPDATE {_config.DbName}.reqtest SET flgtohost = 0 WHERE flgstatus IN ('V','X','Y','Z') AND flgtohost <> 0");
                await _db.ExecuteSqlAsync(
                    $"UPDATE {_config.DbName}.reqtest t, {_config.DbName}.reqtestresult r " +
                    $"SET t.flgtohost = 0, t.flgstatus = 'V' " +
                    $"WHERE t.codsid = r.codsid AND t.codtest = r.codtest " +
                    $"AND t.flgstatus <> r.flgstatus AND t.flgstatus = 'F'");
                _logger.LogInformation("Status correction mode 2 applied");
            }

            if (_config.AutoModifyTestStatus == "3")
            {
                await _db.ExecuteSqlAsync(
                    $"UPDATE {_config.DbName}.reqtestresult, {_config.DbName}.reqtest " +
                    $"SET reqtestresult.flgstatus = 'F' " +
                    $"WHERE reqtestresult.codsid = reqtest.codsid " +
                    $"AND reqtestresult.codtest = reqtest.codtest " +
                    $"AND reqtestresult.flgstatus IN ('V','X','Y','Z') " +
                    $"AND reqtest.flgtohost <> 0");
                await _db.ExecuteSqlAsync(
                    $"UPDATE {_config.DbName}.reqtest SET flgstatus = 'F' " +
                    $"WHERE flgstatus IN ('V','X','Y','Z') AND flgtohost <> 0");
                _logger.LogInformation("Status correction mode 3 applied");
            }

            // Handle IgnoreFlagList
            if (!string.IsNullOrEmpty(_config.IgnoreFlagList))
                await ProcessIgnoreFlagsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status correction error");
        }
    }

    private async Task ProcessIgnoreFlagsAsync()
    {
        var flags = _config.IgnoreFlagList.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var sql = $"SELECT codsid, codtest FROM {_config.DbName}.reqtestresult " +
                  $"WHERE flgstatus NOT IN ('F','V','X','Y','Z')";
        using var conn = _db.NewConnection();
        await conn.OpenAsync();
        using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var sid = reader["codsid"].ToString();
            var test = reader["codtest"].ToString();
if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(test))
                {
                    await _db.ExecuteSqlAsync(
                        $"UPDATE {_config.DbName}.reqtestresult SET flgstatus = 'V' WHERE codsid = @Sid AND codtest = @Test",
                        new { Sid = sid, Test = test });
                    await _db.ExecuteSqlAsync(
                        $"UPDATE {_config.DbName}.reqtest SET flgstatus = 'V', flgtohost = 0 WHERE codsid = @Sid AND codtest = @Test",
                        new { Sid = sid, Test = test });
                }
        }
    }
}
