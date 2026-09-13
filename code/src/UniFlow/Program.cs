using Microsoft.Extensions.Hosting;
using System.Text.Json;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.Common.Services.Logging;
using UniFlow.DisposeSample.Services;
using UniFlow.DisposeSample.Workers;
using UniFlow.SrmExport.Services;
using UniFlow.SrmExport.Workers;
using UniFlow.WorkListCleaner.Services;
using UniFlow.WorkListCleaner.Workers;
using UniFlow.DMSAutoOrder.Services;
using UniFlow.DMSAutoOrder.Workers;
using UniFlow.WebAdmin;
using UniFlow.WebAdmin.Services;

// ============================================================
// 外壳（常驻）：Kestrel/Web + 错误收集 + 健康检查 + 重启管理
// 业务 Worker 运行在可整体重建的内部宿主（InnerHost）中，
// 收到重启请求即按最新配置重建完整实例（Web 不中断）。
// ============================================================

// 首层保护：appsettings.json 不存在或 JSON 损坏（手工编辑出错 / 写入中断）
// → 备份原文件 → 生成一份全新默认配置 → Kestrel 正常启动（进程不崩）
{
    var cfgFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    var needsFresh = true;   // 文件不存在 / 空 / 无法解析
    try
    {
        if (File.Exists(cfgFile))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgFile));
            needsFresh = false;
        }
    }
    catch { }
    var corruptBak = cfgFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
    if (needsFresh && File.Exists(cfgFile))
    {
        try { File.Copy(cfgFile, corruptBak, overwrite: true); } catch { }
    }
    if (needsFresh)
    {
        try
        {
            var fresh = UniFlow.WebAdmin.Services.DefaultConfig.Build();
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(cfgFile, JsonSerializer.Serialize(fresh, opts));
            Console.WriteLine("[UniFlow] appsettings.json was missing or corrupted — a fresh default config has been generated (backup: " +
                Path.GetFileName(corruptBak) + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UniFlow] FATAL: cannot create default appsettings.json: {ex.Message}");
            throw;
        }
    }
}

// 启动时清理：删除上代运行完已释放的 .old 文件
{
    var staleOld = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
        OperatingSystem.IsWindows() ? "UniFlow.exe.old" : "UniFlow.old");
    if (File.Exists(staleOld))
    {
        try { File.Delete(staleOld); } catch { }
    }
}

var builder = WebApplication.CreateBuilder(args);

// 升级包上传（~50MB 自包含单文件）需要放宽请求体上限（默认 30MB 会 413）
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 300L * 1024 * 1024);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "UniFlow";
});

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

var webCfg = builder.Configuration.GetSection("WebAdmin").Get<WebAdminConfig>() ?? new WebAdminConfig();

// ===== 日志：共享文件 Provider（外壳与业务宿主共用同一实例，避免日志文件双开） =====
var fileLoggerConfig = new UniFlowLoggerConfig();
{
    var section = builder.Configuration.GetSection("Logging:UniFlowFile");
    fileLoggerConfig.FileLoggingEnabled = section.GetValue<bool>("FileLoggingEnabled");
    fileLoggerConfig.LogDirectory = section.GetValue<string>("LogDirectory") ?? "logs";
    fileLoggerConfig.MaxFileSizeMb = section.GetValue<int>("MaxFileSizeMb");
    fileLoggerConfig.RetentionDays = section.GetValue<int>("RetentionDays");
    fileLoggerConfig.LogLevel = section.GetValue<string>("LogLevel") ?? "Information";
}
var fileProvider = new UniFlowFileLoggerProvider(fileLoggerConfig);
builder.Logging.AddProvider(fileProvider);
builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss ");
builder.Services.AddSingleton(fileProvider);

// ===== 外壳服务（Web/持久层） =====
builder.Services.AddSingleton(webCfg);
builder.Services.AddSingleton<ErrorReporter>();
builder.Services.AddSingleton<IErrorReporter>(sp => sp.GetRequiredService<ErrorReporter>());
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uniflow_admin.db");
builder.Services.AddSingleton(sp => new HealthStore(dbPath, sp.GetRequiredService<ILogger<HealthStore>>(), webCfg.ErrorRetentionDays, sp.GetRequiredService<ErrorReporter>()));
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<RestartManager>();
builder.Services.AddHostedService<ErrorCollectorWorker>();
builder.Services.AddHostedService<HealthCheckWorker>();

// 启动防护：BindIp/Port 非法时回退默认并告警（杜绝 Kestrel 绑定失败 → 进程崩溃循环）
{
    var bindOk = !string.IsNullOrWhiteSpace(webCfg.BindIp);
    var portOk = webCfg.Port is >= 1 and <= 65535;
    if (!bindOk || !portOk)
    {
        Console.WriteLine($"[UniFlow] invalid WebAdmin BindIp='{webCfg.BindIp}' / Port='{webCfg.Port}' — falling back to 0.0.0.0:5100");
        webCfg.BindIp = "0.0.0.0";
        webCfg.Port = 5100;
    }
}
builder.WebHost.UseUrls($"http://{webCfg.BindIp}:{webCfg.Port}");

var webApp = builder.Build();

if (webCfg.Enabled)
{
    // 静态资源协商缓存（no-cache=每次校验，文件更新浏览器立即拿到新版），
    // 避免旧缓存的 app.js 用旧 path 格式写配置产生影子键
    webApp.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
    });
    webApp.MapGet("/", () => Results.Redirect("/index.html"));

    var health = webApp.Services.GetRequiredService<HealthStore>();
    var config = webApp.Services.GetRequiredService<ConfigService>();
    var restart = webApp.Services.GetRequiredService<RestartManager>();
    ApiEndpoints.Map(webApp, health, config, restart);
}

// ===== 启动业务宿主 =====
var restartManager = webApp.Services.GetRequiredService<RestartManager>();
try
{
    await restartManager.StartAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"[UniFlow] business host start failed: {ex.Message}");
    throw;
}

try
{
    await webApp.RunAsync();
}
finally
{
    await restartManager.StopAsync();
}

// ============================================================
// 业务服务注册（内部宿主每次重建时重新执行 → 启动期配置全部生效）
// ============================================================
public static partial class Program
{
    public static void RegisterBusinessServices(IServiceCollection services, IConfiguration config, IServiceProvider outer)
    {
        var features = config.GetSection("Features").Get<FeatureConfig>() ?? new();
        var aptio = config.GetSection("Aptio").Get<AptioConfig>() ?? new();

        services.Configure<FeatureConfig>(config.GetSection("Features"));
        services.Configure<AptioConfig>(config.GetSection("Aptio"));

        // 共享外壳单例（错误队列/健康存储贯穿内外宿主）
        services.AddSingleton(outer.GetRequiredService<HealthStore>());
        services.AddSingleton(outer.GetRequiredService<IErrorReporter>());

        // Common
        services.AddSingleton<AptioSocketClient>();
        services.AddSingleton<IAptioSocketClient>(sp => sp.GetRequiredService<AptioSocketClient>());
        services.AddHostedService(sp => sp.GetRequiredService<AptioSocketClient>());
        services.AddSingleton<SrmStatusDecoder>();
        services.AddSingleton(aptio);
        services.AddSingleton<AptioTaskRouter>();

        // DisposeSample + SrmExport
        var dbCfg = aptio.Database ?? new();
        var aptioFactory = new MySqlConnectionFactory(dbCfg);

        services.AddSingleton<IDisposeDatabaseService>(sp =>
            new DisposeDatabaseService(sp.GetRequiredService<ILogger<DisposeDatabaseService>>(), aptioFactory));
        services.AddSingleton<AptioCommandService>();

        services.AddSingleton<IExportDatabaseService>(sp =>
            new ExportDatabaseService(sp.GetRequiredService<ILogger<ExportDatabaseService>>(), aptioFactory));
        services.AddSingleton<IExportFileService>(sp =>
        {
            var log = sp.GetRequiredService<ILogger<ExportFileService>>();
            var outputPath = aptio.SrmExport.OutputPath;
            var baseDir = string.IsNullOrEmpty(outputPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DisposeFile")
                : Path.IsPathRooted(outputPath)
                    ? outputPath
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, outputPath);
            return new ExportFileService(log, baseDir);
        });

        // Aptio 组独立功能开关（扫描器无独立开关：任一测试触发功能开启即启用）
        if (features.Aptio.Priority
            || features.Aptio.TestNameDispose
            || features.Aptio.Delivery)
            services.AddHostedService<AptioBatchScannerWorker>();

        if (features.Aptio.DisposeSample)
            services.AddHostedService<DisposeSampleWorker>();

        if (features.Aptio.SrmExport)
            services.AddHostedService<SrmExportWorker>();

        if (features.Aptio.Delivery)
            services.AddHostedService<DeliveryWorker>();

        if (features.Aptio.DeliveryFile)
            services.AddHostedService<DeliveryFileWorker>();

        if (features.Aptio.Priority)
            services.AddHostedService<PriorityWorker>();

        if (features.Aptio.TestNameDispose)
            services.AddHostedService<TestNameDisposeWorker>();

        // WorkListCleaner (Immulite 组)
        if (features.Immulite.WorkListCleaner)
        {
            services.AddSingleton<CentralinkService>();
            var cleanerConfigs = config.GetSection("WorkListCleaners")
                .Get<List<UniFlow.WorkListCleaner.Models.CleanerConfig>>() ?? new();
            foreach (var cfg in cleanerConfigs.Where(c => c.Enabled))
            {
                var config1 = cfg;
                if (!string.IsNullOrEmpty(config1.AgentUrl))
                {
                    services.AddSingleton<IWorkListCleaner>(sp =>
                    {
                        var agent = new UniFlow.WorkListCleaner.Services.AccessAgentClient(
                            sp.GetRequiredService<ILogger<UniFlow.WorkListCleaner.Services.AccessAgentClient>>(),
                            config1.AgentUrl);
                        return new UniFlow.WorkListCleaner.Services.ImmuliteCleaner(
                            sp.GetRequiredService<ILogger<UniFlow.WorkListCleaner.Services.ImmuliteCleaner>>(),
                            sp.GetRequiredService<UniFlow.WorkListCleaner.Services.CentralinkService>(),
                            config1, agent: agent);
                    });
                }
                else
                {
                    services.AddSingleton<UniFlow.WorkListCleaner.Services.AccessDatabaseService>();
                    services.AddSingleton<IWorkListCleaner>(sp =>
                        new UniFlow.WorkListCleaner.Services.ImmuliteCleaner(
                            sp.GetRequiredService<ILogger<UniFlow.WorkListCleaner.Services.ImmuliteCleaner>>(),
                            sp.GetRequiredService<UniFlow.WorkListCleaner.Services.CentralinkService>(),
                            config1,
                            accessDb: sp.GetRequiredService<UniFlow.WorkListCleaner.Services.AccessDatabaseService>()));
                }
                services.AddSingleton(config1);
            }
            services.AddHostedService<WorkListCleanerWorker>();
        }

        // DMS 组
        var dmsOn = features.Dms.PitStopMonitor || features.Dms.StatusCorrection
            || features.Dms.SampleCleanup || features.Dms.EmptyResultCleanup;
        if (dmsOn)
        {
            var dmsCfg = config.GetSection("DMS").Get<UniFlow.DMSAutoOrder.Models.DmsOrderConfig>() ?? new();
            services.AddSingleton(dmsCfg);
            var dmsFactory = new MySqlConnectionFactory(dmsCfg.DbHost, dmsCfg.DbPort, dmsCfg.DbName, dmsCfg.DbUser, dmsCfg.DbPassword);
            services.AddSingleton(sp => new DmsDatabaseService(sp.GetRequiredService<ILogger<DmsDatabaseService>>(), dmsFactory, dmsCfg, sp.GetRequiredService<IErrorReporter>()));
            services.AddSingleton<PitStopMonitorService>();
            services.AddSingleton<SampleCleanupService>();
            services.AddSingleton<StatusCorrectionService>();
            services.AddSingleton<EmptyResultCleanupService>();

            if (features.Dms.PitStopMonitor)
                services.AddHostedService<DmsPitStopWorker>();
            if (features.Dms.StatusCorrection)
                services.AddHostedService<DmsStatusCorrectionWorker>();
            if (features.Dms.SampleCleanup)
                services.AddHostedService<DmsSampleCleanupWorker>();
            if (features.Dms.EmptyResultCleanup)
                services.AddHostedService<DmsEmptyResultCleanupWorker>();
        }
    }
}