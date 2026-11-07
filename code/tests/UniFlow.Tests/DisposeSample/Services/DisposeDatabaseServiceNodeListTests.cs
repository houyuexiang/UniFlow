using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// P2-1 兜底：DisposeDatabaseService 取节点时去重（.Distinct()）并去空白，
/// 避免配置绑定异常时对同一节点重复查询。
/// </summary>
public class DisposeDatabaseServiceNodeListTests
{
    private static DisposeDatabaseService CreateService(params string[] nodeIds)
    {
        var monitor = Substitute.For<IOptionsMonitor<AptioConfig>>();
        monitor.CurrentValue.Returns(new AptioConfig { SrmNodeIds = nodeIds.ToList() });
        var factory = new MySqlConnectionFactory("127.0.0.1", 3306, "flexlab", "u", "p");
        return new DisposeDatabaseService(NullLogger<DisposeDatabaseService>.Instance, factory, monitor);
    }

    [Fact]
    public void SrmNodeIds_Duplicates_AreRemoved()
    {
        var svc = CreateService("18", "18", "16");
        svc.SrmNodeIds.ShouldBe(new[] { "18", "16" });
    }

    [Fact]
    public void SrmNodeIds_BlankEntries_AreRemoved()
    {
        var svc = CreateService("18", "", "  ", "16");
        svc.SrmNodeIds.ShouldBe(new[] { "18", "16" });
    }

    [Fact]
    public void SrmNodeIds_Entries_AreTrimmed()
    {
        var svc = CreateService(" 18 ", "16");
        svc.SrmNodeIds.ShouldBe(new[] { "18", "16" });
    }

    [Fact]
    public void SrmNodeIds_NoConfigMonitor_EmptyList()
    {
        var factory = new MySqlConnectionFactory("127.0.0.1", 3306, "flexlab", "u", "p");
        var svc = new DisposeDatabaseService(NullLogger<DisposeDatabaseService>.Instance, factory);
        svc.SrmNodeIds.ShouldBeEmpty();
    }
}
