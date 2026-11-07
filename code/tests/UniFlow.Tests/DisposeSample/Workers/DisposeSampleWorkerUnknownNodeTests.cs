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
/// 未知节点 + SkipOnUnknownNode=false 的队首卡死用例（P2）。
/// <para>
/// 历史缺陷：跳过 skip 分支后仍走 <c>IsNodeReady</c>（未知节点恒 false）→
/// 记录永远停队首，后续全部阻塞。
/// 现在：未知节点且 skip=false → 跳过 IsNodeReady 判定、直接下发（"照发不误"的本意）。
/// </para>
/// </summary>
public class DisposeSampleWorkerUnknownNodeTests : IDisposable
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IDisposeDatabaseService _db = Substitute.For<IDisposeDatabaseService>();
    private readonly SrmStatusDecoder _decoder = new();
    private readonly DisposeSampleWorker _worker;
    private readonly HealthStore _health;
    private readonly string _healthDbPath;

    public DisposeSampleWorkerUnknownNodeTests()
    {
        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-test-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance);
        var commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _worker = new DisposeSampleWorker(
            NullLogger<DisposeSampleWorker>.Instance, _socket, _db, commands, _decoder, _health,
            new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig()),
            new UniFlow.Tests.Common.TestOptionsMonitor<FeatureConfig>(new FeatureConfig()));

        _socket.Connected.Returns(true);
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
        _db.CountPendingConfirmAsync().Returns(Task.FromResult(0));
    }

    private void LoadConfig(bool skipOnUnknownNode)
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
                SkipOnUnknownNode = skipOnUnknownNode
            }
        };
        _worker.LoadConfig(cfg);
    }

    private void SetStatusResponse(string? response) =>
        _socket.SendAsync(Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

    private void SetDeliveredResponse(string? response) =>
        _socket.SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

    private void SetUnknownNodeRecord()
    {
        _db.SelectOneSendRecordAsync()
            .Returns(Task.FromResult<DisposeStatus?>(new DisposeStatus
            {
                Barcode = "S001",
                Location = "&1-99-000001",   // 节点 99 不在配置的 SrmNodeIds={"18"} 里
                Stype = "S"
            }));
    }

    [Fact]
    public async Task HandleReady_UnknownNode_SkipFalse_SendsDisposeAndAdvancesState()
    {
        LoadConfig(skipOnUnknownNode: false);
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
        SetUnknownNodeRecord();

        await _worker.HandleReadyAsync();

        // 关键：命令已下发（不再被恒 false 的 IsNodeReady 卡住）
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        await _db.Received(1).UpdateStatusAsync("S001", "send");
        await _db.DidNotReceive().UpdateStatusAsync("S001", "skip");
        // 状态推进（不再停在队首）
        _worker.CurrentState.ShouldBe(SysStatus.Ack);
        _worker.CurrentBarcode.ShouldBe("S001");
    }

    [Fact]
    public async Task HandleReady_UnknownNode_SkipFalse_NotDelivered_StaysReady()
    {
        LoadConfig(skipOnUnknownNode: false);
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        SetDeliveredResponse(null);      // 未投递
        SetUnknownNodeRecord();

        await _worker.HandleReadyAsync();

        // 未投递 → 保持 Ready 重试，不推进状态机
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
        await _db.DidNotReceive().UpdateStatusAsync("S001", "send");
    }

    [Fact]
    public async Task HandleReady_UnknownNode_SkipTrue_StillSkipsAndAdvancesQueueHead()
    {
        // 对照组：SkipOnUnknownNode=true 时行为不变（标记 skip 推进队首，不下发丢弃命令）
        LoadConfig(skipOnUnknownNode: true);
        _worker.CurrentState = SysStatus.Ready;
        SetStatusResponse(@"STATUS\18^SRM^1^ON^0000^G^^\");
        SetDeliveredResponse(AptioSocketClient.DeliveredMarker);
        SetUnknownNodeRecord();

        await _worker.HandleReadyAsync();

        await _db.Received(1).UpdateStatusAsync("S001", "skip");
        await _db.DidNotReceive().UpdateStatusAsync("S001", "send");
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
        _worker.CurrentState.ShouldBe(SysStatus.Ready);
    }

    public void Dispose()
    {
        _health.Dispose();
        try { File.Delete(_healthDbPath); } catch { /* ignore */ }
    }
}
