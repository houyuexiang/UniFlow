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

namespace UniFlow.Tests.DisposeSample.Workers;

/// <summary>
/// DB 状态写入失败（UpdateStatusAsync 返回 false）的传播行为：
/// <b>不得静默</b> —— 必须记 Error/health，且状态机<b>不推进</b>（否则 DB 仍是 send=0 → 下一轮重复下发）。
/// </summary>
public class DisposeSampleWorkerStatusUpdateFailureTests : IDisposable
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

    public DisposeSampleWorkerStatusUpdateFailureTests()
    {
        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-statusfail-{Guid.NewGuid():N}.db");
        // 传入自己的 ErrorReporter：测试里手动扮演 ErrorCollectorWorker 把错误写库
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance, 30, _reporter);
        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(_logger, _socket, _db, _commands, _decoder, _health,
            new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig()),
            new UniFlow.Tests.Common.TestOptionsMonitor<FeatureConfig>(new FeatureConfig()));
        _worker.LoadConfig(new AptioConfig
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
            }
        });

        _socket.Connected.Returns(true);
        _socket.SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(@"STATUS\18^SRM^1^ON^0000^G^^\"));
        _socket.SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(AptioSocketClient.DeliveredMarker));
    }

    private void SetupRecord(string barcode = "S001", string location = "&1-18-000001")
        => _db.SelectOneSendRecordAsync().Returns(Task.FromResult<DisposeStatus?>(
            new DisposeStatus { Barcode = barcode, Location = location }));

    /// <summary>
    /// 取出错误队列里的条目并写库（扮演生产里的 ErrorCollectorWorker），再读取错误记录。
    /// </summary>
    private async Task<List<string>> ErrorMessagesAsync()
    {
        while (_reporter.Reader.TryRead(out var entry))
            await _health.WriteErrorAsync(entry.Module, entry.Level, entry.Message);
        var errors = await _health.GetErrorsAsync(1, "DisposeSample");
        return errors.Select(e => e["message"]?.ToString() ?? "").ToList();
    }

    /// <summary>健康状态记录（degraded 等）—— RecordHealthAsync 是直接写库的。</summary>
    private async Task<List<string>> HealthMessagesAsync()
    {
        var history = await _health.GetHealthHistoryAsync(1, "DisposeSample");
        return history.Select(h => $"{h["status"]}|{h["message"]}").ToList();
    }

    [Fact]
    public async Task HandleReady_SendUpdateFails_StaysReady_AndRecordsError()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetupRecord();
        // DB 写入失败
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        await _worker.HandleReadyAsync();

        // 关键：不得进 Ack（DB 仍是 send=0 → 下一轮会重复下发）
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        (await ErrorMessagesAsync()).ShouldNotBeEmpty();
        (await ErrorMessagesAsync()).Any(m => m.Contains("send") && m.Contains("S001")).ShouldBeTrue();
        // 健康状态必须记 degraded（不能静默）
        (await HealthMessagesAsync()).Any(m => m.StartsWith("degraded") && m.Contains("S001")).ShouldBeTrue();
        // 日志必须显式说明"命令已投递但 DB 未更新 → 可能重复下发"
        _logger.Entries.Any(e => e.Level == LogLevel.Error
                                 && e.Message.Contains("status update FAILED")
                                 && e.Message.Contains("re-sent")).ShouldBeTrue();
    }

    [Fact]
    public async Task HandleReady_SendUpdateSucceeds_TransitionsToAck()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetupRecord();
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        (await ErrorMessagesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleReady_SkipUpdateFails_RecordsError_AndStaysReady()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetupRecord("S001", "&1-99-000001");   // 未知节点 → skip 分支
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        (await ErrorMessagesAsync()).Any(m => m.Contains("skip")).ShouldBeTrue();
        _logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("status update FAILED")).ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAck_DisposedUpdateFails_StaysAck_AndRecordsError()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "18").Returns(Task.FromResult(true));
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        await _worker.HandleAckAsync();

        // 不推进（DB 仍 disposed=0 → 下一轮继续确认）
        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        (await ErrorMessagesAsync()).Any(m => m.Contains("disposed")).ShouldBeTrue();
    }

    [Fact]
    public async Task HandleAck_CheckcountUpdateFails_StaysAck_AndRecordsError()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "18").Returns(Task.FromResult(false));
        _db.GetCheckCountAsync("S001").Returns(Task.FromResult(1));
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        await _worker.HandleAckAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        (await ErrorMessagesAsync()).Any(m => m.Contains("checkcount")).ShouldBeTrue();
    }

    [Fact]
    public async Task MaybeRequeue_SkipUpdateFails_RecordsError()
    {
        _db.ResendColumnReady.Returns(true);
        _db.CountPendingAsync().Returns(Task.FromResult(0));
        _db.GetResendCandidatesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromResult(new List<DisposeStatus>
            {
                new() { Barcode = "S001", Location = "NONODE" }   // 位置里取不到节点号 → skip 分支
            }));
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));

        var affected = await _worker.MaybeRequeueForResendAsync();

        affected.ShouldBe(0);
        (await ErrorMessagesAsync()).Any(m => m.Contains("skip")).ShouldBeTrue();
        _logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("status update FAILED")).ShouldBeTrue();
    }

    public void Dispose()
    {
        _health.Dispose();
        try { File.Delete(_healthDbPath); } catch { /* ignore */ }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
