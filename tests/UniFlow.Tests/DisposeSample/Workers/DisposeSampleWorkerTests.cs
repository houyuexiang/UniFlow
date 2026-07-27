using Shouldly;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.DisposeSample.Workers;

namespace UniFlow.Tests.DisposeSample.Workers;

public class DisposeSampleWorkerTests
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IDisposeDatabaseService _db = Substitute.For<IDisposeDatabaseService>();
    private readonly SrmStatusDecoder _decoder = new();
    private readonly AptioCommandService _commands;
    private readonly DisposeSampleWorker _worker;

    public DisposeSampleWorkerTests()
    {
        var aptioConfig = Options.Create(new AptioConfig { Ip = "127.0.0.1", Port = 2055 });
        var disposeConfig = Options.Create(new DisposeConfig
        {
            SrmNodeId = "09",
            DiscardRunDate = "1,2,3,4,5,6,7",
            DiscardTimeRange = "00:00-23:59,9999;",
            MaxWaitDiscardCount = 3,
            MaxOnetimeSelectDiscardCount = 10,
            AllowSrmErrorCode = "0000"
        });

        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(
            NullLogger<DisposeSampleWorker>.Instance,
            _socket,
            _db,
            _commands,
            _decoder,
            aptioConfig,
            disposeConfig);

        _socket.Connected.Returns(true);
    }

    private void SetStatusResponse(string? response)
    {
        _socket.SendAndReceiveAsync(
            Arg.Is<string>(s => s == "STATUS-REQUEST 1"),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
    }

    private void SetAckResponse(string? response)
    {
        _socket.SendAndReceiveAsync(
            Arg.Is<string>(s => s.StartsWith("COMMENT")),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
    }

    private void SetupReadyToAckTransition()
    {
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = "S001", Location = "&1-09-000001", Stype = "S"
            }));
        SetAckResponse("ACK OK");
    }

    [Fact]
    public async Task HandleNone_ThresholdNotExceeded_StaysInNone()
    {
        _db.CheckSrmSampleCountAsync("09").Returns(Task.FromResult(100));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.None);
    }

    [Fact]
    public async Task HandleNone_ExceedsThreshold_TransitionsToReady()
    {
        _db.CheckSrmSampleCountAsync("09").Returns(Task.FromResult(300));
        _db.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10)
            .Returns(Task.FromResult(new List<SampleRecord>
            {
                new() { Barcode = "S001", Location = "&1-09-000001" }
            }));
        _db.InsertDisposeRecordsAsync(100, Arg.Any<List<SampleRecord>>()).Returns(Task.FromResult(1));

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    [Fact]
    public async Task HandleNone_NoSamplesReturned_StaysInNone()
    {
        _db.CheckSrmSampleCountAsync("09").Returns(Task.FromResult(300));
        _db.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10)
            .Returns(Task.FromResult(new List<SampleRecord>()));

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.None);
    }

    [Fact]
    public async Task HandleReady_StatusOkWithRecord_SendsDisposeAndTransitionsToAck()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetupReadyToAckTransition();

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        _worker.CurrentBarcode.ShouldBe("S001");
        await _db.Received(1).UpdateStatusAsync("S001", "send");
    }

    [Fact]
    public async Task HandleReady_StatusOkNoRecords_TransitionsToNone()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(null));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.None);
    }

    [Fact]
    public async Task HandleReady_SrmOff_StaysInReady()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^OFF^0000^G^^\");

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    [Fact]
    public async Task HandleReady_NullResponse_StaysInReady()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(null);

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    [Fact]
    public async Task HandleReady_NoAckReceived_StaysInReady()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus { Barcode = "S001" }));
        SetAckResponse(null);

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    [Fact]
    public async Task HandleAck_DisposedConfirmed_TransitionsToDispose()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "09").Returns(Task.FromResult(true));

        await _worker.HandleAckAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Dispose);
        await _db.Received(1).UpdateStatusAsync("S001", "disposed");
    }

    [Fact]
    public async Task HandleAck_NotYetDisposedUnderMaxWait_IncrementsCheckcount()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "09").Returns(Task.FromResult(false));
        _db.GetCheckCountAsync("S001").Returns(Task.FromResult(1));

        await _worker.HandleAckAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        await _db.Received(1).UpdateStatusAsync("S001", "checkcount");
    }

    [Fact]
    public async Task HandleAck_ExceedsMaxRetries_TransitionsToDispose()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "09").Returns(Task.FromResult(false));
        _db.GetCheckCountAsync("S001").Returns(Task.FromResult(5));

        await _worker.HandleAckAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Dispose);
    }

    [Fact]
    public async Task HandleAck_EmptyBarcode_TransitionsToDispose()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = null;

        await _worker.HandleAckAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Dispose);
    }

    [Fact]
    public async Task HandleDispose_StatusReceived_TransitionsToNone()
    {
        _worker.CurrentState = SysStatus.Dispose;
        SetStatusResponse("STATUS OK");

        await _worker.HandleDisposeAsync();

        _worker.CurrentState.ShouldBe(SysStatus.None);
    }

    [Fact]
    public async Task HandleDispose_NullResponse_StaysInDispose()
    {
        _worker.CurrentState = SysStatus.Dispose;
        SetStatusResponse(null);

        await _worker.HandleDisposeAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Dispose);
    }
}
