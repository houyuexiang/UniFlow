using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using UniFlow.Common.Models;

namespace UniFlow.Tests.Common.Models;

/// <summary>
/// P2-1 回归：AptioConfig.SrmNodeIds 的默认值 ["09"] 会被 ConfigurationBinder「追加」而不是「覆盖」
/// （实测：配置 ["09","16"] → 绑定结果 ["09","09","16"]；配置 ["16"] → ["09","16"]，多出幽灵节点 09）。
/// 节点列表直接决定 DisposeSample 补查（W4）查哪些 SRM 节点，必须与 appsettings.json 完全一致。
/// </summary>
public class AptioConfigBindingTests
{
    private static IConfiguration BuildConfig(params string[] srmNodeIds)
    {
        var data = new Dictionary<string, string?>
        {
            ["Aptio:Ip"] = "10.0.0.1",
            ["Aptio:Port"] = "2055",
        };
        for (var i = 0; i < srmNodeIds.Length; i++)
            data[$"Aptio:SrmNodeIds:{i}"] = srmNodeIds[i];
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void ConfigureAptioOptions_SrmNodeIds_MatchesConfigurationExactly()
    {
        var config = BuildConfig("16");
        var services = new ServiceCollection();

        // 与 Program.cs 生产注册完全相同的入口
        global::Program.ConfigureAptioOptions(services, config);

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AptioConfig>>().CurrentValue;

        // 期望 ["16"]；若沿用旧的 services.Configure<AptioConfig>(section) 会得到 ["09","16"]
        options.SrmNodeIds.ShouldBe(new[] { "16" });
    }

    [Fact]
    public void ConfigureAptioOptions_SrmNodeIds_MultipleNodes_NoDuplicates()
    {
        var config = BuildConfig("09", "16");
        var services = new ServiceCollection();
        global::Program.ConfigureAptioOptions(services, config);

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AptioConfig>>().CurrentValue;

        options.SrmNodeIds.ShouldBe(new[] { "09", "16" });
    }

    [Fact]
    public void ConfigureAptioOptions_OtherFields_StillBound()
    {
        var config = BuildConfig("16");
        var services = new ServiceCollection();
        global::Program.ConfigureAptioOptions(services, config);

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<AptioConfig>>().CurrentValue;

        options.Ip.ShouldBe("10.0.0.1");
        options.Port.ShouldBe(2055);
    }

    [Fact]
    public void ConfigureAptioOptions_RefreshesOnConfigurationChange()
    {
        var config = BuildConfig("16");
        var services = new ServiceCollection();
        global::Program.ConfigureAptioOptions(services, config);

        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<AptioConfig>>();
        monitor.CurrentValue.SrmNodeIds.ShouldBe(new[] { "16" });

        // 配置变更 → monitor 刷新（变更令牌已注册）。
        // 注：MemoryConfigurationProvider 的 Set() 不触发 reload 令牌，需显式 Reload()；
        //     生产用的是 reloadOnChange 的 JSON provider（写文件即触发），已实测可用。
        config["Aptio:SrmNodeIds:0"] = "18";
        config["Aptio:SrmNodeIds:1"] = "20";
        (config as IConfigurationRoot)!.Reload();

        monitor.CurrentValue.SrmNodeIds.ShouldBe(new[] { "18", "20" });
    }

    [Fact]
    public void BindAptioConfig_SrmNodeIds_MatchesConfigurationExactly()
    {
        // Program.cs 内层宿主用的 AptioConfig 实例（services.AddSingleton(aptio)）
        var bound = global::Program.BindAptioConfig(BuildConfig("16"));
        bound.SrmNodeIds.ShouldBe(new[] { "16" });

        var bound2 = global::Program.BindAptioConfig(BuildConfig("09", "16"));
        bound2.SrmNodeIds.ShouldBe(new[] { "09", "16" });
    }

    [Fact]
    public void BindAptioConfig_DefaultBinding_WouldProduceGhostNode()
    {
        // 记录「旧写法」的对照行为：ConfigurationBinder 追加默认列表 → 幽灵节点 09
        var legacy = BuildConfig("16").GetSection("Aptio").Get<AptioConfig>();
        legacy!.SrmNodeIds.ShouldBe(new[] { "09", "16" });
    }
}
