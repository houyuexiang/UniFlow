using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.DisposeSample.Workers;
using UniFlow.WebAdmin.Services;
using Xunit.Abstractions;

namespace UniFlow.Tests.DisposeSample.Workers;

/// <summary>
/// 样本丢弃「快速填窗」行为用例：
/// <list type="bullet">
/// <item>默认关闭（<c>EnableFastFill=false</c>）→ 行为与 1.0.6.7 完全一致（一条一条发、Ready→Ack）；</item>
/// <item>开启后 → 一个 tick 内连续发 COMMENT 直到发送窗口满，不再每条等确认（状态保持 Ready）；</item>
/// <item>确认改为批量（<c>MaybeFastConfirmAsync</c>：一条 SQL 确认所有在途记录）；</item>
/// <item>重排队死锁修复：窗口满时仍允许本轮重排队（放回 send=0 才能释放窗口）。</item>
/// </list>
/// </summary>
public class DisposeSampleWorkerFastFillTests : IDisposable
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IDisposeDatabaseService _db = Substitute.For<IDisposeDatabaseService>();
    private readonly SrmStatusDecoder _decoder = new();
    private readonly CapturingLogger<DisposeSampleWorker> _logger = new();
    private readonly AptioCommandService _commands;
    private readonly DisposeSampleWorker _worker;
    private readonly HealthStore _health;
    private readonly ErrorReporter _reporter = new();
    private readonly string _healthDbPath;
    private readonly ITestOutputHelper _out;
    // 可控时钟（只用于快速填窗批量确认的节流判定）
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DisposeSampleWorkerFastFillTests(ITestOutputHelper output)
    {
        _out = output;
        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-fastfill-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance, 30, _reporter);
        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(_logger, _socket, _db, _commands, _decoder, _health,
            new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig()),
            new UniFlow.Tests.Common.TestOptionsMonitor<FeatureConfig>(new FeatureConfig()),
            () => _now);
        _worker.LoadConfig(BuildConfig());

        _socket.Connected.Returns(true);
        _db.ResendColumnReady.Returns(true);
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingAsync().Returns(Task.FromResult(0));
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((0, 0L)));
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
        _db.GetExhaustedCandidatesAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
    }

    /// <summary>
    /// 默认配置：全天运行（RunDays=1..7，TimeRanges=00:00-23:59）、节点 18、窗口 10、允许错误码 0000。
    /// <paramref name="enableFastFill"/> 默认 false —— 与 1.0.6.7 行为一致。
    /// </summary>
    private static AptioConfig BuildConfig(
        bool enableFastFill = false,
        int window = 10,
        int intervalMs = 0,
        int confirmSeconds = 5,
        bool skipOnUnknownNode = true)
        => new()
        {
            Ip = "127.0.0.1",
            Port = 2055,
            SrmNodeIds = new() { "18" },
            DisposeSample = new AptioDisposeSampleConfig
            {
                CommandType = 0,
                CommandName = "view_overtimestoragesample",
                DiscardRunDate = "1,2,3,4,5,6,7",
                DiscardTimeRange = "00:00-23:59,9999;",
                MaxWaitDiscardCount = 3,
                MaxOnetimeSelectDiscardCount = window,
                AllowSrmErrorCode = "0000",
                SkipOnUnknownNode = skipOnUnknownNode,
                ResendIntervalMinutes = 5,
                MaxResendCount = 24,
                MaxPendingRecords = 0,
                PendingWarnThreshold = 1000,
                EnableFastFill = enableFastFill,
                FastFillIntervalMs = intervalMs,
                FastFillConfirmIntervalSeconds = confirmSeconds
            }
        };

    private void SetStatus(string? response) =>
        _socket.SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(response));

    private const string ReadyStatus = @"STATUS\18^SRM^1^ON^0000^G^^\";

    // 业务命令（COMMENT）投递成功 = 返回 DeliveredMarker（Host LIS 的 COMMENT S002 无 ACK）
    private void SetDelivered() =>
        _socket.SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(AptioSocketClient.DeliveredMarker));

    /// <summary>队首取记录：按调用顺序依次返回 <paramref name="nodes"/> 指定的节点，取完返回 null。</summary>
    private void SetRecords(params string[] nodes)
    {
        var call = 0;
        _db.SelectOneSendRecordAsync().Returns(_ =>
        {
            var idx = call++;
            if (idx >= nodes.Length) return Task.FromResult<DisposeStatus?>(null);
            return Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = $"S{idx + 1:000}",
                Location = $"&1-{nodes[idx]}-{idx + 1:000000}",
                Stype = "S"
            });
        });
    }

    private async Task<List<string>> ErrorMessagesAsync()
    {
        while (_reporter.Reader.TryRead(out var entry))
            await _health.WriteErrorAsync(entry.Module, entry.Level, entry.Message);
        var errors = await _health.GetErrorsAsync(1, "DisposeSample");
        return errors.Select(e => e["message"]?.ToString() ?? "").ToList();
    }

    private async Task<List<string>> HealthMessagesAsync()
    {
        var history = await _health.GetHealthHistoryAsync(1, "DisposeSample");
        return history.Select(h => $"{h["status"]}|{h["message"]}").ToList();
    }

    // ===== 1. 默认关闭 → 行为与 1.0.6.7 完全一致 =====

    [Fact]
    public async Task FastFill_DisabledByDefault_SendsOneAndEntersAck()
    {
        _worker.LoadConfig(BuildConfig());          // EnableFastFill = false（默认）
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18", "18");
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        // 只发 1 条（其余留给下一轮）
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(1).UpdateStatusAsync("S001", "send");
        // 仍然走原来的 Ready → Ack
        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        _worker.CurrentBarcode.ShouldBe("S001");
    }

    [Fact]
    public async Task FastFill_Disabled_ConfigDefaultIsFalse()
    {
        new AptioDisposeSampleConfig().EnableFastFill.ShouldBeFalse();
        new AptioConfig().DisposeSample.EnableFastFill.ShouldBeFalse();
    }

    // ===== 2. 开启 + 窗口 10 + 15 条待发 → 一次调用连发 10 条，状态保持 Ready =====

    [Fact]
    public async Task FastFill_Enabled_SendsUpToWindowInOneTick_StaysReady()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords(Enumerable.Repeat("18", 15).ToArray());
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        // 一次调用发满窗口 10 条
        await _socket.Received(10).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(10).UpdateStatusAsync(Arg.Any<string>(), "send");
        // 不再进 Ack（确认交给批量确认）
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        _logger.Entries.Any(e => e.Message.Contains("Fast-fill sent 10 record(s)")).ShouldBeTrue();
    }

    // ===== 3. 第 3 条投递失败 → 尝试 3 次、成功 2 条后停止，状态 Ready =====

    [Fact]
    public async Task FastFill_ThirdNotDelivered_StopsAfterTwoSuccesses()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18", "18", "18");
        SetStatus(ReadyStatus);

        var calls = 0;
        _socket.SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<string?>(++calls <= 2 ? AptioSocketClient.DeliveredMarker : null));

        await _worker.HandleReadyAsync();

        await _socket.Received(3).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(2).UpdateStatusAsync(Arg.Any<string>(), "send");
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    // ===== 4. 第 1 条 DB 写入失败 → 只发 1 条就停止，并记 Error + 健康 degraded =====

    [Fact]
    public async Task FastFill_SendStatusUpdateFails_StopsAndReportsFailure()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18", "18");
        SetStatus(ReadyStatus);
        SetDelivered();
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        await _worker.HandleReadyAsync();

        // 只发 1 条就停（不再继续发下一条）
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        // ReportStatusUpdateFailureAsync 生效：Error 记录 + 健康 degraded
        (await ErrorMessagesAsync()).Any(m => m.Contains("send") && m.Contains("S001")).ShouldBeTrue();
        (await HealthMessagesAsync()).Any(m => m.StartsWith("degraded")).ShouldBeTrue();
        _logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("status update FAILED")).ShouldBeTrue();
    }

    // ===== 5. 节点未就绪 → 一条都不发，状态 Ready =====

    [Fact]
    public async Task FastFill_NodeNotReady_SendsNothing_StaysReady()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18");
        SetStatus(@"STATUS\18^SRM^1^OFF^0000^G^^\");
        SetDelivered();

        await _worker.HandleReadyAsync();

        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), "send");
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    // ===== 6. 未知节点 + SkipOnUnknownNode=true → 跳过该条并继续发下一条（不卡队首）=====

    [Fact]
    public async Task FastFill_UnknownNodeSkip_AdvancesQueueHeadAndKeepsSending()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10, skipOnUnknownNode: true));
        _worker.CurrentState = SysStatus.Ready;
        // 第 1 条在未知节点 99（应被 skip 并推进队首），其后 11 条在节点 18（窗口 10 → 发满即停）
        SetRecords(new[] { "99" }.Concat(Enumerable.Repeat("18", 11)).ToArray());
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        // 第 1 条被 skip（推进队首），后续 10 条正常投递（证明没有卡在队首）
        await _db.Received(1).UpdateStatusAsync("S001", "skip");
        await _db.DidNotReceive().UpdateStatusAsync("S001", "send");
        await _db.Received(10).UpdateStatusAsync(Arg.Any<string>(), "send");
        await _socket.Received(10).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        // 窗口发满 → 停在 Ready（不进 Ack）
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    // ===== 7. 窗口 <= 0（不限）→ 不走快速填窗，仍是一条一条发（进 Ack）=====

    [Fact]
    public async Task FastFill_WindowUnlimited_KeepsSingleRecordPath()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 0));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18", "18");
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(1).UpdateStatusAsync("S001", "send");
        _worker.CurrentState.ShouldBe(SysStatus.Ack);
    }

    // ===== 8. 批量确认 MaybeFastConfirmAsync =====

    [Fact]
    public async Task FastConfirm_Disabled_DoesNotTouchDb()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: false));

        var n = await _worker.MaybeFastConfirmAsync();

        n.ShouldBe(0);
        await _db.DidNotReceive().RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>());
    }

    [Fact]
    public async Task FastConfirm_Enabled_QueriesOncePerInterval_WithCursorReset()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, confirmSeconds: 5));
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((3, 7L)));

        // 第 1 次 → 查库
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(3);
        await _db.Received(1).RecheckPendingDisposalsAsync(200, 0);
        _logger.Entries.Any(e => e.Message.Contains("Fast-fill confirm: 3")).ShouldBeTrue();

        // 节流窗口内（5 秒）→ 不查库
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(0);
        await _db.Received(1).RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>());

        // 超过间隔 → 再查一次
        _now = _now.AddSeconds(6);
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(3);
        await _db.Received(2).RecheckPendingDisposalsAsync(200, 0);
    }

    // ===== 9. 重排队死锁修复 =====

    [Fact]
    public async Task Requeue_WindowFull_StillRequeuesEligibleRecords()
    {
        // 窗口满（inFlight=10、窗口=10）+ 有到期候选 → 仍要发 STATUS 并重排队（放回 send=0 才能释放窗口）
        _worker.LoadConfig(BuildConfig());
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(10));
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "&1-18-000001", Send = 1, Disposed = 0, ResendCount = 1 }
            }));
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));
        SetStatus(ReadyStatus);

        var n = await _worker.MaybeRequeueForResendAsync();

        n.ShouldBe(1);
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>());
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001")));
        // 重排队阶段仍绝不下发丢弃命令
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _logger.Entries.Any(e => e.Message.Contains("window full but requeue allowed to free it")).ShouldBeTrue();
    }

    [Fact]
    public async Task Requeue_WindowFull_NoCandidates_DoesNotSendStatus()
    {
        // 窗口满 + 无到期候选 → 仍然不发 STATUS（保持「没有候选不发 STATUS」的原行为）
        _worker.LoadConfig(BuildConfig());
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(10));
        SetStatus(ReadyStatus);

        var n = await _worker.MaybeRequeueForResendAsync();

        n.ShouldBe(0);
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    // ===== 10. 配置默认值 =====

    [Fact]
    public void FastFill_ConfigDefaults_AreOff1000And5()
    {
        var dc = new AptioDisposeSampleConfig();
        dc.EnableFastFill.ShouldBeFalse();
        dc.FastFillIntervalMs.ShouldBe(1000);
        dc.FastFillConfirmIntervalSeconds.ShouldBe(5);

        var cfg = new AptioConfig();
        cfg.DisposeSample.EnableFastFill.ShouldBeFalse();
        cfg.DisposeSample.FastFillIntervalMs.ShouldBe(1000);
        cfg.DisposeSample.FastFillConfirmIntervalSeconds.ShouldBe(5);
    }

    [Fact]
    public void FastFill_ConfigPaths_AreHotAndTyped()
    {
        // 页面可保存这三个字段：路径可解析 + 热生效（无需重启）+ 值类型正确
        var (level, known, kind) = RestartLevelResolver.ResolveWithKind("Aptio:DisposeSample:EnableFastFill");
        known.ShouldBeTrue();
        level.ShouldBe(RestartLevel.Hot);
        kind.ShouldBe("bool");

        var (level2, known2, kind2) = RestartLevelResolver.ResolveWithKind("Aptio:DisposeSample:FastFillIntervalMs");
        known2.ShouldBeTrue();
        level2.ShouldBe(RestartLevel.Hot);
        kind2.ShouldBe("int");

        var (level3, known3, kind3) = RestartLevelResolver.ResolveWithKind("Aptio:DisposeSample:FastFillConfirmIntervalSeconds");
        known3.ShouldBeTrue();
        level3.ShouldBe(RestartLevel.Hot);
        kind3.ShouldBe("int");
    }

    // ===== 11. P2-1 范围钳制：FastFillIntervalMs 0~5000 / FastFillConfirmIntervalSeconds 1~60 =====

    [Fact]
    public void FastFill_ClampHelpers_EnforceDocumentedRanges()
    {
        // 间隔：0~5000（600000 → 5000；负数 → 0）
        DisposeSampleWorker.ClampFastFillIntervalMs(600000).ShouldBe(5000);
        DisposeSampleWorker.ClampFastFillIntervalMs(5001).ShouldBe(5000);
        DisposeSampleWorker.ClampFastFillIntervalMs(5000).ShouldBe(5000);
        DisposeSampleWorker.ClampFastFillIntervalMs(1000).ShouldBe(1000);
        DisposeSampleWorker.ClampFastFillIntervalMs(0).ShouldBe(0);
        DisposeSampleWorker.ClampFastFillIntervalMs(-1).ShouldBe(0);
        DisposeSampleWorker.FastFillIntervalMaxMs.ShouldBe(5000);

        // 确认间隔：1~60（0 → 1；999 → 60）
        DisposeSampleWorker.ClampFastFillConfirmIntervalSeconds(0).ShouldBe(1);
        DisposeSampleWorker.ClampFastFillConfirmIntervalSeconds(-5).ShouldBe(1);
        DisposeSampleWorker.ClampFastFillConfirmIntervalSeconds(1).ShouldBe(1);
        DisposeSampleWorker.ClampFastFillConfirmIntervalSeconds(5).ShouldBe(5);
        DisposeSampleWorker.ClampFastFillConfirmIntervalSeconds(60).ShouldBe(60);
        DisposeSampleWorker.ClampFastFillConfirmIntervalSeconds(999).ShouldBe(60);
        DisposeSampleWorker.FastFillConfirmIntervalMinSeconds.ShouldBe(1);
        DisposeSampleWorker.FastFillConfirmIntervalMaxSeconds.ShouldBe(60);
    }

    [Fact]
    public async Task FastFill_IntervalMs600000_EffectiveDelayCappedAt5Seconds()
    {
        // 配置 600000ms（=100 分钟）→ 代码硬上限 5000ms：实际等待约 5 秒，而不是 100 分钟
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 2, intervalMs: 600000));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18");
        SetStatus(ReadyStatus);
        SetDelivered();

        var sw = Stopwatch.StartNew();
        await _worker.HandleReadyAsync();
        sw.Stop();

        await _socket.Received(2).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        // 确实等了一次（>=4s，说明没有走「不等待」分支）
        sw.Elapsed.TotalSeconds.ShouldBeGreaterThanOrEqualTo(4.0);
        // 但绝不超过 5000ms 太多（没有按 600000ms 阻塞）
        sw.Elapsed.TotalSeconds.ShouldBeLessThan(30.0);
    }

    [Fact]
    public async Task FastConfirm_IntervalZero_IsClampedToOneSecond()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, confirmSeconds: 0));
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((2, 0L)));

        (await _worker.MaybeFastConfirmAsync()).ShouldBe(2);
        await _db.Received(1).RecheckPendingDisposalsAsync(200, 0);

        // 0 被钳到 1 秒 → 0.5 秒后仍在节流窗口内
        _now = _now.AddMilliseconds(500);
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(0);
        await _db.Received(1).RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>());

        // 超过 1 秒 → 放行
        _now = _now.AddMilliseconds(700);
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(2);
        await _db.Received(2).RecheckPendingDisposalsAsync(200, 0);
    }

    [Fact]
    public async Task FastConfirm_Interval999_IsClampedToSixtySeconds()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, confirmSeconds: 999));
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((1, 0L)));

        (await _worker.MaybeFastConfirmAsync()).ShouldBe(1);
        await _db.Received(1).RecheckPendingDisposalsAsync(200, 0);

        // 999 被钳到 60 秒 → 30 秒后仍在节流窗口内
        _now = _now.AddSeconds(30);
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(0);
        await _db.Received(1).RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>());

        // 超过 60 秒 → 放行
        _now = _now.AddSeconds(31);
        (await _worker.MaybeFastConfirmAsync()).ShouldBe(1);
        await _db.Received(2).RecheckPendingDisposalsAsync(200, 0);
    }

    // ===== 12. P2-2 未知节点 skip 单轮上限（防止节点号配错时一个 tick 遍历整个队列）=====

    [Fact]
    public async Task FastFill_UnknownNodeSkip_StopsAfterMaxSkipsPerTick()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10, skipOnUnknownNode: true));
        _worker.CurrentState = SysStatus.Ready;
        // 201 条全是未知节点（99）→ 达到单轮上限（200）即停止，不无限循环
        SetRecords(Enumerable.Repeat("99", 201).ToArray());
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        await _db.Received(DisposeSampleWorker.FastFillMaxSkipsPerTick)
            .UpdateStatusAsync(Arg.Any<string>(), "skip");
        // 第 201 条不再处理：不发 COMMENT、不写 send
        await _db.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), "send");
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        // 达上限打 Warning（说明本轮暂停）
        _logger.Entries.Any(e => e.Level == LogLevel.Warning
            && e.Message.Contains("Fast-fill skipped")
            && e.Message.Contains("pause this round")).ShouldBeTrue();
    }

    [Fact]
    public async Task FastFill_UnknownNodeSkip_ContinuesBelowLimit()
    {
        // 对照：未达上限时继续处理下一条（skip 不卡队首）
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10, skipOnUnknownNode: true));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords(new[] { "99" }.Concat(Enumerable.Repeat("18", 3)).ToArray());
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        await _db.Received(1).UpdateStatusAsync("S001", "skip");
        await _db.Received(3).UpdateStatusAsync(Arg.Any<string>(), "send");
        await _socket.Received(3).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _logger.Entries.Any(e => e.Message.Contains("Fast-fill skipped")).ShouldBeFalse();
    }

    // ===== 13. P2-3 窗口 <= 0 时 Ack 仍走逐条确认（MaxWaitDiscardCount 继续生效）=====

    [Fact]
    public async Task Ack_FastFillEnabledWindowZero_KeepsPerRecordConfirm()
    {
        // EnableFastFill=true 但窗口 <= 0 → 发送走单条路径 → 确认也必须走逐条确认（不能直接转 None）
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 0));
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "18").Returns(Task.FromResult(false));

        await _worker.HandleAckAsync();

        await _db.Received(1).CheckDisposedAsync("S001", "18");
        // 未确认 → 走 MaxWaitDiscardCount 计数（不转 None）
        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        await _db.Received(1).UpdateStatusAsync("S001", "checkcount");
    }

    [Fact]
    public async Task Ack_FastFillEnabledWindowPositive_SkipsPerRecordConfirm()
    {
        // 对照：窗口 > 0（快速填窗生效）→ 确认交给批量确认，直接转 None
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";

        await _worker.HandleAckAsync();

        await _db.DidNotReceive().CheckDisposedAsync(Arg.Any<string>(), Arg.Any<string>());
        _worker.CurrentState.ShouldBe(SysStatus.None);
    }

    // ===== 14. 停止路径（防回归：空条码 / STATUS 失败 / skip 失败）=====

    [Fact]
    public async Task FastFill_EmptyBarcode_StopsWithoutSending()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ready;
        _db.SelectOneSendRecordAsync().Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
        {
            Barcode = "",
            Location = "&1-18-000001",
            Stype = "S"
        }));
        SetStatus(ReadyStatus);
        SetDelivered();

        await _worker.HandleReadyAsync();

        // 只取一次队首 → 不会反复取到同一条（死循环）
        await _db.Received(1).SelectOneSendRecordAsync();
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), "send");
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        _logger.Entries.Any(e => e.Message.Contains("Record has no barcode")).ShouldBeTrue();
    }

    [Fact]
    public async Task FastFill_StatusCheckFails_StopsBeforeSelectingRecord()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18");
        SetStatus(null);              // STATUS 无回包
        SetDelivered();

        await _worker.HandleReadyAsync();

        // STATUS 失败 → 立即停：不查队列、不发 COMMENT
        await _db.DidNotReceive().SelectOneSendRecordAsync();
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        _logger.Entries.Any(e => e.Message.Contains("STATUS check failed")).ShouldBeTrue();
    }

    [Fact]
    public async Task FastFill_SkipUpdateFails_StopsLoopAndReports()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10, skipOnUnknownNode: true));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("99", "99");
        SetStatus(ReadyStatus);
        SetDelivered();
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        await _worker.HandleReadyAsync();

        // skip 写库失败 → 只尝试一次即停（不会反复取到同一条）
        await _db.Received(1).UpdateStatusAsync("S001", "skip");
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        // 失败必须上报：Error 记录 + 健康 degraded
        (await ErrorMessagesAsync()).Any(m => m.Contains("skip") && m.Contains("S001")).ShouldBeTrue();
        (await HealthMessagesAsync()).Any(m => m.StartsWith("degraded")).ShouldBeTrue();
    }

    // ===== 15. P3 取消不再被吞掉：DelayFastFillAsync 抛 OperationCanceledException =====

    [Fact]
    public async Task FastFill_CancelledDuringDelay_PropagatesCancellation()
    {
        _worker.LoadConfig(BuildConfig(enableFastFill: true, window: 10, intervalMs: 1000));
        _worker.CurrentState = SysStatus.Ready;
        SetRecords("18", "18", "18");
        SetStatus(ReadyStatus);

        using var cts = new CancellationTokenSource();
        // 第 1 条 COMMENT 投递成功后立刻取消 → 随后的间隔等待必须抛取消（不能吞掉后继续下一轮）
        _socket.SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult<string?>(AptioSocketClient.DeliveredMarker);
            });

        Exception? caught = null;
        try { await _worker.HandleReadyAsync(cts.Token); }
        catch (Exception ex) { caught = ex; }

        caught.ShouldNotBeNull();
        caught.ShouldBeAssignableTo<OperationCanceledException>();
        // 取消后不再发下一条（循环没有在已取消的 token 上继续）
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    // 捕获日志的 ILogger
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    public void Dispose()
    {
        _health.Dispose();
        try { File.Delete(_healthDbPath); } catch { /* ignore */ }
    }
}
