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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await InitDeleteSqlAsync();
            _logger.LogInformation("InitDeleteSqlAsync done in {Ms}ms", sw.ElapsedMilliseconds);
            await ProcessTriggeredDeletionAsync();
            _logger.LogInformation("ProcessTriggeredDeletionAsync done in {Ms}ms (total {TotalMs}ms)",
                sw.ElapsedMilliseconds, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sample cleanup error after {Ms}ms", sw.ElapsedMilliseconds);
            _errors.Report("DmsCleanup", "ERROR", ex.Message);
        }
    }

    // 按配置向 Aptio 发送联动报文（None=不发 / Cancel=ORDER C / Complete=S002 COMPLETE）
    private async Task SendAptioActionAsync(string sid, List<string> tests)
    {
        var mode = ResolveActionMode();
        if (mode == Models.AptioActionMode.None) return;
        try
        {
            // 换行符由 AptioSocketClient 发送层统一追加，勿自带 CR/LF 以免双换行
            var cmd = mode == Models.AptioActionMode.Cancel
                ? BuildCancelCommand(sid, tests)
                : BuildCompleteCommand(sid);
            if (!_aptioSocket.Connected)
                await _aptioSocket.ConnectAsync();
            await _aptioSocket.SendAsync(cmd);
            _logger.LogInformation("{Action} sent to Aptio for {Sid}: {Cmd}", mode, sid, cmd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("{Action} to Aptio failed for {Sid}: {Msg}", mode, sid, ex.Message);
        }
    }

    // AptioAction 未设置（null）时回退旧布尔开关 SendCancelMessageToAptio（向后兼容）
    private Models.AptioActionMode ResolveActionMode()
    {
        var mode = _config.SampleCleanup.AptioAction;
        if (mode.HasValue) return mode.Value;
        return _config.SampleCleanup.SendCancelMessageToAptio ? Models.AptioActionMode.Cancel : Models.AptioActionMode.None;
    }

    // 拼 ORDER 取消帧（DCAU 手册 ORDER 帧字段布局）：
    //   index 0  = Sample ID
    //   index 12 = Action-Code = C
    //   index 13-16 = Sample-Source 等（取消时留空）
    //   index 17,18,... = Test-Request-1, Test-Request-2, ...（每个测试一个顶层字段，| 分隔）
    internal static string BuildCancelCommand(string sid, IReadOnlyCollection<string> tests)
    {
        var fields = new List<string>(18) { "ORDER " + sid };   // index 0
        for (var i = 1; i <= 11; i++) fields.Add("");            // index 1-11 空
        fields.Add("C");                                          // index 12 = Action-Code
        for (var i = 13; i <= 16; i++) fields.Add("");            // index 13-16 空
        foreach (var t in tests) fields.Add(t);                   // index 17,18,... = Test-Request-i
        return string.Join("|", fields);
    }

    // 拼 S002 完成帧：COMMENT S002^<Sample-ID>\COMPLETE^S（全部测试完成）
    internal static string BuildCompleteCommand(string sid)
    {
        return $"COMMENT S002^{sid}\\COMPLETE^S";
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

            // 用索引友好的范围条件替代 TIMESTAMPDIFF(...) > N（后者无法使用 datrequest 索引，大表会全表扫描）
            // 语义等价：datrequest 距今超过 timeoutMin 分钟
            var cutoff = DateTime.Now.AddMinutes(-timeoutMin);
            var sql = $"SELECT DISTINCT codsid FROM {_config.DbName}.reqtest " +
                      $"WHERE codtest = @TestName AND datrequest < @Cutoff";
            try
            {
                var qSw = System.Diagnostics.Stopwatch.StartNew();
                using var conn = _db.NewConnection();
                var sids = (await conn.QueryAsync<string>(sql,
                    new { TestName = testName, Cutoff = cutoff })).ToList();
                qSw.Stop();
                _logger.LogInformation("Trigger query for {Test} returned {Count} sid(s) in {Ms}ms",
                    testName, sids.Count, qSw.ElapsedMilliseconds);
                if (sids.Count == 0) continue;

                var dSw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var sid in sids)
                {
                    if (string.IsNullOrEmpty(sid)) continue;
                    // 先通过 sid 获取 oid（reqtube），再删除关联记录（含 orders）
                    var oid = await _db.GetOidBySidAsync(sid);
                    if (string.IsNullOrEmpty(oid))
                        _logger.LogWarning("No oid found in reqtube for sample {Sid}, orders may not be deleted", sid);
                    // 先取测试名（reqtest 删除后就查不到了），用于拼联动报文
                    var tests = await _db.GetTestsBySidAsync(sid);
                    var affected = await _db.DeleteSampleAsync(sid, oid, _deleteSqlTemplates!);
                    await SendAptioActionAsync(sid, tests);
                    _logger.LogInformation("Trigger-deleted sample {Sid} (oid={Oid}) for test {Test}, {Rows} row(s)",
                        sid, oid, testName, affected);
                }
                dSw.Stop();
                _logger.LogInformation("Trigger deletion loop for {Test} done in {Ms}ms ({Count} sid(s))",
                    testName, dSw.ElapsedMilliseconds, sids.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Trigger deletion error for {Test}: {Msg}", testName, ex.Message);
                _errors.Report("DmsCleanup", "ERROR", $"Trigger deletion error for {testName}: {ex.Message}");
            }
        }
    }
}
