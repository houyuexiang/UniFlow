using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UniFlow.Common.Services.Logging;
using UniFlow.WebAdmin;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin.Api;

/// <summary>
/// API 集成测试宿主：进程内起真实 Kestrel（127.0.0.1:0 随机端口），
/// 配置/数据库全部落在临时目录，测试之间互不影响。
/// </summary>
internal sealed class TestWebHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _tempDir;

    public HttpClient Client { get; }
    public string ConfigPath { get; }
    public string AdminPassword { get; }

    private TestWebHost(WebApplication app, HttpClient client, string tempDir, string configPath, string adminPassword)
    {
        _app = app;
        Client = client;
        _tempDir = tempDir;
        ConfigPath = configPath;
        AdminPassword = adminPassword;
    }

    /// <summary>启动测试宿主（可选自定义配置）。</summary>
    public static async Task<TestWebHost> StartAsync(
        string adminPassword = "Advia1",
        Action<JsonObject>? customize = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "uniflow-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "appsettings.json");

        var node = JsonNode.Parse(JsonSerializer.Serialize(DefaultConfig.Build()))!.AsObject();
        node["WebAdmin"]!["AdminPassword"] = JsonValue.Create(adminPassword ?? "");
        customize?.Invoke(node);
        File.WriteAllText(configPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Test"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var config = new ConfigService(NullLogger<ConfigService>.Instance, configPath);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(sp => new AdminAuthService());
        builder.Services.AddSingleton<AdminAuthFilter>();
        builder.Services.AddSingleton<IDatabaseConnectionProbe, MySqlDatabaseConnectionProbe>();
        builder.Services.AddSingleton<DatabaseConnectionTester>();

        // 与生产 Program.cs 一致：HealthStore / RestartManager 必须进 DI，
        // 否则最小 API 会把 rm 这类参数当请求体推断 → 端点构建失败（整站 500）
        var health = new HealthStore(Path.Combine(tempDir, "admin.db"), NullLogger<HealthStore>.Instance, 30);
        builder.Services.AddSingleton(health);
        var fileLogger = new UniFlowFileLoggerProvider(new UniFlowLoggerConfig
        {
            FileLoggingEnabled = false,
            LogDirectory = "logs",
            MaxFileSizeMb = 1,
            RetentionDays = 1,
            LogLevel = "Information"
        });
        builder.Services.AddSingleton(sp => new RestartManager(sp, fileLogger, NullLogger<RestartManager>.Instance));

        var app = builder.Build();

        // 测试诊断：把未处理异常写进响应体，断言失败时可直接看到堆栈（生产代码不含此中间件）
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ex.ToString()));
            }
        });

        var restart = app.Services.GetRequiredService<RestartManager>();

        ApiEndpoints.Map(app, health, config, restart);

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };

        return new TestWebHost(app, client, tempDir, configPath, adminPassword ?? "");
    }

    /// <summary>取一次性 nonce（GET /api/auth/challenge，免鉴权）。</summary>
    public async Task<string> GetNonceAsync()
    {
        var resp = await Client.GetAsync("/api/auth/challenge");
        var json = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            throw new Exception($"challenge failed: {(int)resp.StatusCode} body=[{json}]");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("nonce").GetString()!;
    }

    /// <summary>按前端口径算哈希：SHA-256(密码 + nonce)，十六进制小写。</summary>
    public static string ComputeHash(string password, string nonce)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password + nonce))).ToLowerInvariant();

    /// <summary>带正确鉴权头的 HttpRequestMessage。</summary>
    public async Task<HttpRequestMessage> AuthedAsync(HttpMethod method, string url, object? body = null)
    {
        var nonce = await GetNonceAsync();
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Add("X-Admin-Nonce", nonce);
        msg.Headers.Add("X-Admin-Hash", ComputeHash(AdminPassword, nonce));
        if (body != null)
            msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return msg;
    }

    /// <summary>带正确鉴权头的 HttpRequestMessage（密码显式指定）。</summary>
    public async Task<HttpRequestMessage> AuthedAsync(HttpMethod method, string url, string password, object? body = null)
    {
        var nonce = await GetNonceAsync();
        var msg = new HttpRequestMessage(method, url);
        msg.Headers.Add("X-Admin-Nonce", nonce);
        msg.Headers.Add("X-Admin-Hash", ComputeHash(password, nonce));
        if (body != null)
            msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return msg;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await _app.StopAsync(); } catch { }
        try { await _app.DisposeAsync(); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
