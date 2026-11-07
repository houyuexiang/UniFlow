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
/// 丢弃队列「重排队」行为用例（5 分钟批量检查 + 条件重排队）：
/// - 命中（节点 ON + 状态 G/Y + 错误码在允许列表）→ 把记录放回待发队列（send=0），<b>不直接下发 COMMENT</b>；
/// - STB（Stand-by）等非 ON 模式 → 一律等待，不重排队；
/// - 发送窗口满 → 等待，连 STATUS-REQUEST 都不发；
/// - 不在允许丢弃的时间段 → 只查库，不发任何指令。
/// </summary>
public class DisposeSampleWorkerRequeueTests : IDisposable
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IDisposeDatabaseService _db = Substitute.For<IDisposeDatabaseService>();
    private readonly SrmStatusDecoder _decoder = new();
    private readonly CapturingLogger<DisposeSampleWorker> _logger = new();
    private readonly AptioCommandService _commands;
    private readonly DisposeSampleWorker _worker;
    private readonly HealthStore _health;
    private readonly string _healthDbPath;
    private readonly ITestOutputHelper _out;

    public DisposeSampleWorkerRequeueTests(ITestOutputHelper output)
    {
        _out = output;
        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-requeue-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance);
        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(_logger, _socket, _db, _commands, _decoder, _health,
            new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig()),
            new UniFlow.Tests.Common.TestOptionsMonitor<FeatureConfig>(new FeatureConfig()));
        _worker.LoadConfig(BuildConfig());

        _socket.Connected.Returns(true);
        _db.ResendColumnReady.Returns(true);
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((0, 0L)));
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
        _db.GetExhaustedCandidatesAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
        _db.CountPendingAsync().Returns(Task.FromResult(0));
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        // 默认：DB 状态写入成功（失败路径由专门用例覆盖）
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
    }

    // 默认配置：全天运行（RunDays=1..7，TimeRanges=00:00-23:59）
    private static AptioConfig BuildConfig() => new()
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
            MaxOnetimeSelectDiscardCount = 10,
            AllowSrmErrorCode = "0000",
            SkipOnUnknownNode = true,
            ResendIntervalMinutes = 5,
            MaxResendCount = 24,
            MaxPendingRecords = 0,
            PendingWarnThreshold = 1000
        }
    };

    private void SetStatus(string response)
    {
        _socket.SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(response));
    }

    private void SetCandidates(params string[] barcodes)
    {
        var rows = barcodes.Select((b, i) => new DisposeStatus
        {
            Barcode = b,
            Location = $"&1-18-00000{i}",
            Send = 1,
            Disposed = 0,
            ResendCount = 1
        }).ToList();
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(rows));
    }

    // ===== 命中 → 重排队（且绝不直接发 COMMENT）=====

    [Fact]
    public async Task Requeue_Eligible_RequeuesWithoutSendingComment()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));

        var n = await _worker.MaybeRequeueForResendAsync();

        n.ShouldBe(1);
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001")));
        // 关键：重排队阶段绝不下发丢弃命令（真正发送由正常状态机按窗口一条条发）
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        // 只发 1 条 STATUS-REQUEST
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requeue_Eligible_YellowStatus_AlsoRequeues()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^Y^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(1);
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001")));
    }

    [Fact]
    public async Task Requeue_SendsOnlyOneStatusRequest_PerRound()
    {
        SetCandidates("S001", "S002", "S003");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(3));

        await _worker.MaybeRequeueForResendAsync();

        await _socket.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Count == 3));
    }

    // ===== STB / 非 ON → 不重排队 =====

    [Fact]
    public async Task Requeue_NotReady_STB_DoesNotRequeue()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^STB^0000^G^^\");

        var n = await _worker.MaybeRequeueForResendAsync();

        n.ShouldBe(0);
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        // STB 不算正常（手册 Appendix F：carriers NOT processed、Automation Map OFF）
        _decoder.IsNodeReady(@"STATUS\18^SRM^1^STB^0000^G^^\", "18", new HashSet<string> { "0000" })
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Requeue_NotReady_STB_LogsMode()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^STB^0000^G^^\");

        await _worker.MaybeRequeueForResendAsync();

        _logger.Entries.Any(e => e.Message.Contains("mode=STB") && e.Message.Contains("wait"))
            .ShouldBeTrue();
    }

    [Theory]
    [InlineData("GTO")]
    [InlineData("PA")]
    [InlineData("OFF")]
    [InlineData("FR")]
    [InlineData("SH")]
    [InlineData("EXE")]
    public async Task Requeue_NeverAllowedModes_SwitchOn_DoNotRequeue(string mode)
    {
        // 这些模式即使 AllowStandbyDispose=true 也不允许丢弃
        var cfg = BuildConfig();
        cfg.DisposeSample.AllowStandbyDispose = true;
        _worker.LoadConfig(cfg);

        SetCandidates("S001");
        SetStatus($@"STATUS\18^SRM^1^{mode}^0000^G^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Requeue_StandBy_SwitchOn_Requeues()
    {
        // AllowStandbyDispose=true → STB 参与丢弃判断 → 允许重排队
        var cfg = BuildConfig();
        cfg.DisposeSample.AllowStandbyDispose = true;
        _worker.LoadConfig(cfg);

        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^STB^0000^G^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(1);
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001")));
        // 仍不直接下发丢弃命令
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requeue_StandBy_SwitchOn_LogsSwitchActive()
    {
        var cfg = BuildConfig();
        cfg.DisposeSample.AllowStandbyDispose = true;
        _worker.LoadConfig(cfg);

        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^STB^0000^G^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));

        await _worker.MaybeRequeueForResendAsync();

        _logger.Entries.Any(e => e.Message.Contains("AllowStandbyDispose=true")).ShouldBeTrue();
    }

    [Fact]
    public async Task Requeue_Pink_SwitchOn_Requeues()
    {
        var cfg = BuildConfig();
        cfg.DisposeSample.AllowStandbyDispose = true;
        _worker.LoadConfig(cfg);

        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^P^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(1);
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001")));
    }

    [Fact]
    public async Task Requeue_Pink_SwitchOff_DoesNotRequeue()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^P^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Requeue_ErrorNotAllowed_DoesNotRequeue()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^E001^G^^\");   // E001 不在允许列表（只允许 0000）

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Requeue_StatusBadOrGreenMissing_DoesNotRequeue()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^R^^\");   // 状态既不是 G 也不是 Y

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    // ===== 窗口满 → 不发 STATUS、不重排队 =====

    [Fact]
    public async Task Requeue_WindowFull_SkipsStatusAndRequeue()
    {
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(10));   // inFlight = 窗口 10
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        var n = await _worker.MaybeRequeueForResendAsync();

        n.ShouldBe(0);
        // 窗口满 → 连 STATUS-REQUEST 都不发
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    // ===== 不在允许丢弃的时间段 → 只查库，不发任何指令 =====

    [Fact]
    public async Task Requeue_OutsideWorkTime_SkipsStatusAndRequeue()
    {
        // 运行日设为「今天之外」的一天 → IsWorkTime=false
        var today = (int)DateTime.Now.DayOfWeek;
        var delphiToday = today == 0 ? 7 : today;
        var otherDay = delphiToday == 1 ? 2 : 1;
        var cfg = BuildConfig();
        cfg.DisposeSample.DiscardRunDate = otherDay.ToString();
        _worker.LoadConfig(cfg);

        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        var n = await _worker.MaybeRequeueForResendAsync();

        n.ShouldBe(0);
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
        // 只查库、不发指令：候选查询都不该发生
        await _db.DidNotReceive().GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
    }

    // ===== 未知节点（位置里取不到节点号）=====

    [Fact]
    public async Task Requeue_UnknownNode_SkipsRecordWhenSkipOnUnknownNode()
    {
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "NONODE", Send = 1, Disposed = 0, ResendCount = 1 }
            }));
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.Received(1).UpdateStatusAsync("S001", "skip");
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Requeue_UnknownNode_SkipsWithoutMarkingWhenSkipDisabled()
    {
        var cfg = BuildConfig();
        cfg.DisposeSample.SkipOnUnknownNode = false;
        _worker.LoadConfig(cfg);

        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "NONODE", Send = 1, Disposed = 0, ResendCount = 1 }
            }));
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    // ===== 节流 / 开关 / 候选为空 =====

    [Fact]
    public async Task Requeue_ThrottledToConfiguredInterval()
    {
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));

        await _worker.RunMaintenanceAsync();
        await _worker.RunMaintenanceAsync();     // 5 分钟节流内不再检查

        await _db.Received(1).GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task Requeue_UsesConfiguredIntervalAsCutoff()
    {
        SetCandidates();

        await _worker.MaybeRequeueForResendAsync();

        // cutoff = now - ResendIntervalMinutes(5)
        await _db.Received(1).GetResendCandidatesAsync(
            Arg.Is<string>(s => DateTime.ParseExact(s, "yyyyMMddHHmmss", null) <= DateTime.Now.AddMinutes(-4)),
            24,
            Arg.Any<int>());
    }

    [Fact]
    public async Task Requeue_IntervalZero_DisablesRequeue()
    {
        var cfg = BuildConfig();
        cfg.DisposeSample.ResendIntervalMinutes = 0;
        _worker.LoadConfig(cfg);

        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Requeue_NoCandidates_DoesNotSendStatus()
    {
        SetCandidates();   // 空
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Requeue_StatusFailed_DoesNotRequeue()
    {
        SetCandidates("S001");
        _socket.SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));   // STATUS 超时/失败

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task Requeue_ColumnNotReady_SkipsEverything()
    {
        _db.ResendColumnReady.Returns(false);
        SetCandidates("S001");
        SetStatus(@"STATUS\18^SRM^1^ON^0000^G^^\");

        (await _worker.MaybeRequeueForResendAsync()).ShouldBe(0);
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
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
