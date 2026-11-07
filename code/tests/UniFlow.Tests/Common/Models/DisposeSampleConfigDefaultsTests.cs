using Shouldly;
using UniFlow.Common.Models;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.Common.Models;

/// <summary>
/// 配置默认值断言（仓库默认值 = 现场口径；现场已有 appsettings.json 的值以现场为准）。
/// </summary>
public class DisposeSampleConfigDefaultsTests
{
    [Fact]
    public void MaxOnetimeSelectDiscardCount_DefaultIsTen()
    {
        // 既是「单轮选择/插入上限」，也是「发送窗口」（未丢弃总数上限）
        new AptioDisposeSampleConfig().MaxOnetimeSelectDiscardCount.ShouldBe(10);
        new AptioConfig().DisposeSample.MaxOnetimeSelectDiscardCount.ShouldBe(10);
    }

    [Fact]
    public void ResendDefaults_Are5And24()
    {
        var dc = new AptioDisposeSampleConfig();
        dc.ResendIntervalMinutes.ShouldBe(5);
        dc.MaxResendCount.ShouldBe(24);
    }

    [Fact]
    public void ResendIntervalMinutes_Default_Is5()
    {
        // 5 分钟批量检查 + 条件重排队（不是 60 分钟、不是直接批量发）
        new AptioDisposeSampleConfig().ResendIntervalMinutes.ShouldBe(5);
        new AptioConfig().DisposeSample.ResendIntervalMinutes.ShouldBe(5);
    }

    [Fact]
    public void QueueDefaults_AreUnlimitedAndWarn1000()
    {
        var dc = new AptioDisposeSampleConfig();
        dc.MaxPendingRecords.ShouldBe(0);            // 0 = 不限
        dc.PendingWarnThreshold.ShouldBe(1000);
    }

    [Fact]
    public void AllowStandbyDispose_Default_IsFalse()
    {
        // 测试开关：默认关闭（保持现状：只有 ON + G/Y 可丢弃）
        new AptioDisposeSampleConfig().AllowStandbyDispose.ShouldBeFalse();
        new AptioConfig().DisposeSample.AllowStandbyDispose.ShouldBeFalse();
    }

    [Fact]
    public void AllowStandbyDispose_ConfigPath_IsHotAndBool()
    {
        // 页面能保存该开关：路径可解析 + 热生效（无需重启）+ 值类型为 bool
        var (level, known, kind) = RestartLevelResolver.ResolveWithKind("Aptio:DisposeSample:AllowStandbyDispose");
        known.ShouldBeTrue();
        level.ShouldBe(RestartLevel.Hot);
        kind.ShouldBe("bool");

        var (normalized, error) = RestartLevelResolver.ValidateValue(kind, System.Text.Json.JsonDocument.Parse("true").RootElement);
        error.ShouldBeNull();
        normalized.ShouldBe(true);
    }
}
