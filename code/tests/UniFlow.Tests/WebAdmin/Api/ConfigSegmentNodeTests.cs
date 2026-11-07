using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace UniFlow.Tests.WebAdmin.Api;

/// <summary>
/// 配置写入的**段节点保护**回归测试：
/// 不能把对象/数组节点（段节点）替换成标量，否则配置文件结构损坏。
/// </summary>
public class ConfigSegmentNodeTests
{
    // 测试配置带 Logging:LogLevel 对象（与真实 appsettings.json 结构一致）
    private static Task<TestWebHost> StartAsync() => TestWebHost.StartAsync(
        customize: o => o["Logging"] = JsonNode.Parse(
            """{ "LogLevel": { "Default": "Information", "UniFlow": "Information" } }"""));

    private static JsonNode? LoadJson(string path) => JsonNode.Parse(File.ReadAllText(path));

    [Fact]
    public async Task PutConfig_SegmentNode_WithScalar_Returns400_AndFileUnchanged()
    {
        await using var host = await StartAsync();
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "Logging:LogLevel", value = "Warning" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("Logging:LogLevel");                 // 错误信息含 path
        body.ShouldContain("cannot replace an object/array node with a scalar");
        body.ShouldContain("edit a leaf path");

        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
    }

    [Fact]
    public async Task PutConfig_LeafUnderSegmentNode_Succeeds()
    {
        await using var host = await StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "Logging:LogLevel:Default", value = "Warning" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        TestConfigFile.ReadString(host.ConfigPath, "Logging:LogLevel:Default").ShouldBe("Warning");
        // 同级子键未丢
        TestConfigFile.ReadString(host.ConfigPath, "Logging:LogLevel:UniFlow").ShouldBe("Information");
    }

    [Fact]
    public async Task PutConfig_ArrayLeaf_Succeeds()
    {
        await using var host = await StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "Aptio:SrmNodeIds", value = new[] { "09", "16" } });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var node = LoadJson(host.ConfigPath)!["Aptio"]!["SrmNodeIds"]!.AsArray();
        node.Count.ShouldBe(2);
        node[0]!.GetValue<string>().ShouldBe("09");
    }

    [Fact]
    public async Task BatchConfig_SegmentNode_WithScalar_Returns400_AndNothingWritten()
    {
        await using var host = await StartAsync();
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        // 一条合法 + 一条段节点被标量化 → 全量校验失败 → 一个字段都不写
        var body = new
        {
            changes = new object[]
            {
                new { path = "Aptio:Ip", value = "10.30.40.50" },
                new { path = "Logging:LogLevel", value = "Warning" },
            }
        };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldContain("Logging:LogLevel");
        text.ShouldContain("cannot replace an object/array node with a scalar");

        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Ip").ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task BatchConfig_LeafUnderSegmentNode_Succeeds()
    {
        await using var host = await StartAsync();

        var body = new
        {
            changes = new object[]
            {
                new { path = "Logging:LogLevel:Default", value = "Debug" },
                new { path = "Aptio:Ip", value = "10.30.40.50" },
            }
        };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(2);
        TestConfigFile.ReadString(host.ConfigPath, "Logging:LogLevel:Default").ShouldBe("Debug");
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Ip").ShouldBe("10.30.40.50");
    }

    [Fact]
    public async Task BatchConfig_SegmentNode_WithSameKindObject_IsRejectedByTypeValidation()
    {
        // 对象→对象虽然不触发段节点保护，但类型校验（kind=unknown）同样拒绝
        await using var host = await StartAsync();

        var body = new { changes = new object[] { new { path = "Logging:LogLevel", value = new { Default = "Warning" } } } };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutConfig_UnknownSegmentNode_IsRejected()
    {
        // 当前不存在的节点 → 允许新建（不误伤）
        await using var host = await StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "Logging:LogLevel:BrandNew", value = "Warning" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        TestConfigFile.ReadString(host.ConfigPath, "Logging:LogLevel:BrandNew").ShouldBe("Warning");
    }

    // ===== P1：大小写 path 绕过段节点保护（logging:loglevel → 200 且子键全丢）=====

    [Theory]
    [InlineData("logging:loglevel")]
    [InlineData("LOGGING:LOGLEVEL")]
    [InlineData("Logging:loglevel")]
    public async Task PutConfig_CaseVariant_SegmentNode_Returns400_AndFileUnchanged(string path)
    {
        await using var host = await StartAsync();
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config", new { path, value = "Warning" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldContain("cannot replace an object/array node with a scalar");
        text.ShouldContain(path);

        // 文件不变，且 LogLevel 仍是对象（子键未丢）
        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
        var node = LoadJson(host.ConfigPath)!["Logging"]!["LogLevel"];
        node.ShouldNotBeNull();
        node!.AsObject().Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task BatchConfig_CaseVariant_SegmentNode_Returns400_AndNothingWritten()
    {
        await using var host = await StartAsync();
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        var body = new
        {
            changes = new object[]
            {
                new { path = "Aptio:Ip", value = "10.30.40.50" },     // 合法
                new { path = "logging:loglevel", value = "Warning" },   // 大小写变体段节点
            }
        };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("logging:loglevel");

        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Ip").ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task PutConfig_CaseVariant_LowerCaseParentObject_Returns400()
    {
        await using var host = await StartAsync();
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        // Aptio:database（小写父节点）本身就是对象节点 → 不能被标量化
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "Aptio:database", value = "oops" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Host").ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task PutConfig_CaseVariant_Leaf_StillWritable()
    {
        await using var host = await StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "logging:loglevel:default", value = "Warning" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        // 写回既有键名（不产生影子键）
        TestConfigFile.ReadString(host.ConfigPath, "Logging:LogLevel:Default").ShouldBe("Warning");
        LoadJson(host.ConfigPath)!["Logging"]!["LogLevel"]!.AsObject().Count.ShouldBe(2);
    }

    [Fact]
    public async Task PutConfig_CaseVariant_ArrayLeaf_StillWritable()
    {
        await using var host = await StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "aptio:srmnodeids", value = new[] { "09", "13" } });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var arr = LoadJson(host.ConfigPath)!["Aptio"]!["SrmNodeIds"]!.AsArray();
        arr.Count.ShouldBe(2);
        arr[1]!.GetValue<string>().ShouldBe("13");
    }
}
