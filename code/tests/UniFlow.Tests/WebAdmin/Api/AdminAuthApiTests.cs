using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;

namespace UniFlow.Tests.WebAdmin.Api;

/// <summary>
/// WebAdmin 管理鉴权 / 批量保存 / 配置下载鉴权 / 数据库连接测试 的 API 集成测试。
/// </summary>
public class AdminAuthApiTests
{
    private static JsonNode? LoadJson(string path) => JsonNode.Parse(File.ReadAllText(path));

    // ===== 挑战（nonce）=====

    [Fact]
    public async Task Challenge_Endpoint_IsFree_And_Returns_Nonce()
    {
        await using var host = await TestWebHost.StartAsync();

        var resp = await host.Client.GetAsync("/api/auth/challenge");
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        var nonce = doc.RootElement.GetProperty("nonce").GetString()!;
        nonce.Length.ShouldBe(64);
        doc.RootElement.GetProperty("expiresInSeconds").GetInt32().ShouldBe(60);
    }

    [Fact]
    public async Task ReadOnly_Endpoints_Stay_Free()
    {
        await using var host = await TestWebHost.StartAsync();

        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/api/version")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/api/restart/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/api/update/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/api/errors")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.Client.GetAsync("/api/logs/list")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ===== 破坏性端点：无鉴权 → 401 =====

    [Theory]
    [InlineData("PUT", "/api/config")]
    [InlineData("PUT", "/api/config/batch")]
    [InlineData("GET", "/api/config/download")]
    [InlineData("POST", "/api/update/upload")]
    [InlineData("POST", "/api/update/apply")]
    [InlineData("POST", "/api/restart")]
    [InlineData("POST", "/api/restart/process")]
    [InlineData("DELETE", "/api/errors")]
    [InlineData("POST", "/api/dispose/pending/reset")]
    [InlineData("POST", "/api/dispose/pending/delete")]
    public async Task Destructive_Endpoints_WithoutAuth_Return401(string method, string url)
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "PUT" or "POST")
            msg.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("Admin authentication failed");
    }

    [Fact]
    public async Task Wrong_Hash_Returns401()
    {
        await using var host = await TestWebHost.StartAsync();
        var nonce = await host.GetNonceAsync();

        var msg = new HttpRequestMessage(HttpMethod.Put, "/api/config/batch");
        msg.Headers.Add("X-Admin-Nonce", nonce);
        msg.Headers.Add("X-Admin-Hash", TestWebHost.ComputeHash("wrong-password", nonce));
        msg.Content = new StringContent("{\"changes\":[]}", System.Text.Encoding.UTF8, "application/json");

        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_Headers_Return401()
    {
        await using var host = await TestWebHost.StartAsync();

        var resp = await host.Client.PutAsync("/api/config/batch",
            new StringContent("{\"changes\":[]}", System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ===== 破坏性端点：正确鉴权 → 业务结果 =====

    [Fact]
    public async Task ConfigBatch_WithAuth_Saves_AllFields()
    {
        await using var host = await TestWebHost.StartAsync();

        var body = new
        {
            changes = new object[]
            {
                new { path = "Aptio:Ip", value = "10.20.30.40" },
                new { path = "Aptio:Port", value = 2077 },
                new { path = "Features:Aptio:DisposeSample", value = false },
            }
        };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(3);

        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Ip").ShouldBe("10.20.30.40");
        TestConfigFile.ReadInt(host.ConfigPath, "Aptio:Port").ShouldBe(2077);
    }

    [Fact]
    public async Task ConfigBatch_WithInvalidField_Returns400_AndFileUnchanged()
    {
        await using var host = await TestWebHost.StartAsync();
        // 先读一次（触发配置迁移/规范化写盘），再取快照
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        var body = new
        {
            changes = new object[]
            {
                new { path = "Aptio:Ip", value = "10.20.30.40" },      // 合法
                new { path = "Aptio:Port", value = "not-a-number" },      // 非法：类型不匹配
            }
        };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldContain("Aptio:Port");

        // 一个字段都不能写
        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Ip").ShouldBe("127.0.0.1");
    }

    [Fact]
    public async Task ConfigBatch_WithUnknownPath_Returns400_AndFileUnchanged()
    {
        await using var host = await TestWebHost.StartAsync();
        (await host.Client.GetAsync("/api/config")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var before = LoadJson(host.ConfigPath);

        var body = new { changes = new object[] { new { path = "Aptio:NoSuchKey", value = "x" } } };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonNode.DeepEquals(before, LoadJson(host.ConfigPath)).ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigBatch_PasswordMask_Keeps_ExistingValue()
    {
        await using var host = await TestWebHost.StartAsync();
        var original = TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password");

        var body = new { changes = new object[] { new { path = "Aptio:Database:Password", value = "***" } } };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(0);       // 没有实际写入
        doc.RootElement.GetProperty("skipped").GetInt32().ShouldBe(1);     // 掩码被跳过
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password").ShouldBe(original);
    }

    [Fact]
    public async Task ConfigBatch_EmptyPasswordValue_Keeps_ExistingValue()
    {
        await using var host = await TestWebHost.StartAsync();
        var original = TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password");

        var body = new { changes = new object[] { new { path = "Aptio:Database:Password", value = "" } } };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password").ShouldBe(original);
    }

    [Fact]
    public async Task ConfigBatch_NewPassword_Is_Written()
    {
        await using var host = await TestWebHost.StartAsync();

        var body = new { changes = new object[] { new { path = "Aptio:Database:Password", value = "NewPass123" } } };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password").ShouldBe("NewPass123");
    }

    [Fact]
    public async Task ConfigBatch_AdminPassword_CannotBeChangedViaApi()
    {
        await using var host = await TestWebHost.StartAsync();

        var body = new { changes = new object[] { new { path = "WebAdmin:AdminPassword", value = "hacked" } } };
        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config/batch", body);
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("appsettings.json");
        TestConfigFile.ReadString(host.ConfigPath, "WebAdmin:AdminPassword").ShouldBe("Advia1");
    }

    [Fact]
    public async Task SingleConfigPut_AdminPassword_CannotBeChangedViaApi()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "WebAdmin:AdminPassword", value = "hacked" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        TestConfigFile.ReadString(host.ConfigPath, "WebAdmin:AdminPassword").ShouldBe("Advia1");
    }

    [Fact]
    public async Task SingleConfigPut_PasswordMask_Keeps_ExistingValue()
    {
        await using var host = await TestWebHost.StartAsync();
        var original = TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password");

        var msg = await host.AuthedAsync(HttpMethod.Put, "/api/config",
            new { path = "Aptio:Database:Password", value = "***" });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        TestConfigFile.ReadString(host.ConfigPath, "Aptio:Database:Password").ShouldBe(original);
    }

    // ===== 配置下载（需鉴权 + 原文）=====

    [Fact]
    public async Task ConfigDownload_WithAuth_Returns_OriginalText()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Get, "/api/config/download");
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldContain("\"AdminPassword\"");
        text.ShouldContain("Advia1");          // 原文（含密码）
    }

    [Fact]
    public async Task Config_Get_Redacts_Passwords()
    {
        await using var host = await TestWebHost.StartAsync();

        var resp = await host.Client.GetAsync("/api/config");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await resp.Content.ReadAsStringAsync();

        text.ShouldContain("\"***\"");
        text.ShouldNotContain("Advia1");        // 管理密码被打码
    }

    // ===== 其余破坏性端点：鉴权通过后的业务结果 =====

    [Fact]
    public async Task UpdateUpload_WithAuth_Reaches_BusinessLogic()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Post, "/api/update/upload");
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);   // 空包 → 业务错误（说明鉴权已通过）
        (await resp.Content.ReadAsStringAsync()).ShouldContain("Empty package");
    }

    [Fact]
    public async Task UpdateApply_WithAuth_Reaches_BusinessLogic()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Post, "/api/update/apply");
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("No staged package");
    }

    [Fact]
    public async Task Restart_WithAuth_Accepted()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Post, "/api/restart");
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DeleteErrors_WithAuth_Ok()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Delete, "/api/errors");
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DisposePendingReset_WithAuth_Reaches_BusinessLogic()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Post, "/api/dispose/pending/reset",
            new { barcodes = new[] { "BC001" } });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);   // 业务宿主未就绪（说明鉴权已通过）
    }

    [Fact]
    public async Task DisposePendingDelete_WithAuth_Reaches_BusinessLogic()
    {
        await using var host = await TestWebHost.StartAsync();

        var msg = await host.AuthedAsync(HttpMethod.Post, "/api/dispose/pending/delete",
            new { barcodes = new[] { "BC001" }, confirm = true });
        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // ===== 管理密码未配置 → 403 =====

    [Theory]
    [InlineData("PUT", "/api/config")]
    [InlineData("PUT", "/api/config/batch")]
    [InlineData("GET", "/api/config/download")]
    [InlineData("POST", "/api/update/upload")]
    [InlineData("POST", "/api/restart")]
    [InlineData("DELETE", "/api/errors")]
    [InlineData("POST", "/api/dispose/pending/delete")]
    public async Task AdminPassword_NotConfigured_Returns403(string method, string url)
    {
        await using var host = await TestWebHost.StartAsync(adminPassword: "");

        var msg = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "PUT" or "POST")
            msg.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var resp = await host.Client.SendAsync(msg);

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("Admin password not configured");
    }

    // ===== 数据库连接测试（免鉴权，只测已保存配置）=====

    [Fact]
    public async Task TestDb_WithoutAuth_IsAllowed_And_NoPasswordInResponse()
    {
        await using var host = await TestWebHost.StartAsync();

        var resp = await host.Client.PostAsync("/api/config/test-db?target=aptio", null);

        resp.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldNotContain("root");              // 不回显密码
        text.ShouldContain("target");
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("target").GetString().ShouldBe("aptio");
        doc.RootElement.TryGetProperty("elapsedMs", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task TestDb_InvalidTarget_Returns400()
    {
        await using var host = await TestWebHost.StartAsync();

        var resp = await host.Client.PostAsync("/api/config/test-db?target=oracle", null);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TestDb_Uses_SavedConfig_Not_RequestBody()
    {
        await using var host = await TestWebHost.StartAsync();

        // 请求体里塞一个"新"配置：端点只测已保存配置，不读请求体
        var resp = await host.Client.PostAsync("/api/config/test-db?target=aptio",
            new StringContent("{\"host\":\"10.9.9.9\",\"password\":\"SuperSecret123\"}",
                System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.ShouldNotContain("SuperSecret123");
        text.ShouldNotContain("10.9.9.9");
    }
}
