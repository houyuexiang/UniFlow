using Microsoft.Extensions.Hosting;
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

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "UniFlow";
});

builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

builder.Logging.AddUniFlowFileLogger(cfg =>
{
    var section = builder.Configuration.GetSection("Logging:UniFlowFile");
    cfg.FileLoggingEnabled = section.GetValue<bool>("FileLoggingEnabled");
    cfg.LogDirectory = section.GetValue<string>("LogDirectory") ?? "logs";
    cfg.MaxFileSizeMb = section.GetValue<int>("MaxFileSizeMb");
    cfg.RetentionDays = section.GetValue<int>("RetentionDays");
    cfg.LogLevel = section.GetValue<string>("LogLevel") ?? "Information";
});

// ===== Register Services =====
var features = builder.Configuration.GetSection("Features").Get<FeatureConfig>() ?? new();
var aptio = builder.Configuration.GetSection("Aptio").Get<AptioConfig>() ?? new();

builder.Services.Configure<FeatureConfig>(builder.Configuration.GetSection("Features"));
builder.Services.Configure<AptioConfig>(builder.Configuration.GetSection("Aptio"));

// Common
builder.Services.AddSingleton<AptioSocketClient>();
builder.Services.AddSingleton<IAptioSocketClient>(sp => sp.GetRequiredService<AptioSocketClient>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AptioSocketClient>());
builder.Services.AddSingleton<SrmStatusDecoder>();
builder.Services.AddSingleton(aptio);
builder.Services.AddSingleton<AptioTaskRouter>();

// DisposeSample + SrmExport
var dbCfg = aptio.Database ?? new();
var aptioFactory = new MySqlConnectionFactory(dbCfg);

builder.Services.AddSingleton<IDisposeDatabaseService>(sp =>
    new DisposeDatabaseService(sp.GetRequiredService<ILogger<DisposeDatabaseService>>(), aptioFactory));
builder.Services.AddSingleton<AptioCommandService>();

builder.Services.AddSingleton<IExportDatabaseService>(sp =>
    new ExportDatabaseService(sp.GetRequiredService<ILogger<ExportDatabaseService>>(), aptioFactory));
builder.Services.AddSingleton<IExportFileService>(sp =>
{
    var log = sp.GetRequiredService<ILogger<ExportFileService>>();
    var outputPath = aptio.SrmExport.OutputPath;
    // outputPath 支持相对路径（相对安装目录，如 "DisposeFile"）和绝对路径（如 /data/exports 或 D:\exports）
    var baseDir = string.IsNullOrEmpty(outputPath)
        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DisposeFile")
        : Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, outputPath);
    return new ExportFileService(log, baseDir);
});

// Aptio 组独立功能开关
// 扫描器无独立开关：任一测试触发功能开启即启用（防止误关）
if (features.Aptio.Priority
    || features.Aptio.TestNameDispose
    || features.Aptio.Delivery)
    builder.Services.AddHostedService<AptioBatchScannerWorker>();

if (features.Aptio.DisposeSample)
    builder.Services.AddHostedService<DisposeSampleWorker>();

if (features.Aptio.SrmExport)
    builder.Services.AddHostedService<SrmExportWorker>();

if (features.Aptio.Delivery)
    builder.Services.AddHostedService<DeliveryWorker>();

if (features.Aptio.DeliveryFile)
    builder.Services.AddHostedService<DeliveryFileWorker>();

if (features.Aptio.Priority)
    builder.Services.AddHostedService<PriorityWorker>();

if (features.Aptio.TestNameDispose)
    builder.Services.AddHostedService<TestNameDisposeWorker>();

// WorkListCleaner (Immulite 组)
if (features.Immulite.WorkListCleaner)
{
    builder.Services.AddSingleton<CentralinkService>();
    var cleanerConfigs = builder.Configuration.GetSection("WorkListCleaners")
        .Get<List<UniFlow.WorkListCleaner.Models.CleanerConfig>>() ?? new();
    foreach (var cfg in cleanerConfigs.Where(c => c.Enabled))
    {
        var config = cfg;
        if (!string.IsNullOrEmpty(config.AgentUrl))
        {
            builder.Services.AddSingleton<IWorkListCleaner>(sp =>
            {
                var agent = new UniFlow.WorkListCleaner.Services.AccessAgentClient(
                    sp.GetRequiredService<ILogger<UniFlow.WorkListCleaner.Services.AccessAgentClient>>(),
                    config.AgentUrl);
                return new UniFlow.WorkListCleaner.Services.ImmuliteCleaner(
                    sp.GetRequiredService<ILogger<UniFlow.WorkListCleaner.Services.ImmuliteCleaner>>(),
                    sp.GetRequiredService<UniFlow.WorkListCleaner.Services.CentralinkService>(),
                    config, agent: agent);
            });
        }
        else
        {
            builder.Services.AddSingleton<UniFlow.WorkListCleaner.Services.AccessDatabaseService>();
            builder.Services.AddSingleton<IWorkListCleaner>(sp =>
                new UniFlow.WorkListCleaner.Services.ImmuliteCleaner(
                    sp.GetRequiredService<ILogger<UniFlow.WorkListCleaner.Services.ImmuliteCleaner>>(),
                    sp.GetRequiredService<UniFlow.WorkListCleaner.Services.CentralinkService>(),
                    config,
                    accessDb: sp.GetRequiredService<UniFlow.WorkListCleaner.Services.AccessDatabaseService>()));
        }
        builder.Services.AddSingleton(config);
    }
    builder.Services.AddHostedService<WorkListCleanerWorker>();
}

// DMSAutoOrder (Dms 组独立开关)
var dmsOn = features.Dms.PitStopMonitor || features.Dms.StatusCorrection
    || features.Dms.SampleCleanup || features.Dms.EmptyResultCleanup;
if (dmsOn)
{
    var dmsCfg = builder.Configuration.GetSection("DMS").Get<UniFlow.DMSAutoOrder.Models.DmsOrderConfig>() ?? new();
    builder.Services.AddSingleton(dmsCfg);
    var dmsFactory = new MySqlConnectionFactory(dmsCfg.DbHost, dmsCfg.DbPort, dmsCfg.DbName, dmsCfg.DbUser, dmsCfg.DbPassword);
    builder.Services.AddSingleton(sp => new DmsDatabaseService(sp.GetRequiredService<ILogger<DmsDatabaseService>>(), dmsFactory, dmsCfg, sp.GetRequiredService<IErrorReporter>()));
    builder.Services.AddSingleton<PitStopMonitorService>();
    builder.Services.AddSingleton<SampleCleanupService>();
    builder.Services.AddSingleton<StatusCorrectionService>();
    builder.Services.AddSingleton<EmptyResultCleanupService>();

    if (features.Dms.PitStopMonitor)
        builder.Services.AddHostedService<DmsPitStopWorker>();
    if (features.Dms.StatusCorrection)
        builder.Services.AddHostedService<DmsStatusCorrectionWorker>();
    if (features.Dms.SampleCleanup)
        builder.Services.AddHostedService<DmsSampleCleanupWorker>();
    if (features.Dms.EmptyResultCleanup)
        builder.Services.AddHostedService<DmsEmptyResultCleanupWorker>();
}

// ===== WebAdmin =====
var webCfg = builder.Configuration.GetSection("WebAdmin").Get<WebAdminConfig>() ?? new WebAdminConfig();
builder.Services.AddSingleton(webCfg);

// 错误/健康存储 + 专属错误收集 Worker（独立于 Web 界面开关）
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uniflow_admin.db");
builder.Services.AddSingleton<ErrorReporter>();
builder.Services.AddSingleton<IErrorReporter>(sp => sp.GetRequiredService<ErrorReporter>());
builder.Services.AddSingleton(sp => new HealthStore(dbPath, sp.GetRequiredService<ILogger<HealthStore>>(), webCfg.ErrorRetentionDays, sp.GetRequiredService<ErrorReporter>()));
builder.Services.AddHostedService<ErrorCollectorWorker>();

if (webCfg.Enabled)
{
    builder.Services.AddSingleton<ConfigService>();
    builder.Services.AddHostedService<HealthCheckWorker>();
}

builder.WebHost.UseUrls($"http://{webCfg.BindIp}:{webCfg.Port}");

var app = builder.Build();

if (webCfg.Enabled)
{
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Redirect("/index.html"));

    var health = app.Services.GetRequiredService<HealthStore>();
    var config = app.Services.GetRequiredService<ConfigService>();
    ApiEndpoints.Map(app, health, config);
}

await app.RunAsync();