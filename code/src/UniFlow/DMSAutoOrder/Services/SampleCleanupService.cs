using System.Text;
using Microsoft.Extensions.Logging;
using UniFlow.Common.Models;
using UniFlow.Common.Services;

namespace UniFlow.DMSAutoOrder.Services;

public class SampleCleanupService
{
    private readonly ILogger<SampleCleanupService> _logger;
    private readonly IDmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly IAptioSocketClient _aptioSocket;
    private readonly IErrorReporter _errors;
    private List<string>? _deleteSqlTemplates;
    private DateTime _deleteSqlBuiltAt = DateTime.MinValue;
    private static readonly TimeSpan DeleteSqlRefreshInterval = TimeSpan.FromDays(1);
    private const string CR = "\r";
    private const string LF = "\n";

    // 超时计时基准：UniFlow 首次检测到该样本的时间（key = "{testName}\u0001{sid}"）。
    // 不用 reqtest.datrequest——那是 DMS/仪器侧写入的时间，可能不可靠（非插库时间、且 UniFlow 当时可能未启动）。
    // 仅内存态：进程重启后重新计时（保守，不会误删）。
    private readonly Dictionary<string, DateTime> _firstSeenUtc = new();

    // 删除回滚(affected=0)告警节流（M-08）：key = sid。
    // 删除失败会保留计时并在**每一轮**重试 → 不节流就是每轮一条 Warning（持续失败刷日志）。
    // 现在：同一 sid 的回滚告警最多 10 分钟一条；首次失败一定可见；
    // 累计失败到阈值（每 N 次）时**只记一条** Error（升级告警，不是每轮刷）。
    private readonly Dictionary<string, DateTime> _rollbackWarnAtUtc = new();
    private readonly Dictionary<string, int> _rollbackFailCount = new();

    /// <summary>同一 sid 的回滚告警最小间隔（10 分钟）。</summary>
    public static readonly TimeSpan RollbackWarnInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 连续回滚每累计到该次数的**倍数** → 升级记一条 Error（关键失败必须可见）。
    /// <para>第 10、20、30… 次各一条 —— **不是**每轮一条（否则 3s/条刷 Error，告警疲劳）。</para>
    /// </summary>
    public const int RollbackErrorEscalationCount = 10;

    private readonly Func<DateTime> _utcNow;

    public SampleCleanupService(
        ILogger<SampleCleanupService> logger,
        IDmsDatabaseService db,
        Models.DmsOrderConfig config,
        IAptioSocketClient aptioSocket,
        IErrorReporter errors,
        Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _db = db;
        _config = config;
        _aptioSocket = aptioSocket;
        _errors = errors;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// 观测用：当前仍在计时的样本 key 集合（key = "{testName}\u0001{sid}"）。
    /// <para>删除回滚时计时**必须保留**（下一轮重试）；删除成功才移除。</para>
    /// </summary>
    public IReadOnlyCollection<string> TrackedTimingKeys => _firstSeenUtc.Keys.ToList();

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await InitDeleteSqlAsync();
            _logger.LogDebug("InitDeleteSqlAsync done in {Ms}ms", sw.ElapsedMilliseconds);
            await ProcessTriggeredDeletionAsync();
            _logger.LogDebug("ProcessTriggeredDeletionAsync done in {Ms}ms (total {TotalMs}ms)",
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

        var nowUtc = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            var testName = rule.TestName;
            var timeoutMin = rule.TimeoutMinutes;
            var prefix = testName + "\u0001";

            // 查出该测试名下的全部样本（不按时间过滤——超时由 UniFlow 首次检测时间起算）
            try
            {
                var qSw = System.Diagnostics.Stopwatch.StartNew();
                var sids = await _db.GetTriggerSampleIdsAsync(testName);
                qSw.Stop();
                _logger.LogDebug("Trigger query for {Test} returned {Count} sid(s) in {Ms}ms",
                    testName, sids.Count, qSw.ElapsedMilliseconds);

                var current = new HashSet<string>(StringComparer.Ordinal);
                var toDelete = new List<string>();
                foreach (var sid in sids)
                {
                    if (string.IsNullOrEmpty(sid)) continue;
                    var key = prefix + sid;
                    current.Add(key);
                    if (!_firstSeenUtc.TryGetValue(key, out var firstSeen))
                    {
                        // 首次检测：记录起点，本轮不删
                        _firstSeenUtc[key] = nowUtc;
                        _logger.LogInformation("Trigger watch start: {Test} sid={Sid} (timeout {Timeout}min)",
                            testName, sid, timeoutMin);
                        continue;
                    }
                    if (nowUtc - firstSeen >= TimeSpan.FromMinutes(timeoutMin))
                        toDelete.Add(sid);
                }

                // 清理已消失（被删除/不再匹配）的计时项
                foreach (var key in _firstSeenUtc.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !current.Contains(k)).ToList())
                    _firstSeenUtc.Remove(key);

                if (toDelete.Count == 0) continue;

                var dSw = System.Diagnostics.Stopwatch.StartNew();
                long tOid = 0, tTests = 0, tDel = 0, tSend = 0;
                foreach (var sid in toDelete)
                {
                    var step = System.Diagnostics.Stopwatch.StartNew();
                    // 先通过 sid 获取 oid（reqtube），再删除关联记录（含 orders）
                    var oid = await _db.GetOidBySidAsync(sid);
                    tOid += step.ElapsedMilliseconds; step.Restart();
                    if (string.IsNullOrEmpty(oid))
                        _logger.LogWarning("No oid found in reqtube for sample {Sid}, orders may not be deleted", sid);
                    // 先取测试名（reqtest 删除后就查不到了），用于拼联动报文
                    var tests = await _db.GetTestsBySidAsync(sid);
                    tTests += step.ElapsedMilliseconds; step.Restart();
                    var affected = await _db.DeleteSampleAsync(sid, oid, _deleteSqlTemplates!);
                    tDel += step.ElapsedMilliseconds; step.Restart();
                    if (affected > 0)
                    {
                        // 删除确实生效 → 才发 Aptio 联动报文 + 清计时
                        await SendAptioActionAsync(sid, tests);
                        tSend += step.ElapsedMilliseconds;
                        var ageSec = _firstSeenUtc.TryGetValue(prefix + sid, out var fs)
                            ? (int)(nowUtc - fs).TotalSeconds : 0;
                        _firstSeenUtc.Remove(prefix + sid);
                        // 删除成功 → 清掉该 sid 的回滚节流/计数（下次再失败时"首次"重新可见）
                        _rollbackWarnAtUtc.Remove(sid);
                        _rollbackFailCount.Remove(sid);
                        _logger.LogInformation("Trigger-deleted sample {Sid} (oid={Oid}) for test {Test} after {Age}s, {Rows} row(s)",
                            sid, oid, testName, ageSec, affected);
                    }
                    else
                    {
                        // 删除回滚（或本就没有匹配行）→ DMS 行还在：
                        //   绝不能发 Aptio 报文（否则仪器侧已被取消/完成，而 DMS 数据仍在 → 状态不一致），
                        //   也**不能**清计时（否则下一轮会重新计时 → 永远到不了超时 → 样本永远不被清理）。
                        //   保留计时 → 下一轮继续重试删除。
                        // M-08：回滚告警按 sid 节流（默认 10 分钟一条）——失败会保留计时并每轮重试，
                        //   不节流就是每轮一条 Warning（持续失败刷日志）。
                        //   首次失败一定可见；累计失败每到 N 的倍数才记**一条** Error（不是每轮刷）。
                        LogRollbackFailure(sid, oid, testName);
                    }
                }
                dSw.Stop();
                _logger.LogDebug("Trigger deletion loop for {Test} done in {Ms}ms ({Count} sid(s)) | breakdown: getOid={Oid}ms getTests={Tests}ms delete={Del}ms send={Send}ms",
                    testName, dSw.ElapsedMilliseconds, toDelete.Count, tOid, tTests, tDel, tSend);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Trigger deletion error for {Test}: {Msg}", testName, ex.Message);
                _errors.Report("DmsCleanup", "ERROR", $"Trigger deletion error for {testName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 删除回滚（affected=0）的告警出口，按 sid 节流（M-08）。
    /// <para>
    /// 节流窗口内（默认 10 分钟）同一 sid 只记一条 Warning —— 首次失败一定可见。
    /// </para>
    /// <para>
    /// 升级 Error **只在累计失败次数正好是 <see cref="RollbackErrorEscalationCount"/> 的倍数时记一条**
    /// （默认第 10、20、30… 次各一条）。
    /// </para>
    /// <para>
    /// 历史缺陷（复核 D1）：<c>escalated = fails &gt;= 10</c> 且 <c>if (!throttled || escalated)</c>
    /// → 第 10 轮起**每轮**一条 Error（3s/条刷屏，告警疲劳）。
    /// </para>
    /// </summary>
    private void LogRollbackFailure(string sid, string oid, string testName)
    {
        var now = _utcNow();
        var fails = _rollbackFailCount.TryGetValue(sid, out var c) ? c + 1 : 1;
        _rollbackFailCount[sid] = fails;

        // 升级判定：**恰好**是阈值的倍数才算一次升级（用 >= 会导致每轮都升级）
        var escalationPoint = RollbackErrorEscalationCount > 0
            && fails % RollbackErrorEscalationCount == 0;
        var throttled = _rollbackWarnAtUtc.TryGetValue(sid, out var last) && now - last < RollbackWarnInterval;

        if (escalationPoint)
        {
            // 升级告警：每累计 N 次一条 Error（不被 Warning 节流吞掉，也不每轮刷）
            _rollbackWarnAtUtc[sid] = now;
            _logger.LogError("Trigger delete for {Sid} (oid={Oid}, test {Test}) affected 0 row(s) " +
                "{Fails} consecutive time(s) — delete keeps failing, skip Aptio action, keep timing for retry",
                sid, oid, testName, fails);
            return;
        }

        // 其余失败：按 10 分钟/sid 节流记 Warning（首次失败一定可见）
        if (!throttled)
        {
            _rollbackWarnAtUtc[sid] = now;
            _logger.LogWarning("Trigger delete for {Sid} (oid={Oid}) affected 0 row(s) " +
                "(rolled back or no matching rows) — skip Aptio action, keep timing for retry",
                sid, oid);
        }
    }
}
