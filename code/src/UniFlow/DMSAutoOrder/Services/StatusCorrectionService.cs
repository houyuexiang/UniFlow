using Dapper;
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
            // 空结果清理已独立为 DMS.EmptyResultCleanup 功能（EmptyResultCleanupService）
            if (_config.StatusCorrection.AutoModifyTestStatus == "1") return;

            if (_config.StatusCorrection.AutoModifyTestStatus == "2")
            {
                var sql1 = $"UPDATE {_config.DbName}.reqtest SET flgtohost = 0 WHERE flgstatus IN ('V','X','Y','Z') AND flgtohost <> 0";
                var sql2 = $"UPDATE {_config.DbName}.reqtest t, {_config.DbName}.reqtestresult r " +
                           $"SET t.flgtohost = 0, t.flgstatus = 'V' " +
                           $"WHERE t.codsid = r.codsid AND t.codtest = r.codtest " +
                           $"AND t.flgstatus <> r.flgstatus AND t.flgstatus = 'F'";
                try
                {
                    var sids1 = await GetAffectedSidsAsync(
                        $"SELECT DISTINCT codsid FROM {_config.DbName}.reqtest WHERE flgstatus IN ('V','X','Y','Z') AND flgtohost <> 0");
                    var n1 = await _db.ExecuteSqlAsync(sql1);
                    if (n1 > 0)
                        _logger.LogInformation("Status correction mode 2 - reset flgtohost(V/X/Y/Z): Count={N}, Sids=[{Sids}]", n1, string.Join(",", sids1));

                    var sids2 = await GetAffectedSidsAsync(
                        $"SELECT DISTINCT t.codsid FROM {_config.DbName}.reqtest t, {_config.DbName}.reqtestresult r " +
                        $"WHERE t.codsid = r.codsid AND t.codtest = r.codtest " +
                        $"AND t.flgstatus <> r.flgstatus AND t.flgstatus = 'F'");
                    var n2 = await _db.ExecuteSqlAsync(sql2);
                    if (n2 > 0)
                        _logger.LogInformation("Status correction mode 2 - sync F to result status: Count={N}, Sids=[{Sids}]", n2, string.Join(",", sids2));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Status correction mode 2 failed: SQL={Sql}", $"{sql1}; {sql2}");
                }
            }

            if (_config.StatusCorrection.AutoModifyTestStatus == "3")
            {
                var sql1 = $"UPDATE {_config.DbName}.reqtestresult, {_config.DbName}.reqtest " +
                           $"SET reqtestresult.flgstatus = 'F' " +
                           $"WHERE reqtestresult.codsid = reqtest.codsid " +
                           $"AND reqtestresult.codtest = reqtest.codtest " +
                           $"AND reqtestresult.flgstatus IN ('V','X','Y','Z') " +
                           $"AND reqtest.flgtohost <> 0";
                var sql2 = $"UPDATE {_config.DbName}.reqtest SET flgstatus = 'F' " +
                           $"WHERE flgstatus IN ('V','X','Y','Z') AND flgtohost <> 0";
                try
                {
                    var sids1 = await GetAffectedSidsAsync(
                        $"SELECT DISTINCT reqtestresult.codsid FROM {_config.DbName}.reqtestresult, {_config.DbName}.reqtest " +
                        $"WHERE reqtestresult.codsid = reqtest.codsid " +
                        $"AND reqtestresult.codtest = reqtest.codtest " +
                        $"AND reqtestresult.flgstatus IN ('V','X','Y','Z') " +
                        $"AND reqtest.flgtohost <> 0");
                    var n1 = await _db.ExecuteSqlAsync(sql1);
                    if (n1 > 0)
                        _logger.LogInformation("Status correction mode 3 - force result to F: Count={N}, Sids=[{Sids}]", n1, string.Join(",", sids1));

                    var sids2 = await GetAffectedSidsAsync(
                        $"SELECT DISTINCT codsid FROM {_config.DbName}.reqtest WHERE flgstatus IN ('V','X','Y','Z') AND flgtohost <> 0");
                    var n2 = await _db.ExecuteSqlAsync(sql2);
                    if (n2 > 0)
                        _logger.LogInformation("Status correction mode 3 - force test to F: Count={N}, Sids=[{Sids}]", n2, string.Join(",", sids2));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Status correction mode 3 failed: SQL={Sql}", $"{sql1}; {sql2}");
                }
            }

            // Handle IgnoreFlagList
            if (_config.StatusCorrection.GetIgnoreFlags().Length > 0)
                await ProcessIgnoreFlagsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status correction error");
        }
    }

    private async Task<List<string>> GetAffectedSidsAsync(string selectSql)
    {
        try
        {
            using var conn = _db.NewConnection();
            return (await conn.QueryAsync<string>(selectSql)).AsList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Query affected sids failed: {Msg}, SQL={Sql}", ex.Message, selectSql);
            return new List<string>();
        }
    }

    private async Task ProcessIgnoreFlagsAsync()
    {
        var ignoreFlags = _config.StatusCorrection.GetIgnoreFlags();
        if (ignoreFlags.Length == 0) return;

        var sql = $"SELECT codsid, codtest, jsnflaginstrument FROM {_config.DbName}.reqtestresult " +
                  $"WHERE flgstatus NOT IN ('F','V','X','Y','Z')";
        using var conn = _db.NewConnection();
        await conn.OpenAsync();
        using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var sid = reader["codsid"].ToString();
            var test = reader["codtest"].ToString();
            var jsn = reader["jsnflaginstrument"]?.ToString() ?? "";

            // 解析 jsnflaginstrument JSON 里的 flags（如 ["flag1","flag2"]），命中 IgnoreFlagList 才处理
            if (!IsIgnoredFlag(jsn, ignoreFlags)) continue;
            if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(test)) continue;

            var sql1 = $"UPDATE {_config.DbName}.reqtestresult SET flgstatus = 'V' WHERE codsid = @Sid AND codtest = @Test";
            var sql2 = $"UPDATE {_config.DbName}.reqtest SET flgstatus = 'V', flgtohost = 0 WHERE codsid = @Sid AND codtest = @Test";
            try
            {
                await _db.ExecuteSqlAsync(sql1, new { Sid = sid, Test = test });
                await _db.ExecuteSqlAsync(sql2, new { Sid = sid, Test = test });
                _logger.LogInformation("Ignore-flag corrected: Sid={Sid}, Test={Test}", sid, test);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ignore-flag correction failed: Sid={Sid}, Test={Test}, SQL={Sql}", sid, test, $"{sql1}; {sql2}");
            }
        }
    }

    private static bool IsIgnoredFlag(string jsnflaginstrument, string[] ignoreFlags)
    {
        if (string.IsNullOrEmpty(jsnflaginstrument) || !jsnflaginstrument.StartsWith("[")) return false;
        // 去掉首尾 [] 和双引号，按逗号拆分
        var inner = jsnflaginstrument.Substring(1, jsnflaginstrument.Length - 2)
            .Replace("\"", "");
        var flags = inner.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var flag in flags)
        {
            if (ignoreFlags.Contains(flag.Trim()))
                return true;
        }
        return false;
    }
}
