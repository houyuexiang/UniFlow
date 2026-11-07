using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin.Services;

/// <summary>
/// 数据库连接测试（只测**已保存配置**）测试。
/// </summary>
public class DatabaseConnectionTesterTests
{
    /// <summary>假探针：记录调用参数，按需抛异常。</summary>
    private sealed class FakeProbe : IDatabaseConnectionProbe
    {
        public DatabaseTestPlan? LastPlan { get; private set; }
        public Exception? Throw { get; set; }
        public Task ProbeAsync(DatabaseTestPlan plan, CancellationToken cancellationToken)
        {
            LastPlan = plan;
            if (Throw != null) throw Throw;
            return Task.CompletedTask;
        }
    }

    private static ConfigService NewService(string path)
        => new(NullLogger<ConfigService>.Instance, path);

    [Fact]
    public void BuildPlan_Aptio_Reads_SavedConfig()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);

        var plan = DatabaseConnectionTester.BuildPlan(svc, "aptio");

        plan.IsValid.ShouldBeTrue();
        plan.Target.ShouldBe("aptio");
        plan.Host.ShouldBe("127.0.0.1");
        plan.Port.ShouldBe(3306);
        plan.User.ShouldBe("root");
        plan.Database.ShouldBe("flexlab");
        plan.Password.ShouldBe("root");      // 取自已保存配置
    }

    [Fact]
    public void BuildPlan_Dms_Reads_SavedConfig()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);

        var plan = DatabaseConnectionTester.BuildPlan(svc, "dms");

        plan.IsValid.ShouldBeTrue();
        plan.Target.ShouldBe("dms");
        plan.Host.ShouldBe("127.0.0.1");
        plan.Port.ShouldBe(3306);
        plan.User.ShouldBe("root");
        plan.Database.ShouldBe("dms");
    }

    [Fact]
    public void BuildPlan_UnknownTarget_HasError()
    {
        var path = TestConfigFile.Create();
        var svc = NewService(path);

        var plan = DatabaseConnectionTester.BuildPlan(svc, "oracle");

        plan.IsValid.ShouldBeFalse();
        plan.Error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TestAsync_Success_ReportsElapsedAndNoError()
    {
        var probe = new FakeProbe();
        var tester = new DatabaseConnectionTester(probe);
        var plan = new DatabaseTestPlan { Target = "aptio", Host = "h", Port = 3306, User = "u", Password = "p", Database = "d" };

        var result = await tester.TestAsync(plan);

        result.Success.ShouldBeTrue();
        result.Target.ShouldBe("aptio");
        result.ElapsedMs.ShouldBeGreaterThanOrEqualTo(0);
        result.Message.ShouldContain("succeeded");
        probe.LastPlan.ShouldNotBeNull();
        probe.LastPlan!.Host.ShouldBe("h");
    }

    [Fact]
    public async Task TestAsync_Failure_DoesNotLeakPassword()
    {
        var probe = new FakeProbe { Throw = new Exception("Access denied for user 'root' using password 'SuperSecret123'") };
        var tester = new DatabaseConnectionTester(probe);
        var plan = new DatabaseTestPlan { Target = "aptio", Host = "h", Port = 3306, User = "root", Password = "SuperSecret123", Database = "d" };

        var result = await tester.TestAsync(plan);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("failed");
        result.Message.ShouldNotContain("SuperSecret123");
        result.Message.ShouldContain("***");
    }

    [Fact]
    public async Task TestAsync_InvalidPlan_FailsWithoutConnecting()
    {
        var probe = new FakeProbe();
        var tester = new DatabaseConnectionTester(probe);
        var plan = new DatabaseTestPlan { Target = "aptio", Error = "Saved configuration 'Aptio:Database:Host' is missing" };

        var result = await tester.TestAsync(plan);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Aptio:Database:Host");
        probe.LastPlan.ShouldBeNull();     // 没发起连接
    }

    [Fact]
    public async Task TestAsync_RealMySqlProbe_AgainstClosedPort_Fails_WithoutPassword()
    {
        // 真实探针 + 一个必然连不通的端口（1）：不依赖本机是否有 MySQL
        var tester = new DatabaseConnectionTester(new MySqlDatabaseConnectionProbe());
        var plan = new DatabaseTestPlan
        {
            Target = "aptio",
            Host = "127.0.0.1",
            Port = 1,
            User = "root",
            Password = "SuperSecret123",
            Database = "flexlab"
        };

        var result = await tester.TestAsync(plan);

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotContain("SuperSecret123");
    }

    [Theory]
    [InlineData("boom SuperSecret123 boom", "SuperSecret123")]
    [InlineData("conn string: Server=h;Password=SuperSecret123;Db=x", "SuperSecret123")]
    public void Sanitize_RemovesPassword(string message, string password)
    {
        var sanitized = DatabaseConnectionTester.Sanitize(message, password);
        sanitized.ShouldNotContain(password);
        sanitized.ShouldContain("***");
    }
}
