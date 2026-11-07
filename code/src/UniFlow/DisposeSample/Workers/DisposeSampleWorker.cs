using System.Text.Json;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class DisposeSampleWorker : BackgroundService
{
    private readonly ILogger<DisposeSampleWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioCommandService _commands;
    private readonly SrmStatusDecoder _statusDecoder;
    private readonly HealthStore _health;
    private FeatureConfig _features = new();
    private AptioConfig _aptio = new();
    private AptioDisposeSampleConfig _dc = new();
    private HashSet<int> _runDays = new();
    private List<TimeRange> _timeRanges = new();
    private HashSet<string> _allowErrors = new();
    private SysStatus _state = SysStatus.None;
    private string? _preBarcode;
    private int _connectRetries;
    private bool _pauseReported;
    private string _lastCountSig = "";

    // 后期补查（确认丢弃）节流：距上次补查 >= 60 秒才真正查一次
    private DateTime _lastRecheckUtc = DateTime.MinValue;
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);
    // 补查轮转游标：只查 ID > cursor 的一批，查完（LastId=0）归零，保证尾部记录最终被覆盖
    private long _recheckCursor;
    // 重排队检查节流：间隔取 ResendIntervalMinutes（默认 5 分钟）；<=0 = 关闭重排队
    private DateTime _lastRequeueUtc = DateTime.MinValue;
    // 重排队单轮候选上限（一次最多重排队多少条；真正发送仍由状态机按窗口一条条发）
    private const int RequeueCandidateLimit = 200;
    // 重排队日志节流（窗口满 / 节点未就绪等）：每 10 分钟最多一次，避免刷屏
    private DateTime _lastRequeueLogUtc = DateTime.MinValue;
    private static readonly TimeSpan RequeueLogInterval = TimeSpan.FromMinutes(10);
    // 测试开关放行提示节流：每 10 分钟最多一条（便于现场确认 AllowStandbyDispose 生效）
    private DateTime _lastStandbyLogUtc = DateTime.MinValue;
    private static readonly TimeSpan StandbyLogInterval = TimeSpan.FromMinutes(10);
    // 重发次数用尽（resend_count >= MaxResendCount）时的失败候选上限
    private const int ExhaustedBatchLimit = 200;
    // 待处理队列告警节流：每 10 分钟最多一次
    private DateTime _lastPendingWarnUtc = DateTime.MinValue;
    private static readonly TimeSpan PendingWarnInterval = TimeSpan.FromMinutes(10);
    // resend_count 列不可用告警节流：每 10 分钟最多一次
    private DateTime _lastColumnWarnUtc = DateTime.MinValue;
    private static readonly TimeSpan ColumnWarnInterval = TimeSpan.FromMinutes(10);
    // 发送窗口日志降噪
    private int _lastWindowActive = -1;
    private int _lastWindowInFlight = -1;
    private DateTime _lastWindowLogUtc = DateTime.MinValue;
    private static readonly TimeSpan WindowLogInterval = TimeSpan.FromMinutes(10);
    // 待发数量上次值（仅用于日志降噪）
    private int _lastPendingSend = -1;

    public DisposeSampleWorker(
        ILogger<DisposeSampleWorker> logger,
        IAptioSocketClient socket,
        IDisposeDatabaseService db,
        AptioCommandService commands,
        SrmStatusDecoder statusDecoder,
        HealthStore health)
    {
        _logger = logger;
        _socket = socket;
        _db = db;
        _commands = commands;
        _statusDecoder = statusDecoder;
        _health = health;
        RefreshConfig();
    }

    private void RefreshConfig()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc == null) return;
            if (doc.TryGetValue("Features", out var fe)) _features = JsonSerializer.Deserialize<FeatureConfig>(fe.GetRawText()) ?? new();
            if (doc.TryGetValue("Aptio", out var ap))
            {
                // 解析失败保留上一次可用的配置（绝不静默回退出厂默认）
                try
                {
                    var parsed = JsonSerializer.Deserialize<AptioConfig>(ap.GetRawText());
                    if (parsed != null) _aptio = parsed;
                }
                catch (Exception ex) { _logger.LogError(ex, "Aptio config parse failed, keeping previous values: {Raw}", ap.GetRawText()); }
            }
            ApplyDerivedConfig();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisposeSample config reload failed, keeping previous values");
        }
    }

    // 供测试/外部显式注入配置（生产运行时由构造函数与 ExecuteAsync 循环经 RefreshConfig 从 appsettings.json 加载）
    public void LoadConfig(AptioConfig aptio, FeatureConfig? features = null)
    {
        _aptio = aptio;
        if (features != null) _features = features;
        ApplyDerivedConfig();
    }

    private void ApplyDerivedConfig()
    {
        _dc = _aptio.DisposeSample ?? new();
        // 统一入口：新 JSON 格式（RunDays/TimeRanges/AllowedSrmErrorCodes）优先，旧串兜底
        _runDays = _dc.GetRunDays();
        _timeRanges = _dc.GetTimeRanges();
        _allowErrors = _dc.GetAllowedErrors();
    }

    public SysStatus CurrentState { get => _state; set => _state = value; }
    public string? CurrentBarcode { get => _preBarcode; set => _preBarcode = value; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DisposeSample started, node={NodeId}, interval={Interval}s",
            _aptio.SrmNodeIds.FirstOrDefault() ?? "?", _dc.LoopIntervalSeconds);
        try { await Task.Delay(3000, ct); }
        catch (OperationCanceledException) { return; }

        // sam_dispose_status 表不存在时自动创建
        try { await _db.EnsureSamDisposeTableAsync(); }
        catch (Exception ex) { _logger.LogWarning("Ensure sam_dispose_status failed: {Msg}", ex.Message); }

        while (!ct.IsCancellationRequested)
        {
            RefreshConfig();
            if (!_features.Aptio.DisposeSample)
            {
                // 关闭时上报一次 stopped，立即从健康统计中排除
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("DisposeSample", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                if (!await EnsureConnectedAsync(ct))
                { await _health.RecordHealthAsync("DisposeSample", "degraded", "Aptio not connected"); await Task.Delay(5000, ct); continue; }

                if (!await _db.PingAsync())
                { await _health.RecordHealthAsync("DisposeSample", "degraded", "Database unavailable"); await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct); continue; }

                // 后期补查 + 到期重发 + 失败标记 + 队列告警（无论当前状态机处于哪个状态）
                try { await MaybeRecheckAsync(ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Dispose recheck failed"); }

                await TickAsync(ct);
                await _health.RecordHealthAsync("DisposeSample", "healthy");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Worker error"); try { await _health.RecordHealthAsync("DisposeSample", "degraded", ex.Message); await _health.RecordErrorAsync("DisposeSample", "ERROR", ex.Message); } catch { } }

            try { await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct); }
            catch (OperationCanceledException) { break; }
        }

        try { await _db.DeleteUnsendAsync(); _logger.LogInformation("DisposeSample stopped"); }
        catch { _logger.LogWarning("DisposeSample cleanup skipped"); }
    }

    /// <summary>
    /// 维护任务（60s 节流，无论状态机处于哪个状态都执行）：
    /// 1) 补查确认丢弃（轮转窗口，只查库，不发指令）；2) 重排队检查（每 ResendIntervalMinutes 分钟一轮，默认 5 分钟）；
    /// 3) 重发次数用尽 → 标记失败（send=3）；4) 待处理队列超阈值告警。
    /// </summary>
    public async Task<int> MaybeRecheckAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (_lastRecheckUtc != DateTime.MinValue && now - _lastRecheckUtc < RecheckInterval)
            return 0;
        _lastRecheckUtc = now;
        return await RunMaintenanceAsync(ct);
    }

    /// <summary>
    /// 维护任务本体（不做节流）：补查确认丢弃（轮转窗口）→ 重排队检查（5 分钟节流）→ 失败标记 → 队列告警。
    /// <see cref="MaybeRecheckAsync"/> 负责 60s 节流后调用本方法。
    /// </summary>
    public async Task<int> RunMaintenanceAsync(CancellationToken ct = default)
    {
        var hits = 0;
        try
        {
            // 1) 补查：确认「已投递但未观测到丢弃」的记录（轮转窗口：ID > 游标）
            //    只查库（&3-{node}-%），不发任何指令
            var (recheckHits, lastId) = await _db.RecheckPendingDisposalsAsync(200, _recheckCursor);
            _recheckCursor = lastId;          // lastId=0 → 游标归零，下轮从头
            hits = recheckHits;
            if (hits > 0)
                _logger.LogInformation("Dispose recheck confirmed {Hits} pending disposal(s)", hits);

            // 2) 重排队检查（每 ResendIntervalMinutes 分钟一轮，默认 5 分钟；本环节绝不直接下发丢弃命令）
            if (ShouldRunRequeue())
                await MaybeRequeueForResendAsync(ct);
        }
        finally
        {
            // 3) + 4) 失败标记与队列告警：只查 DB，不受重发超时影响，始终执行
            try { await MarkExhaustedAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Dispose exhausted-marking failed"); }
            try { await WarnPendingAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Dispose pending-queue check failed"); }
        }
        return hits;
    }

    /// <summary>
    /// 重排队检查是否该在本轮执行：
    /// - ResendIntervalMinutes &lt;= 0 → 关闭重排队；
    /// - 不在允许丢弃的时间段（RunDays/TimeRanges）→ 本轮不执行（只做 DB 补查），等下一个时间窗口；
    /// - 距上次重排队检查不足 ResendIntervalMinutes（默认 5 分钟）→ 本轮不执行。
    /// </summary>
    private bool ShouldRunRequeue()
    {
        var minutes = _dc.ResendIntervalMinutes;
        if (minutes <= 0) return false;                    // 0 = 关闭重排队
        var checker = new WorkTimeChecker();
        if (!checker.IsWorkTime(_runDays, _timeRanges, out _))
            return false;                                   // 不在允许丢弃的时间段 → 只查库，等下一个时间窗口
        var now = DateTime.UtcNow;
        if (_lastRequeueUtc != DateTime.MinValue && now - _lastRequeueUtc < TimeSpan.FromMinutes(minutes))
            return false;                                   // 5 分钟节流
        _lastRequeueUtc = now;
        return true;
    }

    /// <summary>
    /// 重排队（每 ResendIntervalMinutes 分钟一轮，默认 5 分钟）——<b>只重排队，绝不直接下发丢弃命令</b>：
    /// 1) 不在允许丢弃的时间段（RunDays/TimeRanges）→ 只查库，不发任何指令，等下一个时间窗口；
    /// 2) 发送窗口满（inFlight = send=1 &amp; disposed=0 达到 MaxOnetimeSelectDiscardCount）→ 等待，连 STATUS 都不发；
    /// 3) 窗口未满且在工作时间 → 发 <b>1 条</b> STATUS-REQUEST；
    /// 4) 只有该记录所在节点满足 Mode=ON + Status∈{G,Y} + ErrorCode 在允许列表 → 把记录放回待发队列（send=0）；
    ///    STB（Stand-by，carriers NOT processed / Automation Map OFF）/ GTO / PA / OFF / FR / SH / EXE 一律等待，不重排队；
    /// 5) 真正发送由正常状态机按发送窗口一条条发。
    /// </summary>
    public async Task<int> MaybeRequeueForResendAsync(CancellationToken ct = default)
    {
        // 1) 不在允许丢弃的时间段 → 只做 DB 补查，不发任何指令，等下一个时间窗口
        var checker = new WorkTimeChecker();
        if (!checker.IsWorkTime(_runDays, _timeRanges, out _))
        {
            LogRequeueSkip("outside allowed discard time window → wait for next window");
            return 0;
        }
        if (_dc.ResendIntervalMinutes <= 0) return 0;        // 0 = 关闭重排队
        if (!_db.ResendColumnReady) { WarnResendColumnNotReady(); return 0; }

        // 2) 发送窗口满 → 等待（不发任何指令，含 STATUS-REQUEST）
        var window = _dc.MaxOnetimeSelectDiscardCount;      // <=0 视为不限
        var inFlight = await _db.CountPendingConfirmAsync();
        if (window > 0 && inFlight >= window)
        {
            LogRequeueSkip($"dispose window full: inFlight={inFlight}/{window} → wait (no STATUS sent)");
            return 0;
        }

        // 3) 到期候选：send=1 & disposed=0 & send_time <= now-ResendIntervalMinutes & resend_count < MaxResendCount
        var cutoff = DateTime.Now.AddMinutes(-_dc.ResendIntervalMinutes).ToString("yyyyMMddHHmmss");
        var candidates = await _db.GetResendCandidatesAsync(cutoff, _dc.MaxResendCount, RequeueCandidateLimit);
        if (candidates == null || candidates.Count == 0) return 0;

        // 4) 只发 1 条 STATUS-REQUEST：用来判断各节点当前是否可丢弃
        var status = await _commands.SendStatusRequestAsync(ct);
        if (status == null || !status.StartsWith("STATUS"))
        {
            _logger.LogWarning("Resend requeue skipped: STATUS check failed: {Resp}", status);
            return 0;
        }

        // 5) 逐条判定：只有 ON + G/Y + 允许错误码 才重排队
        var requeue = new List<string>();
        var skipped = new Dictionary<string, (string Mode, int Count)>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.Barcode)) continue;
            var node = GetNodeIdFromLocation(c.Location);
            if (string.IsNullOrEmpty(node))
            {
                // 位置里取不到节点号 → 按「未知节点」处理
                if (_dc.SkipOnUnknownNode)
                {
                    _logger.LogWarning("Resend requeue: record {Barcode} has no node in location → skip (send=2)", c.Barcode);
                    var skipOk = await _db.UpdateStatusAsync(c.Barcode!, "skip");
                    if (!skipOk)
                        await ReportStatusUpdateFailureAsync("skip", c.Barcode!,
                            "record stays at queue head (send=0) and will be retried");
                }
                else
                {
                    BumpSkipped(skipped, "(unknown)", "");
                }
                continue;
            }
            if (!_statusDecoder.IsNodeReady(status, node, _allowErrors, _dc.AllowStandbyDispose))
            {
                // 非 ON（如 STB/GTO/PA/OFF/FR/SH/EXE）或状态非 G/Y（或开关关闭时的 P）→ 一律等待，不重排队
                BumpSkipped(skipped, node, _statusDecoder.GetNodeMode(status, node));
                continue;
            }
            // 测试开关放行提示（仅当 AllowStandbyDispose=true 且该节点是 STB / Pink 时打印）
            LogStandbyAllowedIfAny(status, node);
            requeue.Add(c.Barcode!);
        }

        LogSkippedNodes(skipped);
        if (requeue.Count == 0) return 0;

        // 6) 放回待发队列（send=0）；真正发送由正常状态机按窗口一条条发
        var affected = await _db.RequeueForResendAsync(requeue);
        _logger.LogInformation(
            "Requeued {Count} record(s) for resend (window inFlight={InFlight}/{Window}); state machine will send one by one",
            affected, inFlight, window > 0 ? window.ToString() : "unlimited");
        return affected;
    }

    // 记录被跳过的节点及其 Mode（用于降噪日志：node 09 mode=STB → wait）
    private static void BumpSkipped(Dictionary<string, (string Mode, int Count)> skipped, string node, string mode)
    {
        skipped[node] = skipped.TryGetValue(node, out var cur)
            ? (string.IsNullOrEmpty(cur.Mode) ? mode : cur.Mode, cur.Count + 1)
            : (mode, 1);
    }

    // 跳过原因日志（10 分钟节流 + 数量降噪）
    private void LogSkippedNodes(Dictionary<string, (string Mode, int Count)> skipped)
    {
        if (skipped.Count == 0) return;
        var parts = skipped.Select(kv =>
            $"node {kv.Key} mode={(string.IsNullOrEmpty(kv.Value.Mode) ? "?" : kv.Value.Mode)} → wait ({kv.Value.Count} record(s))");
        LogRequeueSkip(string.Join("; ", parts));
    }

    private void LogRequeueSkip(string reason)
    {
        var now = DateTime.UtcNow;
        if (_lastRequeueLogUtc != DateTime.MinValue && now - _lastRequeueLogUtc < RequeueLogInterval) return;
        _lastRequeueLogUtc = now;
        _logger.LogInformation("Resend requeue: {Reason}", reason);
    }

    /// <summary>
    /// 测试开关放行提示：<c>AllowStandbyDispose=true</c> 时，STB（Stand-by）/ Pink(P) 被允许参与丢弃判断。
    /// 开关关闭或节点是普通 ON/G、Y 时不打印；每 10 分钟最多一条（降噪）。
    /// </summary>
    private void LogStandbyAllowedIfAny(string statusMessage, string nodeId)
    {
        if (!_dc.AllowStandbyDispose) return;
        var node = _statusDecoder.FindNode(statusMessage, nodeId);
        if (node == null) return;
        if (node.Mode != "STB" && node.Status != "P") return;   // 普通 ON + G/Y 不需要提示
        var now = DateTime.UtcNow;
        if (_lastStandbyLogUtc != DateTime.MinValue && now - _lastStandbyLogUtc < StandbyLogInterval) return;
        _lastStandbyLogUtc = now;
        _logger.LogInformation(
            "STB/P allowed by AllowStandbyDispose=true (node {Node} mode={Mode} status={Status} error={Error})",
            nodeId, node.Mode, node.Status, node.Error);
    }

    // 重发次数已用尽（resend_count >= MaxResendCount）→ 标记失败 send=3，交由页面人工处置
    private async Task<int> MarkExhaustedAsync()
    {
        if (_dc.MaxResendCount <= 0) return 0;          // 0 = 不限重发次数
        if (!_db.ResendColumnReady) { WarnResendColumnNotReady(); return 0; }
        var exhausted = await _db.GetExhaustedCandidatesAsync(_dc.MaxResendCount, ExhaustedBatchLimit);
        if (exhausted == null || exhausted.Count == 0) return 0;
        var barcodes = exhausted.Select(e => e.Barcode)
            .Where(b => !string.IsNullOrEmpty(b)).Select(b => b!).Distinct().ToList();
        if (barcodes.Count == 0) return 0;

        var marked = await _db.MarkFailedAsync(barcodes);
        // 只记数量与示例条码，避免 200 条拼成超长日志
        var sample = string.Join(",", barcodes.Take(5));
        _logger.LogError("Dispose failed after {Max} resend(s): {Count} record(s) marked failed (send=3), e.g. {Samples}",
            _dc.MaxResendCount, marked, sample);
        try
        {
            await _health.RecordErrorAsync("DisposeSample", "ERROR",
                $"Dispose failed after {_dc.MaxResendCount} resend(s): {marked} record(s) marked as send=3, e.g. {sample}");
        }
        catch { /* 健康/错误记录失败不影响主循环 */ }
        return marked;
    }

    /// <summary>
    /// DB 状态写入失败的上报：**记 Error 日志 + 记健康错误（degraded）**，绝不静默。
    /// <para>
    /// 语义：DB 未推进（记录仍是 send=0 / disposed=0），调用方据此**不推进状态机**，
    /// 下一轮重试；extra 说明本次失败的业务后果（例如"命令已投递但 DB 未更新 → 可能重复下发"）。
    /// </para>
    /// </summary>
    private async Task ReportStatusUpdateFailureAsync(string field, string barcode, string extra)
    {
        _logger.LogError(
            "Dispose status update FAILED: Field={Field}, Barcode={Barcode}. {Extra}. " +
            "State machine is NOT advanced; the record keeps its old DB state and will be retried.",
            field, barcode, extra);
        try
        {
            await _health.RecordErrorAsync("DisposeSample", "ERROR",
                $"Dispose status update failed: field={field}, barcode={barcode}. {extra}");
            await _health.RecordHealthAsync("DisposeSample", "degraded",
                $"Dispose status update failed: field={field}, barcode={barcode}");
        }
        catch { /* 健康/错误记录失败不影响主循环 */ }
    }

    // resend_count 列不可用：每 10 分钟最多告警一次（不刷屏）
    private void WarnResendColumnNotReady()
    {
        var now = DateTime.UtcNow;
        if (_lastColumnWarnUtc != DateTime.MinValue && now - _lastColumnWarnUtc < ColumnWarnInterval) return;
        _lastColumnWarnUtc = now;
        _logger.LogWarning("sam_dispose_status.resend_count is not ready — auto resend and fail-marking are disabled (check DB migration)");
    }

    // 待处理队列过大告警（每 10 分钟最多一次）
    private async Task WarnPendingAsync()
    {
        if (_dc.PendingWarnThreshold <= 0) return;
        var pending = await _db.CountPendingAsync();
        if (pending <= _dc.PendingWarnThreshold) return;
        var now = DateTime.UtcNow;
        if (_lastPendingWarnUtc != DateTime.MinValue && now - _lastPendingWarnUtc < PendingWarnInterval) return;
        _lastPendingWarnUtc = now;
        _logger.LogWarning("Pending dispose queue is large: {Pending} (threshold {Threshold})",
            pending, _dc.PendingWarnThreshold);
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_socket.Connected) { _connectRetries = 0; return true; }
        try
        {
            await _socket.ConnectAsync(ct);
            _connectRetries = 0;
            return true;
        }
        catch
        {
            _connectRetries++;
            if (_connectRetries % 6 == 1)
                _logger.LogWarning("Waiting for Aptio (attempt {Retry})", _connectRetries);
            return false;
        }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        var checker = new WorkTimeChecker();
        if (!checker.IsWorkTime(_runDays, _timeRanges, out var threshold)) return;

        switch (_state)
        {
            case SysStatus.None:
                await HandleNoneAsync(threshold, ct);
                break;
            case SysStatus.Ready:
                await HandleReadyAsync(ct);
                break;
            case SysStatus.Ack:
                await HandleAckAsync(ct);
                break;
            case SysStatus.Dispose:
                await HandleDisposeAsync(ct);
                break;
        }
    }

    // ===== Timed Dispose State Machine =====

    public async Task HandleNoneAsync(int threshold, CancellationToken ct = default)
    {
        await _db.DeleteHistoryAsync();

        // 发送窗口：MaxOnetimeSelectDiscardCount 同时是「冰箱未丢弃总数上限」
        //   active = send=0（待发） + send=1 且 disposed=0（已发未确认）
        //   窗口只拦「插入新记录」；发送已有 send=0 的闸门在 HandleReady（按在途数 inFlight）。
        var window = _dc.MaxOnetimeSelectDiscardCount;      // <=0 视为不限
        var pendingSend = await _db.CountPendingSendAsync();
        var active = await _db.CountPendingAsync();

        // 已有待发记录（send=0）→ 先把它们发完。
        // 必须排在窗口判断之前：窗口里若正好是 10 条待发（active=10、pendingSend=10、inFlight=0），
        // 先被窗口拦下就会永远停在 None —— 正是"窗口塞满就不发"的老毛病。
        // 旧逻辑还只看 inserted>0：候选条码已存在时 inserted=0 → 也永远停在 None。
        if (pendingSend > 0)
        {
            if (pendingSend != _lastPendingSend)
            {
                _logger.LogInformation("Pending queue has {Pending} unsent record(s) → Ready", pendingSend);
                _lastPendingSend = pendingSend;
            }
            _state = SysStatus.Ready;
            return;
        }

        // 窗口满 → 只等待（不插入新记录）。发送由 HandleReady 的在途窗口控制。
        if (window > 0 && active >= window)
        {
            if (active != _lastWindowActive)
            {
                _logger.LogInformation("Dispose window is full: {Active}/{Window} undisposed records → waiting",
                    active, window);
                _lastWindowActive = active;
            }
            return;     // 状态保持 None，等窗口释放
        }

        // 待处理队列硬上限（MaxPendingRecords>0 时生效）：达到上限不再插入新记录
        if (_dc.MaxPendingRecords > 0)
        {
            var pending = await _db.CountPendingAsync();
            if (pending >= _dc.MaxPendingRecords)
            {
                _logger.LogWarning("Pending queue at limit: {Pending}/{Max} → skip inserting new dispose records",
                    pending, _dc.MaxPendingRecords);
                return;
            }
        }

        var totalCount = 0;
        var snapshot = new List<string>();
        foreach (var nodeId in _aptio.SrmNodeIds)
        {
            var count = await _db.CheckSrmSampleCountAsync(nodeId);
            totalCount += count;
            snapshot.Add($"{nodeId}={count}");
        }
        // 循环降噪：仅数量变化时记录（巡检重复不打）
        var sig = string.Join(",", snapshot) + $";T={totalCount}";
        if (sig != _lastCountSig)
        {
            _logger.LogInformation("Node counts [{Snapshot}], total={Total}, threshold={Threshold}",
                string.Join(",", snapshot), totalCount, threshold);
            _lastCountSig = sig;
        }

        if (totalCount <= threshold) return;

        var need = totalCount - threshold;
        _logger.LogInformation("Need dispose {Need}", need);

        var samples = await _db.GetDisposeSamplesAsync(
            _dc.CommandType, _dc.CommandName, _dc.MaxOnetimeSelectDiscardCount);

        if (samples.Count > 0)
        {
            // 插入条数 = min(need, window - active, samples.Count)
            // window<=0 视为不限 → 保持原逻辑（need）
            var insertCap = need;
            if (window > 0) insertCap = Math.Min(insertCap, window - active);
            insertCap = Math.Min(insertCap, samples.Count);
            var inserted = await _db.InsertDisposeRecordsAsync(insertCap, samples);
            if (inserted > 0)
            {
                _state = SysStatus.Ready;
                _logger.LogInformation("State → Ready ({Inserted} records)", inserted);
            }
        }
    }

    private static string GetNodeIdFromLocation(string? location)
    {
        // Location format: &1-{NodeId}-{Rack}-...
        if (string.IsNullOrEmpty(location)) return "";
        var parts = location.Split('-');
        return parts.Length > 1 ? parts[1] : "";
    }

    public async Task HandleReadyAsync(CancellationToken ct = default)
    {
        // 发送窗口：在途（send=1 且 disposed=0）达到窗口上限 → 本轮不发任何命令（含 STATUS 探测）
        var window = _dc.MaxOnetimeSelectDiscardCount;      // <=0 视为不限
        var inFlight = await _db.CountPendingConfirmAsync();
        if (window > 0 && inFlight >= window)
        {
            var now = DateTime.UtcNow;
            if (inFlight != _lastWindowInFlight
                || _lastWindowLogUtc == DateTime.MinValue
                || now - _lastWindowLogUtc >= WindowLogInterval)
            {
                _logger.LogInformation("Dispose window is full: {InFlight}/{Window} in-flight records → waiting",
                    inFlight, window);
                _lastWindowInFlight = inFlight;
                _lastWindowLogUtc = now;
            }
            return;     // 状态保持 Ready，下一轮再检查
        }

        var response = await _commands.SendStatusRequestAsync(ct);
        if (response == null || !response.StartsWith("STATUS"))
        {
            _logger.LogWarning("STATUS check failed: {Resp}", response);
            return;
        }

        var record = await _db.SelectOneSendRecordAsync();
        if (record == null)
        {
            _logger.LogInformation("No pending records → None");
            _state = SysStatus.None;
            return;
        }

        // Check if the specific node for this record is ready
        var nodeId = GetNodeIdFromLocation(record.Location);
        var nodeUnknown = string.IsNullOrEmpty(nodeId) || !_aptio.SrmNodeIds.Contains(nodeId);
        if (nodeUnknown)
        {
            if (_dc.SkipOnUnknownNode)
            {
                _logger.LogWarning("Record {Barcode} node '{Node}' not found in configured nodes, skipping", record.Barcode, nodeId);
                // 推进队首：标记 skip（send=2 sentinel），否则该记录永远停在队首，阻塞后续所有记录
                var skipOk = await _db.UpdateStatusAsync(record.Barcode!, "skip");
                if (!skipOk)
                    await ReportStatusUpdateFailureAsync("skip", record.Barcode!,
                        "record stays at queue head (send=0) and will be retried; queue may be blocked");
                return;
            }
            // SkipOnUnknownNode=false：按"照发不误"处理 —— 未知节点**跳过 IsNodeReady 判定**直接下发。
            // 历史缺陷（P2）：此前仍走 IsNodeReady（未知节点恒 false）→ 记录永远卡在队首、后续全部阻塞。
            _logger.LogWarning("Record {Barcode} node '{Node}' not in configured nodes (SkipOnUnknownNode=false), " +
                "disposing anyway without node-ready check", record.Barcode, nodeId);
        }
        else if (!_statusDecoder.IsNodeReady(response, nodeId, _allowErrors, _dc.AllowStandbyDispose))
        {
            // 已知节点但未就绪（OFF/STB/GTO/… 或状态非 G/Y）→ 本轮不发，等下一轮
            _logger.LogInformation("Node {Node} not ready, skipping", nodeId);
            return;
        }
        // 测试开关放行提示（仅当 AllowStandbyDispose=true 且该节点是 STB / Pink 时打印）
        LogStandbyAllowedIfAny(response, nodeId);

        _preBarcode = record.Barcode;
        if (string.IsNullOrEmpty(_preBarcode))
        {
            _logger.LogWarning("Record has no barcode, skipping");
            return;
        }
        // 成功 = 命令已投递（Host LIS 的 COMMENT S002 / ORDER 无 ACK，手册 §8.59 / §9.3）
        var delivered = await _commands.SendDisposeAsync(_preBarcode, ct);

        if (delivered)
        {
            var sendOk = await _db.UpdateStatusAsync(record.Barcode!, "send");
            if (!sendOk)
            {
                // DB 写入失败：记录仍是 send=0 → 状态机**不进入 Ack**（保持 Ready，下一轮重试）。
                // 注意：命令已经投递，重试会导致同一条被重复下发（丢弃命令幂等，但需人工知晓）。
                await ReportStatusUpdateFailureAsync("send", record.Barcode!,
                    "command was ALREADY delivered but DB still shows send=0 - the same record may be re-sent next cycle");
                return;
            }
            _state = SysStatus.Ack;
            _logger.LogInformation("State → Ack (sent {Barcode})", record.Barcode);
        }
        else
        {
            _logger.LogWarning("Dispose not delivered: {Barcode}", record.Barcode);
        }
    }

    public async Task HandleAckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_preBarcode))
        {
            _state = SysStatus.Dispose;
            return;
        }

        // Check all configured nodes for disposal confirmation
        var disposed = false;
        foreach (var nodeId in _aptio.SrmNodeIds)
        {
            if (await _db.CheckDisposedAsync(_preBarcode, nodeId))
            {
                disposed = true;
                break;
            }
        }

        if (disposed)
        {
            var disposedOk = await _db.UpdateStatusAsync(_preBarcode, "disposed");
            if (!disposedOk)
            {
                // DB 未标记 disposed → 不推进状态（保持 Ack，下一轮继续确认）
                await ReportStatusUpdateFailureAsync("disposed", _preBarcode!,
                    "DB still shows disposed=0 - confirmation will be retried");
                return;
            }
            _state = SysStatus.Dispose;
            _logger.LogInformation("{Barcode} confirmed disposed → Dispose", _preBarcode);
        }
        else if (_dc.MaxWaitDiscardCount > 0)
        {
            var cnt = await _db.GetCheckCountAsync(_preBarcode);
            if (cnt < _dc.MaxWaitDiscardCount)
            {
                var checkOk = await _db.UpdateStatusAsync(_preBarcode, "checkcount");
                if (!checkOk)
                {
                    // 计数未推进 → 保持 Ack（下一轮重试，不静默）
                    await ReportStatusUpdateFailureAsync("checkcount", _preBarcode!,
                        "checkcount not incremented - retry next cycle");
                    return;
                }
                _logger.LogInformation("Waiting ({Check}/{Max})", cnt, _dc.MaxWaitDiscardCount);
            }
            else
            {
                _state = SysStatus.Dispose;
                _logger.LogInformation("Max check reached → Dispose");
            }
        }
        else
        {
            _state = SysStatus.Dispose;
        }
    }

    public async Task HandleDisposeAsync(CancellationToken ct = default)
    {
        var resp = await _commands.SendStatusRequestAsync(ct);
        if (resp != null && resp.StartsWith("STATUS"))
        {
            _state = SysStatus.None;
            _logger.LogInformation("State → None (cycle complete)");
        }
    }
}