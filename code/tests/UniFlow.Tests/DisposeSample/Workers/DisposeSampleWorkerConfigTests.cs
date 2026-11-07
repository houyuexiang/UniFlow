using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.DisposeSample.Workers;
using UniFlow.Tests.Common;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.DisposeSample.Workers;

/// <summary>
/// M-04：配置双源的修复用例。
/// <para>
/// 历史缺陷：<c>DisposeSampleWorker</c> 每 3s 自己 <c>File.ReadAllText("appsettings.json")</c>
/// 并用 <c>System.Text.Json</c> 解析 <c>Features</c>/<c>Aptio</c> 两段，
/// 而同一份配置在 DI 里又通过 <c>IOptions</c>/<c>IOptionsMonitor</c> 绑定了一份
/// → **两个配置源**：worker 直读的值可能与 DI 绑定的值不一致
/// （<c>DisposeDatabaseService</c> 用的是 <c>IOptionsMonitor</c>，worker 用的是直读文件）。
/// 现在：worker 只认 <c>IOptionsMonitor</c>（唯一入口），文件改动 → 变更令牌 → 监视器刷新。
/// </para>
/// </summary>
public class DisposeSampleWorkerConfigTests : IDisposable
{
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IDisposeDatabaseService _db = Substitute.For<IDisposeDatabaseService>();
    private readonly SrmStatusDecoder _decoder = new();
    private readonly AptioCommandService _commands;
    private readonly HealthStore _health;
    private readonly string _healthDbPath;
    private readonly TestOptionsMonitor<AptioConfig> _aptioMonitor;
    private readonly TestOptionsMonitor<FeatureConfig> _featureMonitor;

    public DisposeSampleWorkerConfigTests()
    {
        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-cfg-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance);
        _commands = new AptioCommandService(_socket, NullLogger<AptioCommandService>.Instance);
        _aptioMonitor = new TestOptionsMonitor<AptioConfig>(BuildConfig());
        _featureMonitor = new TestOptionsMonitor<FeatureConfig>(new FeatureConfig());
        _socket.Connected.Returns(true);
    }

    private static AptioConfig BuildConfig(bool allowStandby = false, string commandName = "view_overtimestoragesample") => new()
    {
        Ip = "127.0.0.1",
        Port = 2055,
        SrmNodeIds = new() { "18" },
        DisposeSample = new AptioDisposeSampleConfig
        {
            CommandType = 0,
            CommandName = commandName,
            DiscardRunDate = "1,2,3,4,5,6,7",
            DiscardTimeRange = "00:00-23:59,9999;",
            MaxWaitDiscardCount = 3,
            MaxOnetimeSelectDiscardCount = 10,
            AllowSrmErrorCode = "0000",
            SkipOnUnknownNode = true,
            AllowStandbyDispose = allowStandby
        }
    };

    private DisposeSampleWorker CreateWorker()
        => new(NullLogger<DisposeSampleWorker>.Instance, _socket, _db, _commands, _decoder, _health,
               _aptioMonitor, _featureMonitor);

    private void SetStatusOk() => _socket.SendAsync(
        Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<string?>(@"STATUS\18^SRM^1^ON^0000^G^^\"));

    /// <summary>STB（Stand-by）状态：仅在 AllowStandbyDispose=true 时才可丢弃。</summary>
    private void SetStatusStandby() => _socket.SendAsync(
        Arg.Is<string>(s => s == "STATUS-REQUEST 1"), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<string?>(@"STATUS\18^SRM^1^STB^0000^G^^\"));

    private void SetDelivered() => _socket.SendAsync(
        Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>())
        .Returns(Task.FromResult<string?>(AptioSocketClient.DeliveredMarker));

    private void SetupRecord() => _db.SelectOneSendRecordAsync().Returns(Task.FromResult<DisposeStatus?>(
        new DisposeStatus { Barcode = "S001", Location = "&1-18-000001" }));

    [Fact]
    public async Task Worker_UsesMonitorValues_NotFileRead()
    {
        // 监视器给出 AllowStandbyDispose=false → STB 节点不可丢弃（不来自任何文件）
        _aptioMonitor.CurrentValue = BuildConfig(allowStandby: false);
        var worker = CreateWorker();

        worker.CurrentState = SysStatus.Ready;
        SetStatusStandby();
        SetDelivered();
        SetupRecord();
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));

        await worker.HandleReadyAsync();

        worker.CurrentState.ShouldBe(SysStatus.Ready);   // 未就绪 → 不下发
        await _socket.DidNotReceive().SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MonitorChange_IsPickedUp_OnRefresh()
    {
        // 初始：开关关闭 → STB 不下发
        _aptioMonitor.CurrentValue = BuildConfig(allowStandby: false);
        var worker = CreateWorker();

        worker.CurrentState = SysStatus.Ready;
        SetStatusStandby();
        SetDelivered();
        SetupRecord();
        _db.UpdateStatusAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));

        await worker.HandleReadyAsync();
        worker.CurrentState.ShouldBe(SysStatus.Ready);

        // 配置改动（模拟 appsettings.json 被改 → 变更令牌 → 监视器刷新）
        _aptioMonitor.CurrentValue = BuildConfig(allowStandby: true);
        worker.RefreshConfig();

        worker.CurrentState = SysStatus.Ready;
        await worker.HandleReadyAsync();

        // 开关生效 → 正常下发（证明 worker 认监视器，而不是自己读文件）
        worker.CurrentState.ShouldBe(SysStatus.Ack);
        await _socket.Received(1).SendAsync(Arg.Is<string>(s => s.StartsWith("COMMENT")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MonitorValue_IsUsed_ForDerivedValues_AfterRefresh()
    {
        var worker = CreateWorker();
        // 监视器里的 CommandName 改成别的名字 → 刷新后 worker 用的是新值
        _aptioMonitor.CurrentValue = BuildConfig(commandName: "view_changed_by_monitor");
        worker.RefreshConfig();

        // 通过行为验证：CommandName 传给 DB 层
        _db.CheckSrmSampleCountAsync("18").Returns(Task.FromResult(300));   // 超过阈值 → 走取样路径
        _db.CountPendingSendAsync().Returns(Task.FromResult(0));
        _db.CountPendingAsync().Returns(Task.FromResult(0));
        _db.GetDisposeSamplesAsync(0, "view_changed_by_monitor", 10)
            .Returns(Task.FromResult(new List<SampleRecord>()));

        await worker.HandleNoneAsync(200);

        await _db.Received(1).GetDisposeSamplesAsync(0, "view_changed_by_monitor", 10);
    }

    [Fact]
    public void Worker_Source_NoLonger_ReadsAppsettingsJson()
    {
        // 负向断言（防止回退到"自己读 appsettings.json"的老写法 → 重新引入配置双源）：
        // worker 的配置只能来自 IOptionsMonitor。
        // 注意：断言针对**代码**（先剥掉注释）——注释里可以说明历史实现，但不能真的去读文件。
        var source = UniFlow.Tests.Common.SourceText.CodeOf(
            UniFlow.Tests.Common.SourceText.RepoFile("code", "src", "UniFlow", "DisposeSample", "Workers", "DisposeSampleWorker.cs"));

        source.ShouldNotContain("File.ReadAllText");
        source.ShouldNotContain("appsettings.json");
        source.ShouldNotContain("JsonSerializer");
        source.ShouldNotContain("System.Text.Json");
        // 必须显式依赖 IOptionsMonitor（唯一配置入口）
        source.ShouldContain("IOptionsMonitor<AptioConfig>");
        source.ShouldContain("IOptionsMonitor<FeatureConfig>");
    }

    [Fact]
    public void Di_Registers_Worker_WithMonitor_Dependencies()
    {
        // DI 冒烟：生产同款注册（Program.ConfigureAptioOptions + Configure<FeatureConfig>）
        // 必须能解析出 DisposeSampleWorker（新增的 IOptionsMonitor 依赖可满足）。
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Aptio:Ip"] = "127.0.0.1",
            ["Aptio:SrmNodeIds:0"] = "18",
            ["Aptio:DisposeSample:CommandName"] = "view_overtimestoragesample",
        }).Build();

        var services = new ServiceCollection();
        global::Program.ConfigureAptioOptions(services, config);
        services.Configure<FeatureConfig>(config.GetSection("Features"));
        services.AddSingleton(_socket);
        services.AddSingleton<IDisposeDatabaseService>(_db);
        services.AddSingleton(_commands);
        services.AddSingleton(_decoder);
        services.AddSingleton(_health);
        services.AddSingleton<ILogger<DisposeSampleWorker>>(NullLogger<DisposeSampleWorker>.Instance);
        // 生产用 AddHostedService<DisposeSampleWorker>()；此处注册为普通单例以便直接解析
        services.AddSingleton<DisposeSampleWorker>();

        var provider = services.BuildServiceProvider();
        var worker = provider.GetRequiredService<DisposeSampleWorker>();

        worker.ShouldNotBeNull();
        // 监视器绑定的节点与配置一致（证明走的是 DI 的 AptioConfig 绑定）
        provider.GetRequiredService<IOptionsMonitor<AptioConfig>>().CurrentValue.SrmNodeIds
            .ShouldBe(new[] { "18" });
    }

    /// <summary>从测试输出目录向上找仓库根，再拼相对路径取文件（与 Deploy 用例同法）。</summary>
    private static string ReadRepoFile(params string[] relativeParts)
        => UniFlow.Tests.Common.SourceText.RepoFile(relativeParts);

    public void Dispose()
    {
        _health.Dispose();
        try { File.Delete(_healthDbPath); } catch { /* ignore */ }
    }
}
