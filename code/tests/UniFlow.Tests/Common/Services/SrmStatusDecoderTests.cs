using UniFlow.Common.Services;
using Shouldly;

namespace UniFlow.Tests.Common.Services;

public class SrmStatusDecoderTests
{
    private readonly SrmStatusDecoder _decoder = new();
    private readonly HashSet<string> _allowedErrors = new() { "0000", "0F0A" };

    [Fact]
    public void Decode_OnModeWithGreenStatusAndNoError_ReturnsReady()
    {
        var msg = @"\18^SRM^1^ON^0000^G^^\";
        _decoder.Decode(msg, _allowedErrors, out var status, out var isReady).ShouldBeTrue();
        isReady.ShouldBeTrue();
        status.ShouldBe("Mode:ON Status:G Error:0000");
    }

    [Fact]
    public void Decode_OnModeWithYellowStatusAndNoError_ReturnsReady()
    {
        var msg = @"\18^SRM^1^ON^0000^Y^^\";
        _decoder.Decode(msg, _allowedErrors, out _, out var isReady).ShouldBeTrue();
        isReady.ShouldBeTrue();
    }

    [Fact]
    public void Decode_OffMode_ReturnsNotReady()
    {
        var msg = @"\18^SRM^1^OFF^0000^G^^\";
        _decoder.Decode(msg, _allowedErrors, out var status, out var isReady).ShouldBeTrue();
        isReady.ShouldBeFalse();
        status.ShouldContain("Mode:OFF");
    }

    [Fact]
    public void Decode_DisallowedError_ReturnsNotReady()
    {
        var msg = @"\18^SRM^1^ON^E001^G^^\";
        _decoder.Decode(msg, _allowedErrors, out var status, out var isReady).ShouldBeTrue();
        isReady.ShouldBeFalse();
        status.ShouldContain("Error:E001");
    }

    [Fact]
    public void Decode_AllowedError_TreatedAsZeroError_ReturnsReady()
    {
        var msg = @"\18^SRM^1^ON^0F0A^G^^\";
        _decoder.Decode(msg, _allowedErrors, out _, out var isReady).ShouldBeTrue();
        isReady.ShouldBeTrue();
    }

    [Fact]
    public void Decode_InvalidFormat_ReturnsFalse()
    {
        _decoder.Decode("INVALID", _allowedErrors, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Decode_NoSrmSegment_ReturnsFalse()
    {
        var msg = @"\18^ABC^1^ON^0000^G^^\";
        _decoder.Decode(msg, _allowedErrors, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Decode_TooFewFields_ReturnsFalse()
    {
        var msg = @"\18^SRM^1^ON\";
        _decoder.Decode(msg, _allowedErrors, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Decode_ComplexRealWorldMessage_ParsesCorrectly()
    {
        var msg = "STATUS\\18^SRM^1^ON^0000^G^^\\";
        _decoder.Decode(msg, _allowedErrors, out var status, out var isReady).ShouldBeTrue();
        isReady.ShouldBeTrue();
        status.ShouldBe("Mode:ON Status:G Error:0000");
    }

    // ===== 可丢弃判定与测试开关 AllowStandbyDispose =====
    // 默认（allowStandby=false）：可丢弃 = Mode=="ON" && Status∈{G,Y} && 错误码在允许列表
    // 开启（allowStandby=true）：额外允许 Mode=="STB"（Stand-by）与 Status=="P"（Pink）
    // GTO / PA / OFF / FR / SH / EXE 始终不允许（与开关无关）

    [Theory]
    [InlineData("GTO")]
    [InlineData("PA")]
    [InlineData("OFF")]
    [InlineData("FR")]
    [InlineData("SH")]
    [InlineData("EXE")]
    public void IsNodeReady_NeverAllowedModes_AreNotReady(string mode)
    {
        // 这些模式即使开关开启也不允许丢弃
        var msg = $@"STATUS\18^SRM^1^{mode}^0000^G^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors).ShouldBeFalse();
        _decoder.IsNodeReady(msg, "18", _allowedErrors, allowStandby: true).ShouldBeFalse();
    }

    [Fact]
    public void IsNodeReady_StandByMode_DefaultNotReady()
    {
        // 默认关闭：STB 不参与丢弃判断
        var msg = @"STATUS\18^SRM^1^STB^0000^G^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors).ShouldBeFalse();
    }

    [Fact]
    public void IsNodeReady_PinkStatus_DefaultNotReady()
    {
        // 默认关闭：Pink(P) 不参与丢弃判断
        var msg = @"STATUS\18^SRM^1^ON^0000^P^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors).ShouldBeFalse();
    }

    [Fact]
    public void IsNodeReady_OnGreen_DefaultReady()
    {
        var msg = @"STATUS\18^SRM^1^ON^0000^G^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors).ShouldBeTrue();
    }

    [Theory]
    [InlineData("ON", "G")]
    [InlineData("ON", "Y")]
    [InlineData("STB", "G")]
    [InlineData("STB", "Y")]
    [InlineData("STB", "P")]
    public void IsNodeReady_AllowStandby_AllowsStbAndPink(string mode, string status)
    {
        var msg = $@"STATUS\18^SRM^1^{mode}^0000^{status}^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors, allowStandby: true).ShouldBeTrue();
    }

    [Theory]
    [InlineData("GTO", "G")]
    [InlineData("PA", "G")]
    [InlineData("OFF", "G")]
    [InlineData("FR", "G")]
    [InlineData("SH", "G")]
    [InlineData("EXE", "G")]
    [InlineData("STB", "R")]
    [InlineData("ON", "R")]
    public void IsNodeReady_AllowStandby_StillRejects(string mode, string status)
    {
        var msg = $@"STATUS\18^SRM^1^{mode}^0000^{status}^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors, allowStandby: true).ShouldBeFalse();
    }

    [Fact]
    public void IsNodeReady_AllowStandby_ErrorNotAllowed_NotReady()
    {
        // 开关开启也不能绕过错误码白名单
        var msg = @"STATUS\18^SRM^1^STB^E001^G^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors, allowStandby: true).ShouldBeFalse();
    }

    [Fact]
    public void IsNodeReady_AllowStandby_AllowedError_Ready()
    {
        // 允许列表内的错误码按 0000 处理
        var msg = @"STATUS\18^SRM^1^STB^0F0A^G^^\";
        _decoder.IsNodeReady(msg, "18", _allowedErrors, allowStandby: true).ShouldBeTrue();
    }

    [Fact]
    public void Decode_AllowStandby_FollowsSameRule()
    {
        var msg = @"STATUS\18^SRM^1^STB^0000^P^^\";
        _decoder.Decode(msg, _allowedErrors, out var status, out var isReady).ShouldBeTrue();
        isReady.ShouldBeFalse();                                   // 默认关闭
        _decoder.Decode(msg, _allowedErrors, out status, out isReady, allowStandby: true).ShouldBeTrue();
        isReady.ShouldBeTrue();                                    // 开关开启
        status.ShouldBe("Mode:STB Status:P Error:0000");
    }

    [Fact]
    public void GetNodeMode_ReturnsStandByForLogging()
    {
        var msg = @"STATUS\18^SRM^1^STB^0000^G^^\";
        _decoder.GetNodeMode(msg, "18").ShouldBe("STB");
        _decoder.GetNodeMode(msg, "99").ShouldBe("");
        _decoder.DescribeNode(msg, "18").ShouldBe("mode=STB status=G error=0000");
    }

    [Fact]
    public void FindNode_ReturnsFullStatus_OrNull()
    {
        var msg = @"STATUS\18^SRM^1^ON^0F0A^Y^^\";
        var node = _decoder.FindNode(msg, "18");
        node.ShouldNotBeNull();
        node!.Mode.ShouldBe("ON");
        node.Status.ShouldBe("Y");
        node.Error.ShouldBe("0F0A");
        _decoder.FindNode(msg, "99").ShouldBeNull();
    }
}
