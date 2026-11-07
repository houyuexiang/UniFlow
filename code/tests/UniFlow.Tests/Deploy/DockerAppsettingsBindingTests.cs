using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Shouldly;
using UniFlow.Common.Models;

namespace UniFlow.Tests.Deploy;

/// <summary>
/// deploy/appsettings.docker.json 必须与**当前配置模型**一致（能被正确绑定），
/// 且不得残留旧平铺结构（Database / Dispose / Export / Features.AptioAutoProcess …）。
/// </summary>
public class DockerAppsettingsBindingTests
{
    /// <summary>从测试输出目录向上找仓库根下的目标文件。</summary>
    private static string RepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo file not found: " + Path.Combine(relativeParts));
    }

    private static JsonDocument LoadDockerJson() => JsonDocument.Parse(File.ReadAllText(RepoFile("deploy", "appsettings.docker.json")));

    [Fact]
    public void DockerAppsettings_Binds_To_CurrentModel()
    {
        var path = RepoFile("deploy", "appsettings.docker.json");
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();

        // 与生产内层宿主完全相同的绑定入口
        var aptio = global::Program.BindAptioConfig(config);

        // ① Aptio:Database（旧结构是顶层 "Database"，会被绑成 null）
        aptio.Database.ShouldNotBeNull();
        aptio.Database!.Host.ShouldBe("192.168.1.200");
        aptio.Database.Port.ShouldBe(3306);
        aptio.Database.User.ShouldBe("root");
        aptio.Database.Password.ShouldBe("root");
        aptio.Database.Database.ShouldBe("flexlab");

        // ② Aptio:DisposeSample（当前结构，不是旧的顶层 "Dispose"）
        aptio.DisposeSample.ShouldNotBeNull();
        aptio.DisposeSample!.MaxOnetimeSelectDiscardCount.ShouldBe(10);
        aptio.DisposeSample.CommandName.ShouldBe("view_overtimestoragesample");
        aptio.DisposeSample.ResendIntervalMinutes.ShouldBe(5);
        aptio.DisposeSample.MaxResendCount.ShouldBe(24);

        // ③ 节点列表：与 JSON 完全一致（无幽灵节点）
        aptio.SrmNodeIds.ShouldBe(new[] { "09", "16" });

        // ④ Features 分组开关（不再是 Features.AptioAutoProcess）
        var features = config.GetSection("Features").Get<FeatureConfig>()!;
        features.Aptio.DisposeSample.ShouldBeTrue();
        features.Aptio.SrmExport.ShouldBeTrue();
        features.Aptio.Delivery.ShouldBeFalse();
        features.Aptio.Priority.ShouldBeFalse();
        features.Immulite.WorkListCleaner.ShouldBeFalse();
        features.Dms.StatusCorrection.ShouldBeFalse();

        // ⑤ WebAdmin（含管理密码）
        var web = config.GetSection("WebAdmin").Get<WebAdminConfig>()!;
        web.Port.ShouldBe(5100);
        web.AdminPassword.ShouldBe("Advia1");
    }

    [Fact]
    public void DockerAppsettings_Has_No_LegacyFlatKeys()
    {
        using var doc = LoadDockerJson();
        var root = doc.RootElement;

        // 旧平铺根：Database / Dispose / Export / AptioAutoProcess
        root.TryGetProperty("Database", out _).ShouldBeFalse("旧顶层 Database 段应已删除");
        root.TryGetProperty("Dispose", out _).ShouldBeFalse("旧顶层 Dispose 段应已删除");
        root.TryGetProperty("Export", out _).ShouldBeFalse("旧顶层 Export 段应已删除");
        root.TryGetProperty("AptioAutoProcess", out _).ShouldBeFalse("旧 AptioAutoProcess 段应已删除");

        // Features 旧布尔开关
        var features = root.GetProperty("Features");
        features.TryGetProperty("AptioAutoProcess", out _).ShouldBeFalse();
        features.TryGetProperty("ImmuliteWorkOrderClean", out _).ShouldBeFalse();
        features.TryGetProperty("DmsAutoOrder", out _).ShouldBeFalse();

        // 保留项
        root.TryGetProperty("Logging", out _).ShouldBeTrue();
        root.TryGetProperty("WorkListCleaners", out _).ShouldBeTrue();
        root.TryGetProperty("DMS", out _).ShouldBeTrue();
        root.TryGetProperty("WebAdmin", out _).ShouldBeTrue();
    }

    [Fact]
    public void DockerCompose_EnvVars_Use_CurrentConfigRoots()
    {
        var text = File.ReadAllText(RepoFile("deploy", "docker-compose.yml"));

        // 当前根（无 UniFlow 前缀：应用未调用 AddEnvironmentVariables("UniFlow")）
        text.ShouldContain("Aptio__Database__Host");
        text.ShouldContain("Aptio__Database__Port");
        text.ShouldContain("Aptio__Database__User");
        text.ShouldContain("Aptio__Database__Password");
        text.ShouldContain("Aptio__Database__Database");
        text.ShouldContain("Aptio__Ip");
        text.ShouldContain("Aptio__Port");
        text.ShouldContain("Features__Aptio__DisposeSample");

        // 旧死变量必须清掉
        text.ShouldNotContain("UniFlow__Database__");
        text.ShouldNotContain("UniFlow__Aptio__");
        text.ShouldNotContain("UniFlow__Features__");
    }

    [Fact]
    public void EnvVar_Naming_Maps_To_CurrentConfigKey()
    {
        // 锁死约定：环境变量名（__ 分隔、无前缀）→ 配置键（: 分隔）
        var key = "Aptio__Database__Host".Replace("__", ":");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "10.1.2.3" })
            .Build();

        config["Aptio:Database:Host"].ShouldBe("10.1.2.3");
        config["UniFlow:Aptio:Database:Host"].ShouldBeNull("带 UniFlow 前缀是死变量（应用未剥离该前缀）");
    }
}
