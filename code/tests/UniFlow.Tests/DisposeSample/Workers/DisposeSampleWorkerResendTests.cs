using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
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
/// 丢弃队列「重发 / 失败处置」行为用例：
/// - 到期重排队（send=1 &amp; disposed=0 &amp; send_time 超过 ResendIntervalMinutes → 放回待发队列 send=0）
/// - 重排队阶段<b>绝不</b>直接下发 COMMENT S002（真正发送由正常状态机按窗口一条条发）
/// - 重发次数用尽 → 标记失败 send=3
/// - ResendIntervalMinutes=0 → 关闭重排队
/// </summary>
public class DisposeSampleWorkerResendTests : IDisposable
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

    public DisposeSampleWorkerResendTests(ITestOutputHelper output)
    {
        _out = output;        var aptioConfig = new AptioConfig
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

        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-test-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance);
        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(_logger, _socket, _db, _commands, _decoder, _health,
            new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig()),
            new UniFlow.Tests.Common.TestOptionsMonitor<FeatureConfig>(new FeatureConfig()));
        _worker.LoadConfig(aptioConfig);

        _socket.Connected.Returns(true);
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((0, 0L)));
        _db.ResendColumnReady.Returns(true);
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
        _db.GetExhaustedCandidatesAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
        _db.CountPendingAsync().Returns(Task.FromResult(0));
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
    }

    private void SetDelivered()
    {
        _socket.SendAsync(
            Arg.Is<string>(s => s.StartsWith("COMMENT")),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(AptioSocketClient.DeliveredMarker));
    }

    private void SetStatusOk()
    {
        _socket.SendAsync(
            Arg.Is<string>(s => s == "STATUS-REQUEST 1"),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(@"STATUS\18^SRM^1^ON^0000^G^^\"));
    }

    [Fact]
    public async Task Retry_PendingDue_RequeuesInsteadOfSendingComment()
    {
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "&1-18-000001", Send = 1, Disposed = 0, ResendCount = 2 }
            }));
        SetStatusOk();
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));
        SetDelivered();

        await _worker.MaybeRecheckAsync();

        // 放回待发队列（send=0），由正常状态机按窗口一条条发
        await _db.Received(1).RequeueForResendAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001")));
        // 关键：重排队阶段绝不下发丢弃命令
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _out.WriteLine("[requeue] S001 → send=0（等状态机按窗口一条条发）");
    }

    [Fact]
    public async Task Retry_PendingDue_UsesConfiguredInterval()
    {
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));

        await _worker.MaybeRecheckAsync();

        // cutoff = now - ResendIntervalMinutes(5)
        await _db.Received(1).GetResendCandidatesAsync(
            Arg.Is<string>(s => DateTime.ParseExact(s, "yyyyMMddHHmmss", null) <= DateTime.Now.AddMinutes(-4)),
            24,
            Arg.Any<int>());
    }

    [Fact]
    public async Task Retry_NotDue_NoResend()
    {
        // DB 侧按 cutoff 过滤后返回空 → 不重排队、也不发任何指令
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>()));
        SetDelivered();

        await _worker.MaybeRecheckAsync();

        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_NeverSendsDisposeComment()
    {
        // 即使候选命中且状态正常，重排队环节也只发 STATUS-REQUEST，绝不发 COMMENT S002
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "&1-18-000001", Send = 1, Disposed = 0, ResendCount = 2 }
            }));
        SetStatusOk();
        _db.RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(1));
        SetDelivered();

        await _worker.MaybeRecheckAsync();

        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_Exhausted_MarksFailedAndLogsError()
    {
        _db.GetExhaustedCandidatesAsync(24, Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Send = 1, Disposed = 0, ResendCount = 24 },
                new() { Barcode = "S002", Send = 1, Disposed = 0, ResendCount = 30 }
            }));
        _db.MarkFailedAsync(Arg.Any<IReadOnlyCollection<string>>()).Returns(Task.FromResult(2));

        await _worker.MaybeRecheckAsync();

        await _db.Received(1).MarkFailedAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("S001") && c.Contains("S002")));
        // 记录了 Error 级日志（数量 + 示例条码，不含全量条码）
        var err = _logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        err.ShouldNotBeEmpty();
        err.Any(e => e.Message.Contains("2") && e.Message.Contains("S001")).ShouldBeTrue();
    }

    [Fact]
    public async Task Retry_ResendIntervalZero_DisablesRetry()
    {
        var cfg = new AptioConfig
        {
            Ip = "127.0.0.1",
            Port = 2055,
            SrmNodeIds = new() { "18" },
            DisposeSample = new AptioDisposeSampleConfig
            {
                ResendIntervalMinutes = 0,   // 关闭重排队
                MaxResendCount = 24
            }
        };
        _worker.LoadConfig(cfg);

        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "&1-18-000001", Send = 1, Disposed = 0, ResendCount = 2 }
            }));
        SetDelivered();

        await _worker.MaybeRecheckAsync();

        await _db.DidNotReceive().GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Retry_MaxResendCountZero_SkipsExhaustedScan()
    {
        var cfg = new AptioConfig
        {
            Ip = "127.0.0.1",
            Port = 2055,
            SrmNodeIds = new() { "18" },
            DisposeSample = new AptioDisposeSampleConfig
            {
                ResendIntervalMinutes = 5,
                MaxResendCount = 0     // 不限重发次数 → 永远不会"用尽"
            }
        };
        _worker.LoadConfig(cfg);

        await _worker.MaybeRecheckAsync();

        await _db.DidNotReceive().GetExhaustedCandidatesAsync(Arg.Any<int>(), Arg.Any<int>());
        await _db.DidNotReceive().MarkFailedAsync(Arg.Any<IReadOnlyCollection<string>>());
    }

    [Fact]
    public async Task PendingQueue_AboveThreshold_WarnsOnce()
    {
        _db.CountPendingAsync().Returns(Task.FromResult(5000));

        await _worker.MaybeRecheckAsync();
        await _worker.MaybeRecheckAsync();   // 10 分钟节流内不再告警

        _logger.Entries.Count(e => e.Level == LogLevel.Warning
            && e.Message.Contains("Pending dispose queue")).ShouldBe(1);
    }

    [Fact]
    public async Task Resend_ColumnNotReady_SkipsResendAndFailMarking()
    {
        _db.ResendColumnReady.Returns(false);      // 迁移失败 → resend_count 不可用
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "&1-18-000001", Send = 1, Disposed = 0, ResendCount = 1 }
            }));
        _db.GetExhaustedCandidatesAsync(Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S002", Send = 1, Disposed = 0, ResendCount = 99 }
            }));
        SetDelivered();

        await _worker.MaybeRecheckAsync();

        await _db.DidNotReceive().GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>());
        await _db.DidNotReceive().GetExhaustedCandidatesAsync(Arg.Any<int>(), Arg.Any<int>());
        await _db.DidNotReceive().MarkFailedAsync(Arg.Any<IReadOnlyCollection<string>>());
        await _db.DidNotReceive().RequeueForResendAsync(Arg.Any<IReadOnlyCollection<string>>());
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _logger.Entries.Any(e => e.Level == LogLevel.Warning
            && e.Message.Contains("resend_count")).ShouldBeTrue();
    }

    [Fact]
    public async Task Recheck_Cursor_AdvancesThenResetsToZero()
    {
        var call = 0;
        Func<CallInfo, Task<(int, long)>> seq = ci =>
        {
            call++;
            return Task.FromResult(call == 1 ? (0, 42L) : (0, 0L));
        };
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(seq);

        await _worker.RunMaintenanceAsync();
        await _db.Received(1).RecheckPendingDisposalsAsync(200, 0L);

        await _worker.RunMaintenanceAsync();
        await _db.Received(1).RecheckPendingDisposalsAsync(200, 42L);   // 游标推进
    }

    // 捕获日志的 ILogger（断言 Error/Warning 级日志）
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
