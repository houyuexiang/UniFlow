using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin.Api;

/// <summary>
/// 配置读取的**打码**回归测试（P0：?path=&lt;父节点&gt; 明文泄露密码）。
/// </summary>
public class ConfigRedactionApiTests
{
    // 用有辨识度的口令，便于断言"响应里绝不能出现"
    private const string AdminPwd = "T3st-P@ss";
    private const string AptioDbPwd = "REALDBPWD";
    private const string DmsDbPwd = "REALPWD";

    private static Task<TestWebHost> StartAsync() => TestWebHost.StartAsync(
        adminPassword: AdminPwd,
        customize: o =>
        {
            o["WebAdmin"]!["AdminPassword"] = JsonValue.Create(AdminPwd);
            o["Aptio"]!["Database"]!["Password"] = JsonValue.Create(AptioDbPwd);
            o["DMS"]!["DbPassword"] = JsonValue.Create(DmsDbPwd);
        });

    private static async Task<JsonElement> GetValueAsync(TestWebHost host, string path)
    {
        var resp = await host.Client.GetAsync($"/api/config?path={Uri.EscapeDataString(path)}");
        var text = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("value").Clone();
    }

    [Fact]
    public async Task GetConfig_ByPath_WebAdmin_DoesNotLeak_AdminPassword()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=WebAdmin");
        var text = await resp.Content.ReadAsStringAsync();

        text.ShouldNotContain(AdminPwd);
        var value = await GetValueAsync(host, "WebAdmin");
        value.GetProperty("AdminPassword").GetString().ShouldBe("***");
        // 非密码字段不受影响
        value.GetProperty("Port").GetInt32().ShouldBe(5100);
    }

    [Fact]
    public async Task GetConfig_ByPath_AptioDatabase_DoesNotLeak_Password()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=Aptio%3ADatabase");
        var text = await resp.Content.ReadAsStringAsync();

        text.ShouldNotContain(AptioDbPwd);
        var value = await GetValueAsync(host, "Aptio:Database");
        value.GetProperty("Password").GetString().ShouldBe("***");
        value.GetProperty("User").GetString().ShouldBe("root");   // 非密码字段保留
    }

    [Fact]
    public async Task GetConfig_ByPath_DMS_DoesNotLeak_DbPassword()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=DMS");
        var text = await resp.Content.ReadAsStringAsync();

        text.ShouldNotContain(DmsDbPwd);
        var value = await GetValueAsync(host, "DMS");
        value.GetProperty("DbPassword").GetString().ShouldBe("***");
        value.GetProperty("DbName").GetString().ShouldBe("dms");
    }

    [Fact]
    public async Task GetConfig_ByPath_AdminPassword_Itself_IsMask()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=WebAdmin%3AAdminPassword");
        var text = await resp.Content.ReadAsStringAsync();

        text.ShouldNotContain(AdminPwd);
        var value = await GetValueAsync(host, "WebAdmin:AdminPassword");
        value.GetString().ShouldBe("***");
    }

    [Fact]
    public async Task GetConfig_All_DoesNotLeak_AnyPassword()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config");
        var text = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        text.ShouldNotContain(AdminPwd);
        text.ShouldNotContain(AptioDbPwd);
        text.ShouldNotContain(DmsDbPwd);
        text.ShouldContain("\"***\"");
    }

    // ===== 小写 path：大小写不敏感也必须打码（防"换个大小写就绕过打码"）=====

    [Fact]
    public async Task GetConfig_ByPath_Lowercase_WebAdminAdminPassword_StillMasked()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=webadmin%3Aadminpassword");
        var text = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, text);
        text.ShouldNotContain(AdminPwd);
        var value = await GetValueAsync(host, "webadmin:adminpassword");
        value.GetString().ShouldBe("***");
    }

    [Fact]
    public async Task GetConfig_ByPath_Lowercase_AptioDatabasePassword_StillMasked()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=aptio%3Adatabase%3Apassword");
        var text = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, text);
        text.ShouldNotContain(AptioDbPwd);
        var value = await GetValueAsync(host, "aptio:database:password");
        value.GetString().ShouldBe("***");
    }

    [Fact]
    public async Task GetConfig_ByPath_Lowercase_DmsDbPassword_StillMasked()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=dms%3Adbpassword");
        var text = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, text);
        text.ShouldNotContain(DmsDbPwd);
        var value = await GetValueAsync(host, "dms:dbpassword");
        value.GetString().ShouldBe("***");
    }

    [Fact]
    public async Task GetConfig_ByPath_Lowercase_ParentNode_StillMasksChildren()
    {
        // 小写父节点：?path=webadmin → 递归打码仍须遮蔽 AdminPassword
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=webadmin");
        var text = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, text);
        text.ShouldNotContain(AdminPwd);
        var value = await GetValueAsync(host, "webadmin");
        value.GetProperty("AdminPassword").GetString().ShouldBe("***");
    }

    [Fact]
    public async Task GetConfig_ByPath_Scalar_KeepsType()
    {
        await using var host = await StartAsync();

        var value = await GetValueAsync(host, "Aptio:Port");
        value.GetInt32().ShouldBe(2055);
    }

    [Fact]
    public async Task GetConfig_ByPath_UnknownPath_Still404()
    {
        await using var host = await StartAsync();

        var resp = await host.Client.GetAsync("/api/config?path=Aptio%3ANoSuchKey");

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    // ===== 递归打码函数本身（数组/嵌套结构，当前配置里没有的场景）=====

    [Fact]
    public void RedactElement_Masks_SecretKeys_InArrayOfObjects()
    {
        var json = """
        [
          { "Name": "a", "Password": "leak1", "Nested": { "DbPwd": "leak2" } },
          { "Name": "b", "Password": "leak3" }
        ]
        """;
        using var doc = JsonDocument.Parse(json);

        var redacted = ConfigRedactor.RedactElement(doc.RootElement);

        redacted.GetArrayLength().ShouldBe(2);
        redacted[0].GetProperty("Name").GetString().ShouldBe("a");
        redacted[0].GetProperty("Password").GetString().ShouldBe("***");
        redacted[0].GetProperty("Nested").GetProperty("DbPwd").GetString().ShouldBe("***");
        redacted[1].GetProperty("Password").GetString().ShouldBe("***");
        redacted.GetRawText().ShouldNotContain("leak1");
        redacted.GetRawText().ShouldNotContain("leak2");
        redacted.GetRawText().ShouldNotContain("leak3");
    }

    [Fact]
    public void Redact_Masks_SecretKeys_InDictionaryWithLists()
    {
        var input = new Dictionary<string, object?>
        {
            ["Host"] = "127.0.0.1",
            ["Password"] = "leak-dict",
            ["Items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["User"] = "root", ["Pwd"] = "leak-list" },
            },
            ["Sub"] = new Dictionary<string, object?> { ["Password"] = "leak-sub" },
        };

        var redacted = (Dictionary<string, object?>)ConfigRedactor.Redact(input)!;

        redacted["Host"].ShouldBe("127.0.0.1");
        redacted["Password"].ShouldBe("***");
        var items = (List<object?>)redacted["Items"]!;
        var item = (Dictionary<string, object?>)items[0]!;
        item["User"].ShouldBe("root");
        item["Pwd"].ShouldBe("***");
        var sub = (Dictionary<string, object?>)redacted["Sub"]!;
        sub["Password"].ShouldBe("***");
    }

    [Fact]
    public void RedactElement_Keeps_ScalarsAndStructure()
    {
        using var doc = JsonDocument.Parse("""{ "A": 1, "B": true, "C": null, "D": "x" }""");

        var redacted = ConfigRedactor.RedactElement(doc.RootElement);

        redacted.GetProperty("A").GetInt32().ShouldBe(1);
        redacted.GetProperty("B").GetBoolean().ShouldBeTrue();
        redacted.GetProperty("C").ValueKind.ShouldBe(JsonValueKind.Null);
        redacted.GetProperty("D").GetString().ShouldBe("x");
    }

    [Theory]
    [InlineData("Password", true)]
    [InlineData("password", true)]
    [InlineData("DbPassword", true)]
    [InlineData("Pwd", true)]
    [InlineData("dbPwd", true)]
    [InlineData("Host", false)]
    [InlineData("Port", false)]
    public void IsSecretKey_Matches_PasswordAndPwd(string key, bool expected)
    {
        ConfigRedactor.IsSecretKey(key).ShouldBe(expected);
    }

    // ===== string 必须原样返回（string 是 IEnumerable<char>，不能被拆成字符数组）=====

    [Fact]
    public void Redact_StringValue_Returns_StringUnchanged()
    {
        // 注意：只断言 ShouldBe("abc") 不够 —— Shouldly 的 IEnumerable 重载会按元素比较，
        // List{'a','b','c'} 与 "abc" 也会判等；必须同时断言运行时类型。
        ConfigRedactor.Redact("abc").ShouldBeOfType<string>();
        ConfigRedactor.Redact("abc").ShouldBe("abc");
        ConfigRedactor.Redact("").ShouldBeOfType<string>();
        ConfigRedactor.Redact("").ShouldBe("");
        ConfigRedactor.Redact("中文密码").ShouldBeOfType<string>();
        ConfigRedactor.Redact("中文密码").ShouldBe("中文密码");
    }

    [Fact]
    public void RedactDictionary_Keeps_StringValues_Untouched()
    {
        var input = new Dictionary<string, object?>
        {
            ["Host"] = "127.0.0.1",
            ["Note"] = "line1\nline2",
            ["Password"] = "leak",
        };

        var redacted = ConfigRedactor.RedactDictionary(input);

        redacted["Host"].ShouldBe("127.0.0.1");
        redacted["Note"].ShouldBe("line1\nline2");
        redacted["Password"].ShouldBe("***");
        // 字符串不能被拆成字符数组
        redacted["Host"].ShouldBeOfType<string>();
        redacted["Note"].ShouldBeOfType<string>();
    }

    [Fact]
    public void Redact_NestedStringInsideList_Keeps_String()
    {
        var input = new Dictionary<string, object?>
        {
            ["Items"] = new List<object?> { "abc", 1, true },
        };

        var redacted = ConfigRedactor.RedactDictionary(input);
        var items = (List<object?>)redacted["Items"]!;

        items[0].ShouldBe("abc");
        items[0].ShouldBeOfType<string>();
        items[1].ShouldBe(1);
        items[2].ShouldBe(true);    }
}
