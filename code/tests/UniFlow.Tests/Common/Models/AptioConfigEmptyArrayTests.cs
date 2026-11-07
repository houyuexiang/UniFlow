using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using UniFlow.Common.Models;

namespace UniFlow.Tests.Common.Models;

/// <summary>
/// M-04 的配套回归：配置绑定的「空数组」语义必须与模型定义一致。
/// <para>
/// 陷阱：<b>ConfigurationBinder 无法区分「空数组」与「键不存在」</b>（两者都留 null），
/// 而 <c>System.Text.Json</c> 会给出空集合。模型语义（<c>AptioDisposeSampleConfig.GetRunDays</c>）：
/// <c>!= null</c> 即权威，<b>空数组 = 显式清空/禁用</b>，<c>null</c> 才回退旧格式字符串。
/// 不还原 → 页面写 <c>"RunDays": []</c>（提示「全部取消 = 功能不运行」）
/// → 绑定成 null → 回退旧串 → <b>丢弃功能反而被打开</b>（危险）。
/// </para>
/// </summary>
public class AptioConfigEmptyArrayTests
{
    private static IConfiguration BuildConfig(string aptioJson)
    {
        var path = Path.Combine(Path.GetTempPath(), "uniflow-emptyarray-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"Aptio\":" + aptioJson + "}");
        return new ConfigurationBuilder().AddJsonFile(path).Build();
    }

    [Fact]
    public void EmptyRunDays_IsTreatedAs_ExplicitlyDisabled_NotFallbackToLegacy()
    {
        var config = BuildConfig("{\"DisposeSample\":{\"RunDays\":[],\"DiscardRunDate\":\"1,2,3,4,5,6,7\"}}");

        var bound = global::Program.BindAptioConfig(config);

        // 关键：空数组 = 显式清空 → GetRunDays() 必须为空（功能不运行），不能回退旧串
        bound.DisposeSample.RunDays.ShouldNotBeNull();
        bound.DisposeSample.RunDays.ShouldBeEmpty();
        bound.DisposeSample.GetRunDays().ShouldBeEmpty("空数组 = 显式禁用（不得回退旧串把功能打开）");
    }

    [Fact]
    public void EmptyAllowedSrmErrorCodes_IsTreatedAs_ExplicitlyEmpty()
    {
        var config = BuildConfig("{\"DisposeSample\":{\"AllowedSrmErrorCodes\":[],\"AllowSrmErrorCode\":\"0000,0F0A\"}}");

        var bound = global::Program.BindAptioConfig(config);

        bound.DisposeSample.AllowedSrmErrorCodes.ShouldNotBeNull();
        bound.DisposeSample.GetAllowedErrors().ShouldBeEmpty("空数组 = 显式清空（不得回退旧串）");
    }

    [Fact]
    public void EmptyTimeRanges_IsTreatedAs_ExplicitlyEmpty()
    {
        var config = BuildConfig("{\"DisposeSample\":{\"TimeRanges\":[],\"DiscardTimeRange\":\"01:30-05:45,3800;\"}}");

        var bound = global::Program.BindAptioConfig(config);

        bound.DisposeSample.TimeRanges.ShouldNotBeNull();
        bound.DisposeSample.GetTimeRanges().ShouldBeEmpty("空数组 = 显式清空（不得回退旧串）");
    }

    [Fact]
    public void AbsentKey_StillFallsBackToLegacyString()
    {
        // 反向对照：键**不存在** → null → 回退旧格式字符串（既有行为不变）
        var config = BuildConfig("{\"DisposeSample\":{\"DiscardRunDate\":\"1,2,3,4,5,6,7\"}}");

        var bound = global::Program.BindAptioConfig(config);

        bound.DisposeSample.RunDays.ShouldBeNull();
        bound.DisposeSample.GetRunDays().ShouldBe(new[] { 1, 2, 3, 4, 5, 6, 7 });
    }

    [Fact]
    public void OptionsMonitor_UsesSameBinding_AsBindAptioConfig()
    {
        // 两个入口必须同口径（M-04：消除配置双源）→ monitor 与 BindAptioConfig 结果一致
        var config = BuildConfig("{\"SrmNodeIds\":[\"16\"],\"DisposeSample\":{\"RunDays\":[],\"DiscardRunDate\":\"1,2\"}}");

        var services = new ServiceCollection();
        global::Program.ConfigureAptioOptions(services, config);
        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<AptioConfig>>().CurrentValue;
        var direct = global::Program.BindAptioConfig(config);

        monitor.SrmNodeIds.ShouldBe(direct.SrmNodeIds);
        monitor.DisposeSample.RunDays.ShouldBe(direct.DisposeSample.RunDays);
        monitor.DisposeSample.GetRunDays().ShouldBeEmpty();
        monitor.DisposeSample.GetRunDays().ShouldBe(direct.DisposeSample.GetRunDays());
    }
}
