using Shouldly;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.DisposeSample.Workers;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.DisposeSample.Workers;

public class DisposeSampleWorkerTests : IDisposable
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IDisposeDatabaseService _db = Substitute.For<IDisposeDatabaseService>();
    private readonly SrmStatusDecoder _decoder = new();
    private readonly AptioCommandService _commands;
    private readonly DisposeSampleWorker _worker;
    private readonly HealthStore _health;
    private readonly string _healthDbPath;

    public DisposeSampleWorkerTests()
    {
        var aptioConfig = new AptioConfig
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
                SkipOnUnknownNode = true
            }
        };

        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-test-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance);
        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(
            NullLogger<DisposeSampleWorker>.Instance,
            _socket,
            _db,
            _commands,
            _decoder,
            _health,
            new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig()),
            new UniFlow.Tests.Common.TestOptionsMonitor<FeatureConfig>(new FeatureConfig()));
        _worker.LoadConfig(aptioConfig);

        _socket.Connected.Returns(true);
        // 默认：DB 状态写入成功（失败路径由专门用例覆盖）
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
    }

    private void SetStatusResponse(string? response)
    {
        _socket.SendAsync(
            Arg.Is<string>(s => s == "STATUS-REQUEST 1"),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
    }

    // 业务命令（COMMENT/ORDER）无 ACK：写成功=已投递，返回 AptioSocketClient.DeliveredMarker
    // （手册 §8.59：ACK/NAK 仅 Dream→GUI 通知，覆盖 6 条 GUI 命令，与 Host LIS 无关；§9.3：S002 无回复定义）
    private void SetDeliveredResponse(string? response)
    {
        _socket.SendAsync(
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
                Barcode = "S001", Location = "&1-18-000001", Stype = "S"
            }));
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
    }

    [Fact]
    public async Task HandleNone_ThresholdNotExceeded_StaysInNone()
    {
        _db.CheckSrmSampleCountAsync("18").Returns(Task.FromResult(100));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.None);
    }

    [Fact]
    public async Task HandleNone_ExceedsThreshold_TransitionsToReady()
    {
        _db.CheckSrmSampleCountAsync("18").Returns(Task.FromResult(300));
        _db.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10)
            .Returns(Task.FromResult(new List<SampleRecord>
            {
                new() { Barcode = "S001", Location = "&1-18-000001" }
            }));
        _db.InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>()).Returns(Task.FromResult(1));

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    [Fact]
    public async Task HandleNone_NoSamplesReturned_StaysInNone()
    {
        _db.CheckSrmSampleCountAsync("18").Returns(Task.FromResult(300));
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
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus { Barcode = "S001", Location = "&1-18-000001" }));

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
    public async Task HandleReady_NotDelivered_StaysInReady()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus { Barcode = "S001", Location = "&1-18-000001" }));
        SetDeliveredResponse(null);

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.DidNotReceive().UpdateStatusAsync("S001", "send");
    }

    [Fact]
    public async Task HandleReady_SendThrows_StaysInReady()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus { Barcode = "S001", Location = "&1-18-000001" }));
        _socket.SendAsync(
            Arg.Is<string>(s => s.StartsWith("COMMENT")),
            Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string?>(new IOException("simulated write failure")));

        await Should.ThrowAsync<IOException>(() => _worker.HandleReadyAsync());

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.DidNotReceive().UpdateStatusAsync("S001", "send");
    }

    [Fact]
    public async Task HandleReady_UnknownNodeSkip_MarksSkipAndAdvancesQueueHead()
    {
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");

        // 队首是未知节点（node 99 不在配置的 SrmNodeIds={"18"} 中），下一条是正常记录
        var call = 0;
        _db.SelectOneSendRecordAsync().Returns(_ =>
        {
            call++;
            return Task.FromResult<DisposeStatus?>(call == 1
                ? new DisposeStatus { Barcode = "S001", Location = "&1-99-000001" }
                : new DisposeStatus { Barcode = "S002", Location = "&1-18-000002" });
        });
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);

        // 第 1 轮：未知节点 → 标记 skip（send=2），状态机仍为 Ready
        await _worker.HandleReadyAsync();
        await _db.Received(1).UpdateStatusAsync("S001", "skip");
        await _db.DidNotReceive().UpdateStatusAsync("S001", "send");
        _worker.CurrentState.ShouldBe(SysStatus.Ready);

        // 第 2 轮：队首已推进 → 选中下一条 S002 并正常投递
        await _worker.HandleReadyAsync();
        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        _worker.CurrentBarcode.ShouldBe("S002");
        await _db.Received(1).UpdateStatusAsync("S002", "send");
        await _db.DidNotReceive().UpdateStatusAsync("S002", "skip");
    }

    [Fact]
    public async Task HandleNone_WithPendingSend_TransitionsToReady_WithoutInsert()
    {
        // 已有 send=0 记录（候选条码已存在 → inserted=0）：必须先发完，不能卡在 None
        _db.CountPendingSendAsync().Returns(Task.FromResult(5));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.DidNotReceive().InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>());
        await _db.DidNotReceive().CheckSrmSampleCountAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNone_NoPendingSend_KeepsOldBehavior()
    {
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);
        _db.CheckSrmSampleCountAsync("18").Returns(Task.FromResult(300));
        _db.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10)
            .Returns(Task.FromResult(new List<SampleRecord>
            {
                new() { Barcode = "S001", Location = "&1-18-000001" }
            }));
        _db.InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>()).Returns(Task.FromResult(1));

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.Received(1).CheckSrmSampleCountAsync("18");
        // need=100，但窗口 10 且 active=0 → 插入上限 = min(100, 10, samples=1) = 1
        await _db.Received(1).InsertDisposeRecordsAsync(1, Arg.Any<List<SampleRecord>>());
    }

    [Fact]
    public async Task HandleNone_MaxPendingRecordsReached_SkipsInsert()
    {
        // 待处理队列达到硬上限 → 不再插入新记录
        // （窗口设为 0 = 不限，隔离出 MaxPendingRecords 这一道闸门）
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingAsync().Returns(Task.FromResult(100));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        var cfg = new AptioConfig
        {
            Ip = "127.0.0.1",
            Port = 2055,
            SrmNodeIds = new() { "18" },
            DisposeSample = new AptioDisposeSampleConfig
            {
                CommandType = 0,
                CommandName = "view_overtimestoragesample",
                MaxOnetimeSelectDiscardCount = 0,   // 窗口不限
                MaxPendingRecords = 100
            }
        };
        _worker.LoadConfig(cfg);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.None);
        await _db.DidNotReceive().InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>());
    }

    // ===== 发送窗口（MaxOnetimeSelectDiscardCount 同时是未丢弃总数上限）=====

    [Fact]
    public async Task HandleNone_WindowFull_DoesNotInsert()
    {
        // active = 10（send=0 待发 + send=1 且 disposed=0）≥ 窗口 10 → 等待，不插入、不统计
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingAsync().Returns(Task.FromResult(10));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.None);
        await _db.DidNotReceive().InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>());
        await _db.DidNotReceive().CheckSrmSampleCountAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNone_WindowHasRoom_InsertsUpToWindow()
    {
        // active = 8、窗口 = 10 → 本轮最多再插入 2 条（need 参数 = 2）
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingAsync().Returns(Task.FromResult(8));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);
        _db.CheckSrmSampleCountAsync("18").Returns(Task.FromResult(300));
        _db.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10)
            .Returns(Task.FromResult(new List<SampleRecord>
            {
                new() { Barcode = "S001", Location = "&1-18-000001" },
                new() { Barcode = "S002", Location = "&1-18-000002" },
                new() { Barcode = "S003", Location = "&1-18-000003" }
            }));
        _db.InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>()).Returns(Task.FromResult(2));

        await _worker.HandleNoneAsync(200);

        await _db.Received(1).InsertDisposeRecordsAsync(2, Arg.Any<List<SampleRecord>>());
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    [Fact]
    public async Task HandleReady_WindowFull_DoesNotSendStatusOrDispose()
    {
        // 在途（send=1 且 disposed=0）= 10 ≥ 窗口 10 → 本轮不发任何命令
        _worker.CurrentState = SysStatus.Ready;
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(10));
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _db.DidNotReceive().SelectOneSendRecordAsync();
    }

    [Fact]
    public async Task HandleReady_WindowNotFull_Sends()
    {
        // 在途 = 9 < 窗口 10 → 正常发送一条
        _worker.CurrentState = SysStatus.Ready;
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(9));
        SetupReadyToAckTransition();

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(1).UpdateStatusAsync("S001", "send");
    }

    // ===== 测试开关 AllowStandbyDispose（默认关；开启后 STB / Pink 也可丢弃）=====

    private void LoadConfigWithStandby(bool allowStandby)
    {
        var cfg = new AptioConfig
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
                AllowStandbyDispose = allowStandby
            }
        };
        _worker.LoadConfig(cfg);
    }

    [Fact]
    public async Task HandleReady_StandBy_SwitchOff_DoesNotSend()
    {
        // 默认（开关关）：STB 不可丢弃 → 不下发丢弃命令，状态保持 Ready
        LoadConfigWithStandby(false);
        _worker.CurrentState = SysStatus.Ready;
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        SetStatusResponse(@"STATUS\18^SRM^1^STB^0000^G^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = "S001", Location = "&1-18-000001", Stype = "S"
            }));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReady_StandBy_SwitchOn_Sends()
    {
        // 开关开：STB 参与丢弃判断 → 正常下发
        LoadConfigWithStandby(true);
        _worker.CurrentState = SysStatus.Ready;
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        SetStatusResponse(@"STATUS\18^SRM^1^STB^0000^G^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = "S001", Location = "&1-18-000001", Stype = "S"
            }));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(1).UpdateStatusAsync("S001", "send");
    }

    [Fact]
    public async Task HandleReady_Pink_SwitchOn_Sends()
    {
        // 开关开：Pink(P) 参与丢弃判断
        LoadConfigWithStandby(true);
        _worker.CurrentState = SysStatus.Ready;
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^P^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = "S001", Location = "&1-18-000001", Stype = "S"
            }));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReady_Gto_SwitchOn_DoesNotSend()
    {
        // GTO 始终不允许（与开关无关）
        LoadConfigWithStandby(true);
        _worker.CurrentState = SysStatus.Ready;
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        SetStatusResponse(@"STATUS\18^SRM^1^GTO^0000^G^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = "S001", Location = "&1-18-000001", Stype = "S"
            }));

        await _worker.HandleReadyAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleNone_PendingSendWithActiveAtWindow_TransitionsToReady()
    {
        // 窗口里正好是 10 条待发（active=10、pendingSend=10、inFlight=0）
        // → 必须转 Ready 先发（窗口只拦插入，不能拦发送）——否则永远卡在 None
        _db.CountPendingSendAsync().Returns(Task.FromResult(10));
        _db.CountPendingAsync().Returns(Task.FromResult(10));
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.DidNotReceive().InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>());
        await _db.DidNotReceive().CheckSrmSampleCountAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNone_NoPendingSend_ActiveAtWindow_StaysNone()
    {
        // 没有待发 + active=10（窗口 10）→ 窗口满，不插入，状态保持 None
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingAsync().Returns(Task.FromResult(10));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.None);
        await _db.DidNotReceive().InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>());
        await _db.DidNotReceive().CheckSrmSampleCountAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task HandleNone_PendingSendWithInFlightFull_TransitionsToReady()
    {
        // active=13（10 在途 + 3 待发）、pendingSend=3、inFlight=10
        // → HandleNone 必须转 Ready（随后 HandleReady 会因在途窗口满而等待，这里只验证不卡死）
        _db.CountPendingSendAsync().Returns(Task.FromResult(3));
        _db.CountPendingAsync().Returns(Task.FromResult(13));
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(10));
        _db.DeleteHistoryAsync().Returns(Task.CompletedTask);

        await _worker.HandleNoneAsync(200);

        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.DidNotReceive().InsertDisposeRecordsAsync(Arg.Any<int>(), Arg.Any<List<SampleRecord>>());
    }

    [Fact]
    public async Task MaybeRecheck_ThrottlesWithin60Seconds()
    {
        _db.RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>()).Returns(Task.FromResult((3, 0L)));

        (await _worker.MaybeRecheckAsync()).ShouldBe(3);
        await _db.Received(1).RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>());

        // 60 秒内再次调用 → 节流，不查库
        (await _worker.MaybeRecheckAsync()).ShouldBe(0);
        await _db.Received(1).RecheckPendingDisposalsAsync(Arg.Any<int>(), Arg.Any<long>());
    }

    [Fact]
    public async Task HandleAck_DisposedConfirmed_TransitionsToDispose()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "18").Returns(Task.FromResult(true));

        await _worker.HandleAckAsync();

        _worker.CurrentState.ShouldBe(SysStatus.Dispose);
        await _db.Received(1).UpdateStatusAsync("S001", "disposed");
    }

    [Fact]
    public async Task HandleAck_NotYetDisposedUnderMaxWait_IncrementsCheckcount()
    {
        _worker.CurrentState = SysStatus.Ack;
        _worker.CurrentBarcode = "S001";
        _db.CheckDisposedAsync("S001", "18").Returns(Task.FromResult(false));
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
        _db.CheckDisposedAsync("S001", "18").Returns(Task.FromResult(false));
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

    public void Dispose()
    {
        _health.Dispose();
        try { File.Delete(_healthDbPath); } catch { /* ignore */ }
    }
}
