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
var features = builder.Configuration.GetSection("Features").Get<FeatureConfig>() ?? new FeatureConfig();

builder.Services.Configure<FeatureConfig>(builder.Configuration.GetSection("Features"));
builder.Services.Configure<AptioConfig>(builder.Configuration.GetSection("Aptio"));
builder.Services.Configure<DatabaseConfig>(builder.Configuration.GetSection("Database"));
builder.Services.Configure<UniFlow.DisposeSample.Models.DisposeConfig>(builder.Configuration.GetSection("Dispose"));
builder.Services.Configure<UniFlow.SrmExport.Models.SrmExportConfig>(builder.Configuration.GetSection("Export"));
builder.Services.Configure<UniFlow.WorkListCleaner.Models.CleanerConfig>(builder.Configuration.GetSection("WorkListCleaners:0"));
builder.Services.Configure<UniFlow.DMSAutoOrder.Models.DmsOrderConfig>(builder.Configuration.GetSection("DMS"));

// Common
builder.Services.AddSingleton<IAptioSocketClient, AptioSocketClient>();
builder.Services.AddSingleton<SrmStatusDecoder>();

// DisposeSample
builder.Services.AddSingleton<IDisposeDatabaseService>(sp =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseConfig>>().Value;
    var log = sp.GetRequiredService<ILogger<DisposeDatabaseService>>();
    return new DisposeDatabaseService(log, cfg);
});
builder.Services.AddSingleton<AptioCommandService>();
if (features.DisposeSampleEnabled) builder.Services.AddHostedService<DisposeSampleWorker>();

// SrmExport
builder.Services.AddSingleton<IExportDatabaseService>(sp =>
{
    var cfg = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseConfig>>().Value;
    var log = sp.GetRequiredService<ILogger<ExportDatabaseService>>();
    return new ExportDatabaseService(log, cfg);
});
builder.Services.AddSingleton<IExportFileService, ExportFileService>();
if (features.SrmExportEnabled) builder.Services.AddHostedService<SrmExportWorker>();

// WorkListCleaner
if (features.WorkListCleanerEnabled)
{
    builder.Services.AddSingleton<AccessDatabaseService>();
    builder.Services.AddSingleton<CentralinkService>();
    builder.Services.AddSingleton<UniFlow.WorkListCleaner.Models.CleanerConfig>(sp =>
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UniFlow.WorkListCleaner.Models.CleanerConfig>>().Value);
    builder.Services.AddSingleton<IWorkListCleaner, ImmuliteCleaner>();
    builder.Services.AddHostedService<WorkListCleanerWorker>();
}

// DMSAutoOrder
if (features.DmsAutoOrderEnabled)
{
    var dmsCfg = builder.Configuration.GetSection("DMS").Get<UniFlow.DMSAutoOrder.Models.DmsOrderConfig>() ?? new();
    builder.Services.AddSingleton(dmsCfg);
    builder.Services.AddSingleton<DmsDatabaseService>();
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

// ===== Build =====
builder.WebHost.UseUrls($"http://{webCfg.BindIp}:{webCfg.Port}");

var app = builder.Build();

// ===== API Endpoints =====
if (webCfg.Enabled)
{
    app.UseStaticFiles();
    app.MapGet("/", () => Results.Redirect("/index.html"));

    var health = app.Services.GetRequiredService<HealthStore>();
    var config = app.Services.GetRequiredService<ConfigService>();
    ApiEndpoints.Map(app, health, config);
}

await app.RunAsync();