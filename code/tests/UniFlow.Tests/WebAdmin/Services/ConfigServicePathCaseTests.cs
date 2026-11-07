using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin.Services;

/// <summary>
/// 配置路径读取的**大小写不敏感**语义（P1 回归）：
/// <list type="bullet">
/// <item>Navigate 必须在 TryGetProperty 未命中时做大小写不敏感回退（且不能把 cur 置 default 后继续）；</item>
/// <item>不存在的路径 → null（= 新节点），且**不抛异常**；</item>
/// <item>ReadPaths 中某条路径出问题不得影响其它路径的当前值。</item>
/// </list>
/// </summary>
public class ConfigServicePathCaseTests
{
    private static string CreateConfig()
        => TestConfigFile.Create(customize: o => o["Logging"] = JsonNode.Parse(
            """{ "LogLevel": { "Default": "Information", "UniFlow": "Information" } }"""));

    private static ConfigService NewService(string path)
        => new(NullLogger<ConfigService>.Instance, path);

    [Fact]
    public void ReadPath_LowerCasePath_Finds_ObjectNode()
    {
        var svc = NewService(CreateConfig());

        var v = svc.ReadPath("logging:loglevel");

        v.ShouldNotBeNull("大小写不同也必须命中既有节点，否则段节点保护会被旁路");
        v!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
        v.Value.GetProperty("Default").GetString().ShouldBe("Information");
    }

    [Theory]
    [InlineData("logging:loglevel")]
    [InlineData("LOGGING:LOGLEVEL")]
    [InlineData("Logging:LogLevel")]
    [InlineData("logging:LogLevel")]
    public void ReadPath_CaseVariants_AllFind_SameNode(string path)
    {
        var svc = NewService(CreateConfig());

        var v = svc.ReadPath(path);

        v.ShouldNotBeNull();
        v!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public void ReadPath_CaseInsensitive_Leaf_ReturnsValue()
    {
        var svc = NewService(CreateConfig());

        svc.ReadPath("LOGGING:LOGLEVEL:DEFAULT")!.Value.GetString().ShouldBe("Information");
        svc.ReadPath("aptio:database")!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
    }

    [Fact]
    public void ReadPath_UnknownPath_ReturnsNull_AndDoesNotThrow()
    {
        var svc = NewService(CreateConfig());

        svc.ReadPath("logging:loglevel:nosuchkey").ShouldBeNull();
        svc.ReadPath("Aptio:NoSuchSection").ShouldBeNull();
        svc.ReadPath("").ShouldBeNull();
    }

    [Fact]
    public void ReadPaths_MixedCase_Batch_Returns_EachCurrentValue()
    {
        var svc = NewService(CreateConfig());

        var values = svc.ReadPaths(new[]
        {
            "logging:loglevel",            // 大小写不同 → 命中对象节点
            "Aptio:database",              // 小写父节点 → 命中对象节点
            "aptio:srmnodeids",            // 数组节点
            "Logging:LogLevel:Default",    // 叶子
            "logging:nosuch",              // 不存在 → null
        });

        values.Count.ShouldBe(5);
        values["logging:loglevel"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
        values["Aptio:database"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
        values["aptio:srmnodeids"]!.Value.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Array);
        values["Logging:LogLevel:Default"]!.Value.GetString().ShouldBe("Information");
        values["logging:nosuch"].ShouldBeNull();
    }

    [Fact]
    public void ReadPaths_OneBadPath_DoesNotBlank_OtherPaths()
    {
        var svc = NewService(CreateConfig());

        // 混合：正常 + 大小写不同 + 不存在
        var values = svc.ReadPaths(new[] { "Aptio:Ip", "logging:loglevel", "nope:nope" });

        values["Aptio:Ip"].ShouldNotBeNull();
        values["Aptio:Ip"]!.Value.GetString().ShouldBe("127.0.0.1");
        values["logging:loglevel"].ShouldNotBeNull("一条坏路径不得把整批当前值置空");
        values["nope:nope"].ShouldBeNull();
    }

    [Fact]
    public void ReadPath_After_MissingPathRead_StillWorks()
    {
        // 回归：先读一个不存在的路径（曾经会把 cur 置 default → 后续导航抛异常），
        // 再读正常路径，两者都必须正常
        var svc = NewService(CreateConfig());

        svc.ReadPath("nosuch:root").ShouldBeNull();
        svc.ReadPath("logging:loglevel").ShouldNotBeNull();
    }
}
