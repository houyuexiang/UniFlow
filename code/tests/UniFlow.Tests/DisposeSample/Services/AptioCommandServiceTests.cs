using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// W1：ACK 语义修复 —— Host LIS 的 COMMENT S002 / ORDER 没有 ACK（手册 §8.59 / §9.3），
/// 成功判定应为「写成功 = 已投递」，即返回值非 null。
/// </summary>
public class AptioCommandServiceTests
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly AptioCommandService _service;

    public AptioCommandServiceTests()
    {
        _service = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
    }

    private void SetReply(string? reply)
    {
        _socket.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reply));
    }

    [Fact]
    public async Task SendDisposeAsync_DeliveredMarker_ReturnsTrue()
    {
        SetReply(AptioSocketClient.DeliveredMarker);
        (await _service.SendDisposeAsync("S001")).ShouldBeTrue();
    }

    [Fact]
    public async Task SendDisposeAsync_Null_ReturnsFalse()
    {
        SetReply(null);
        (await _service.SendDisposeAsync("S001")).ShouldBeFalse();
    }

    [Fact]
    public async Task SendDeliverAsync_DeliveredMarker_ReturnsTrue()
    {
        SetReply(AptioSocketClient.DeliveredMarker);
        (await _service.SendDeliverAsync("S001")).ShouldBeTrue();
    }

    [Fact]
    public async Task SendDeliverAsync_Null_ReturnsFalse()
    {
        SetReply(null);
        (await _service.SendDeliverAsync("S001")).ShouldBeFalse();
    }

    [Fact]
    public async Task SendStatPriorityAsync_DeliveredMarker_ReturnsTrue()
    {
        SetReply(AptioSocketClient.DeliveredMarker);
        (await _service.SendStatPriorityAsync("S001")).ShouldBeTrue();
    }

    [Fact]
    public async Task SendCancelTestAsync_DeliveredMarker_ReturnsTrue()
    {
        SetReply(AptioSocketClient.DeliveredMarker);
        (await _service.SendCancelTestAsync("S001", "BNP")).ShouldBeTrue();
    }

    [Fact]
    public async Task SendCancelTestAsync_Null_ReturnsFalse()
    {
        SetReply(null);
        (await _service.SendCancelTestAsync("S001", "BNP")).ShouldBeFalse();
    }

    [Fact]
    public async Task SendDisposeAsync_AckShapedReply_StillTreatedAsDelivered()
    {
        // 回复内容与成功判定无关：任何非 null 回复都算已投递（旧实现依赖 ACK 前缀匹配，已删除）
        SetReply("ACK OK");
        (await _service.SendDisposeAsync("S001")).ShouldBeTrue();
    }
}
