using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin.Services;

/// <summary>
/// ConfigService 批量写入（UpdateBatch）+ 管理密码读取 测试。
/// </summary>
public class ConfigServiceBatchTests
{
    private static ConfigService NewService(string path)
        => new(NullLogger<ConfigService>.Instance, path);

    [Fact]
    public void UpdateBatch_Writes_AllFields_InOnePass()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);

        var ok = svc.UpdateBatch(new List<(string, object?)>
        {
            ("Aptio:Ip", "10.0.0.9"),
            ("Aptio:Port", 2066),
            ("Features:Aptio:DisposeSample", false),
        });

        ok.ShouldBeTrue();
        TestConfigFile.ReadString(path, "Aptio:Ip").ShouldBe("10.0.0.9");
        TestConfigFile.ReadInt(path, "Aptio:Port").ShouldBe(2066);
        TestConfigFile.ReadBool(path, "Features:Aptio:DisposeSample").ShouldBe(false);
    }

    [Fact]
    public void UpdateBatch_Keeps_UnrelatedFields()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);
        var before = TestConfigFile.ReadString(path, "Aptio:Database:Database");

        svc.UpdateBatch(new List<(string, object?)> { ("Aptio:Ip", "10.0.0.9") }).ShouldBeTrue();

        TestConfigFile.ReadString(path, "Aptio:Database:Database").ShouldBe(before);
        TestConfigFile.ReadString(path, "WebAdmin:BindIp").ShouldBe("0.0.0.0");
    }

    [Fact]
    public void UpdateBatch_WithNullValue_Fails_AndWritesNothing()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);
        var before = File.ReadAllText(path);

        var ok = svc.UpdateBatch(new List<(string, object?)>
        {
            ("Aptio:Ip", "10.0.0.9"),
            ("Aptio:Port", null),
        });

        ok.ShouldBeFalse();
        File.ReadAllText(path).ShouldBe(before);
    }

    [Fact]
    public void UpdateBatch_WithEmptyList_IsNoOp()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);
        svc.UpdateBatch(new List<(string, object?)>()).ShouldBeTrue();
    }

    [Fact]
    public void UpdateBatch_Creates_NestedPath_IfMissing()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);

        svc.UpdateBatch(new List<(string, object?)> { ("Aptio:BrandNew:Key", "v1") }).ShouldBeTrue();

        TestConfigFile.ReadString(path, "Aptio:BrandNew:Key").ShouldBe("v1");
    }

    [Fact]
    public void ReadAdminPassword_Returns_ConfiguredValue()
    {
        var path = TestConfigFile.Create(adminPassword: "S3cret!");
        var svc = NewService(path);
        svc.ReadAdminPassword().ShouldBe("S3cret!");
    }

    [Fact]
    public void ReadAdminPassword_WhenMissing_ReturnsNull()
    {
        var path = TestConfigFile.Create(customize: o => o["WebAdmin"]!.AsObject().Remove("AdminPassword"));
        var svc = NewService(path);
        svc.ReadAdminPassword().ShouldBeNull();
    }

    [Fact]
    public void ReadAdminPassword_Trims_SurroundingWhitespace()
    {
        var path = TestConfigFile.Create(adminPassword: "  Advia1  ");
        var svc = NewService(path);
        svc.ReadAdminPassword().ShouldBe("Advia1");
    }

    [Fact]
    public void ReadAdminPassword_WhenEmpty_ReturnsEmpty()
    {
        var path = TestConfigFile.Create(adminPassword: "");
        var svc = NewService(path);
        svc.ReadAdminPassword().ShouldBe("");
    }

    [Fact]
    public void FilePath_Exposes_ConfiguredPath()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);
        svc.FilePath.ShouldBe(path);
    }
}
