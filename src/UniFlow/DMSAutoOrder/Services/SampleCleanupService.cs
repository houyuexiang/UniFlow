using System.Net.Sockets;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using UniFlow.Common.Models;

namespace UniFlow.DMSAutoOrder.Services;

public class SampleCleanupService
{
    private readonly ILogger<SampleCleanupService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly AptioConfig _aptio;
    private List<string>? _deleteSqlTemplates;
    private const string CR = "\r";
    private const string LF = "\n";

    public SampleCleanupService(
        ILogger<SampleCleanupService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config,
        AptioConfig aptio)
    {
        _logger = logger;
        _db = db;
        _config = config;
        _aptio = aptio;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
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

    private async Task SendCancelToAptioAsync(string sid)
    {
        if (!_config.SendCancelMessageToAptio) return;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_aptio.Ip, _aptio.Port);
            var stream = client.GetStream();
            var cmd = $"ORDER {sid}||||||||||||C|||||{CR}{LF}";
            var data = Encoding.ASCII.GetBytes(cmd);
            await stream.WriteAsync(data);
            _logger.LogInformation("Cancel sent to Aptio for {Sid}", sid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cancel to Aptio failed for {Sid}: {Msg}", sid, ex.Message);
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
                      $"WHERE codtest = @TestName AND TIMESTAMPDIFF(MINUTE, datrequest, NOW()) > @Timeout";
            try
            {
                using var conn = _db.NewConnection();
                var rows = await conn.QueryAsync<(string codsid, string codoid)>(sql,
                    new { TestName = testName, Timeout = int.Parse(timeoutMin) });
                foreach (var row in rows)
                {
                    var sid = row.codsid;
                    var oid = row.codoid;
                    if (!string.IsNullOrEmpty(sid))
                    {
                        await _db.DeleteSampleAsync(sid, oid ?? "", _deleteSqlTemplates!);
                        await SendCancelToAptioAsync(sid);
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
