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
}
