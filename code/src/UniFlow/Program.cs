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
builder.Services.Configure<AptioAutoProcessConfig>(builder.Configuration.GetSection("AptioAutoProcess"));

// Common
builder.Services.AddSingleton<IAptioSocketClient, AptioSocketClient>();
builder.Services.AddSingleton<SrmStatusDecoder>();
builder.Services.AddSingleton(aptio);

// DisposeSample + SrmExport
var dbCfg = aptio.Database ?? new();
var autoCfg = builder.Configuration.GetSection("AptioAutoProcess").Get<AptioAutoProcessConfig>() ?? new();
var aptioFactory = new MySqlConnectionFactory(dbCfg);

builder.Services.AddSingleton<IDisposeDatabaseService>(sp =>
    new DisposeDatabaseService(sp.GetRequiredService<ILogger<DisposeDatabaseService>>(), aptioFactory));
builder.Services.AddSingleton<AptioCommandService>();

builder.Services.AddSingleton<IExportDatabaseService>(sp =>
    new ExportDatabaseService(sp.GetRequiredService<ILogger<ExportDatabaseService>>(), aptioFactory));
builder.Services.AddSingleton<IExportFileService>(sp =>
{
    var log = sp.GetRequiredService<ILogger<ExportFileService>>();
    var outputPath = autoCfg.Export?.OutputPath;
    // outputPath 支持相对路径（相对安装目录，如 "DisposeFile"）和绝对路径（如 /data/exports 或 D:\exports）
    var baseDir = string.IsNullOrEmpty(outputPath)
        ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DisposeFile")
        : Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, outputPath);
    return new ExportFileService(log, baseDir);
});

if (features.AptioAutoProcess)
{
    builder.Services.AddHostedService<DisposeSampleWorker>();
    builder.Services.AddHostedService<SrmExportWorker>();
}

// WorkListCleaner
if (features.ImmuliteWorkOrderClean)
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

// DMSAutoOrder
if (features.DmsAutoOrder)
{
    var dmsCfg = builder.Configuration.GetSection("DMS").Get<UniFlow.DMSAutoOrder.Models.DmsOrderConfig>() ?? new();
    builder.Services.AddSingleton(dmsCfg);
    var dmsFactory = new MySqlConnectionFactory(dmsCfg.DbHost, dmsCfg.DbPort, dmsCfg.DbName, dmsCfg.DbUser, dmsCfg.DbPassword);
    builder.Services.AddSingleton(sp => new DmsDatabaseService(sp.GetRequiredService<ILogger<DmsDatabaseService>>(), dmsFactory, dmsCfg));
    builder.Services.AddSingleton<PitStopMonitorService>();
    builder.Services.AddSingleton<SampleCleanupService>();
    builder.Services.AddSingleton<StatusCorrectionService>();
    builder.Services.AddHostedService<DmsAutoOrderWorker>();
}

// ===== WebAdmin =====
var webCfg = builder.Configuration.GetSection("WebAdmin").Get<WebAdminConfig>() ?? new WebAdminConfig();
builder.Services.AddSingleton(webCfg);

if (webCfg.Enabled)
{
    var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uniflow_admin.db");
    builder.Services.AddSingleton(sp => new HealthStore(dbPath, sp.GetRequiredService<ILogger<HealthStore>>(), webCfg.ErrorRetentionDays));
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