using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using UniFlow.Common.Models;
using UniFlow.Common.Services;

namespace UniFlow.DMSAutoOrder.Services;

public class SampleCleanupService
{
    private readonly ILogger<SampleCleanupService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly IAptioSocketClient _aptioSocket;
    private readonly IErrorReporter _errors;
    private List<string>? _deleteSqlTemplates;
    private DateTime _deleteSqlBuiltAt = DateTime.MinValue;
    private static readonly TimeSpan DeleteSqlRefreshInterval = TimeSpan.FromDays(1);
    private const string CR = "\r";
    private const string LF = "\n";

    public SampleCleanupService(
        ILogger<SampleCleanupService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config,
        IAptioSocketClient aptioSocket,
        IErrorReporter errors)
    {
        _logger = logger;
        _db = db;
        _config = config;
        _aptioSocket = aptioSocket;
        _errors = errors;
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
            _errors.Report("DmsCleanup", "ERROR", ex.Message);
        }
    }

    private async Task SendCancelToAptioAsync(string sid)
    {
        if (!_config.SampleCleanup.SendCancelMessageToAptio) return;
        try
        {
            if (!_aptioSocket.Connected)
                await _aptioSocket.ConnectAsync();
            // 取消整个样本（换行符由 AptioSocketClient 发送层统一追加，勿自带 CR/LF 以免双换行）
            var cmd = $"ORDER {sid}||||||||||||C|||||";
            await _aptioSocket.SendAsync(cmd);
            _logger.LogInformation("Cancel sent to Aptio for {Sid}", sid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cancel to Aptio failed for {Sid}: {Msg}", sid, ex.Message);
        }
    }

    // 构建删除模板：动态扫描 information_schema，自动纳入含 codsid/codoid 列的所有表
    // 每 DeleteSqlRefreshInterval（默认 1 天）重建一次，自动适应数据库结构升级（新增表等）
    private async Task InitDeleteSqlAsync(bool force = false)
    {
        if (_deleteSqlTemplates != null && !force
            && DateTime.UtcNow - _deleteSqlBuiltAt < DeleteSqlRefreshInterval)
            return;

        var sidTables = await _db.GetTableColumnsAsync("codsid");
        var oidTables = await _db.GetTableColumnsAsync("codoid");

        // 按精确表名合并（避免 orders 与 orders_details 之类的子串误判）
        var templates = new List<string>();
        var allTables = sidTables.Concat(oidTables).Distinct().OrderBy(t => t, StringComparer.Ordinal);
        foreach (var t in allTables)
        {
            var conds = new List<string>();
            if (sidTables.Contains(t)) conds.Add("codsid = @sid");
            if (oidTables.Contains(t)) conds.Add("codoid = @oid");
            if (conds.Count == 0) continue;
            templates.Add($"DELETE FROM {_config.DbName}.{t} WHERE {string.Join(" AND ", conds)}");
        }

        var changed = _deleteSqlTemplates == null || _deleteSqlTemplates.Count != templates.Count;
        _deleteSqlTemplates = templates;
        _deleteSqlBuiltAt = DateTime.UtcNow;
        if (changed)
            _logger.LogInformation("Delete SQL templates built for {Count} tables", templates.Count);
    }

    private async Task ProcessTriggeredDeletionAsync()
    {
        // 闸门用统一入口（新 JSON 格式 TriggerRules 优先，旧分号串兜底），
        // 不能只判旧字段 TestTriggerSampleDeletion——表单保存只写新格式
        var rules = _config.SampleCleanup.GetRules();
        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            var testName = rule.TestName;
            var timeoutMin = rule.TimeoutMinutes;

            var sql = $"SELECT DISTINCT codsid FROM {_config.DbName}.reqtest " +
                      $"WHERE codtest = @TestName AND TIMESTAMPDIFF(MINUTE, datrequest, NOW()) > @Timeout";
            try
            {
                using var conn = _db.NewConnection();
                var sids = (await conn.QueryAsync<string>(sql,
                    new { TestName = testName, Timeout = timeoutMin })).ToList();
                if (sids.Count == 0) continue;

                foreach (var sid in sids)
                {
                    if (string.IsNullOrEmpty(sid)) continue;
                    // 先通过 sid 获取 oid（reqtube），再删除关联记录（含 orders）
                    var oid = await _db.GetOidBySidAsync(sid);
                    if (string.IsNullOrEmpty(oid))
                        _logger.LogWarning("No oid found in reqtube for sample {Sid}, orders may not be deleted", sid);
                    var affected = await _db.DeleteSampleAsync(sid, oid, _deleteSqlTemplates!);
                    await SendCancelToAptioAsync(sid);
                    _logger.LogInformation("Trigger-deleted sample {Sid} (oid={Oid}) for test {Test}, {Rows} row(s)",
                        sid, oid, testName, affected);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Trigger deletion error for {Test}: {Msg}", testName, ex.Message);
                _errors.Report("DmsCleanup", "ERROR", $"Trigger deletion error for {testName}: {ex.Message}");
            }
        }
    }
}
