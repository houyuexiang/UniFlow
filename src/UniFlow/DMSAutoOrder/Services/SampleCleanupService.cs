using Microsoft.Extensions.Logging;

namespace UniFlow.DMSAutoOrder.Services;

public class SampleCleanupService
{
    private readonly ILogger<SampleCleanupService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private List<string>? _deleteSqlTemplates;

    public SampleCleanupService(
        ILogger<SampleCleanupService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config)
    {
        _logger = logger;
        _db = db;
        _config = config;
    }

    public async Task ExecuteAsync()
    {
        try
        {
            await InitDeleteSqlAsync();
            await ProcessTriggeredDeletionAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sample cleanup error");
        }
    }

    private async Task InitDeleteSqlAsync()
    {
        if (_deleteSqlTemplates != null) return;

        var tables = await _db.GetTableColumnsAsync("codsid");
        var templates = new List<string>();
        foreach (var t in tables)
            templates.Add($"DELETE FROM {_config.DbName}.{t} WHERE codsid = '{{0}}'");

        tables = await _db.GetTableColumnsAsync("codoid");
        foreach (var t in tables)
        {
            if (!templates.Any(s => s.Contains(t)))
                templates.Add($"DELETE FROM {_config.DbName}.{t} WHERE codoid = '{{1}}'");
            else
            {
                var idx = templates.FindIndex(s => s.Contains(t));
                templates[idx] = templates[idx].Replace("WHERE codsid = '{0}'",
                    "WHERE codsid = '{0}' AND codoid = '{1}'");
            }
        }

        _deleteSqlTemplates = templates;
    }

    private async Task ProcessTriggeredDeletionAsync()
    {
        if (string.IsNullOrEmpty(_config.TestTriggerSampleDeletion)) return;

        var tests = _config.TestTriggerSampleDeletion.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var test in tests)
        {
            var parts = test.Split(':');
            if (parts.Length < 2) continue;
            var testName = parts[0];
            var timeoutMin = parts[1];

            var sql = $"SELECT codsid, codoid FROM {_config.DbName}.reqtest " +
                      $"WHERE codtest = '{testName}' AND TIMESTAMPDIFF(MINUTE, datrequest, NOW()) > {timeoutMin}";
            var rows = await _db.GetUnknownPatientOrdersAsync(); // reuse query pattern
            // Simple approach: just find samples with the test older than timeout
            try
            {
                using var conn = new MySqlConnector.MySqlConnection(
                    $"Server={_config.DbHost};Port={_config.DbPort};Database={_config.DbName};" +
                    $"User={_config.DbUser};Password={_config.DbPassword};");
                await conn.OpenAsync();
                using var cmd = new MySqlConnector.MySqlCommand(sql, conn);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var sid = reader["codsid"].ToString();
                    var oid = reader["codoid"].ToString();
                    if (!string.IsNullOrEmpty(sid))
                    {
                        await _db.DeleteSampleAsync(sid, oid ?? "", _deleteSqlTemplates!);
                        _logger.LogInformation("Trigger-deleted sample {Sid} for test {Test}", sid, testName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Trigger deletion error for {Test}: {Msg}", testName, ex.Message);
            }
        }
    }
}
