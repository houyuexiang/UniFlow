using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UniFlow.Common.Services.Logging;

namespace UniFlow.WebAdmin;

// 共享文件日志 Provider 的防释放包装：
// 内部宿主销毁时 ILoggerFactory 会 Dispose 所有已注册 Provider，
// 而该 Provider 实例归外壳所有并被下一个内部宿主继续使用——
// 若被内壳 Dispose，所有 StreamWriter 会被关闭且不再重开（日志静默失效）。
file class NonDisposingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;
    public NonDisposingLoggerProvider(ILoggerProvider inner) => _inner = inner;
    public ILogger CreateLogger(string categoryName) => _inner.CreateLogger(categoryName);
    public void Dispose() { /* 共享实例的生命周期归外壳管理 */ }
}

// 业务宿主管理器：Web 外壳常驻，功能 Worker 运行在可整体重建的内部宿主中。
// 重启请求 → 停止并释放旧业务宿主 → 按最新配置构建全新实例并启动
//（= 完整重启语义：所有 DI 单例（数据库工厂/socket 等）随新配置重建，Web 不中断）
public class RestartManager
{
    private readonly IServiceProvider _outer;
    private readonly ILogger<RestartManager> _logger;
    private readonly UniFlowFileLoggerProvider _fileProvider;
    private IHost? _inner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // 业务宿主是否可用（重建期间为 false，API 可据此返回"重启中"）
    public bool IsReady => Volatile.Read(ref _ready) == 1;
    private int _ready;

    // 是否正在重启（gate 被占用）
    public bool IsRestarting => _gate.CurrentCount == 0;

    // 最近一次重启失败信息（null=无失败）
    public string? LastFailure { get; private set; }

    public RestartManager(IServiceProvider outer, UniFlowFileLoggerProvider fileProvider, ILogger<RestartManager> logger)
    {
        _outer = outer;
        _fileProvider = fileProvider;
        _logger = logger;
    }

    public async Task StartAsync()
    {
        // 进程冷启动同理：清掉上一代进程遗留的健康记录（如生产/测试配置切换、崩溃前 degraded），
        // 避免重启后 5 分钟内仪表盘显示上代进程的死状态
        await MarkAllStoppedAsync();
        // 保护：业务宿主构建/启动失败（如配置文件损坏导致 binder 抛异常）时，
        // 保持外壳运行（Web/API 可用，可修复配置后重试），绝不让进程 ABRT 崩溃循环
        try
        {
            var fresh = BuildInnerHost();
            await fresh.StartAsync();
            _inner = fresh;
            Interlocked.Exchange(ref _ready, 1);
            _logger.LogInformation("Business host started");
        }
        catch (Exception ex)
        {
            LastFailure = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.Message}";
            _logger.LogError(ex, "Business host start FAILED — shell remains running; fix config then POST /api/restart");
        }
    }

    public async Task RestartAsync(string reason)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero))
        {
            _logger.LogWarning("Restart already in progress, request ignored: {Reason}", reason);
            return;
        }
        try
        {
            _logger.LogWarning("Restarting business host: {Reason}", reason);
            Interlocked.Exchange(ref _ready, 0);

            var old = Interlocked.Exchange(ref _inner, null);
            if (old != null)
            {
                try { await old.StopAsync(TimeSpan.FromSeconds(30)); }
                catch (Exception ex) { _logger.LogWarning("Stop old business host: {Msg}", ex.Message); }
                old.Dispose();
            }

            await Task.Delay(500);

            // 旧宿主已停：把全部功能模块标记为 stopped（避免停用模块显示陈旧 healthy）；
            // 新宿主启动后，实际运行的模块会重新上报 healthy 覆盖
            await MarkAllStoppedAsync();

            IHost fresh;
            try { fresh = BuildInnerHost(); }
            catch (Exception ex)
            {
                // 新宿主构建失败（配置解析异常等）：_inner 保持 null、ready 保持 0，
                // gate 在 finally 释放，可重试
                LastFailure = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.Message}";
                _logger.LogError(ex, "Business host build FAILED: {Reason}", reason);
                return;
            }
            try
            {
                await fresh.StartAsync();
            }
            catch (Exception ex)
            {
                // 新宿主启动失败：释放半初始化实例，标记失败并允许后续重试
                fresh.Dispose();
                LastFailure = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex.Message}";
                _logger.LogError(ex, "Business host restart FAILED (config invalid?): {Reason}", reason);
                return;
            }

            _inner = fresh;
            Interlocked.Exchange(ref _ready, 1);
            LastFailure = null;
            _logger.LogInformation("Business host restarted ({Reason})", reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _ready, 0);
        var old = Interlocked.Exchange(ref _inner, null);
        if (old == null) return;
        try { await old.StopAsync(TimeSpan.FromSeconds(15)); }
        catch { }
        old.Dispose();
    }

    // 已知功能健康模块名（与各 Worker 的 RecordHealthAsync 一致）
    private static readonly string[] KnownModules =
    {
        "DisposeSample", "SrmExport", "Delivery", "DeliveryFile", "Priority", "TestNameDispose",
        "AptioBatchScanner", "WorkListCleaner",
        "DmsPitStop", "DmsStatusCorr", "DmsCleanup", "DmsEmptyResultCleanup"
    };

    private async Task MarkAllStoppedAsync()
    {
        try
        {
            var health = _outer.GetService<WebAdmin.Services.HealthStore>();
            if (health == null) return;
            foreach (var m in KnownModules)
                await health.RecordHealthAsync(m, "stopped");
        }
        catch { }
    }

    private IHost BuildInnerHost()
    {
        var builder = new HostBuilder();
        builder.ConfigureAppConfiguration(b =>
        {
            b.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
            b.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        });
        builder.ConfigureLogging((ctx, lb) =>
        {
            lb.AddConfiguration(ctx.Configuration.GetSection("Logging"));
            lb.AddProvider(new NonDisposingLoggerProvider(_fileProvider));
            lb.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
        });
        builder.ConfigureServices((ctx, services) =>
            Program.RegisterBusinessServices(services, ctx.Configuration, _outer));
        return builder.Build();
    }
}