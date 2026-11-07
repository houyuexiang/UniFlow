using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Shouldly;
using UniFlow.WebAdmin;

namespace UniFlow.Tests.WebAdmin.Api;

/// <summary>
/// dispose 管理端点的 DB 不可达处理用例（P3）。
/// <para>
/// 历史缺陷：DB 异常一律回 500（页面能处理，但 console 噪音大、语义不准）。
/// 现在：DB 不可达（连接失败/超时）→ <b>503</b> {success:false,error:"Database unavailable"}；
/// 其它异常 → 仍是 500（带 ex.Message，不泄漏堆栈）。
/// </para>
/// </summary>
public class DisposeApiDbUnavailableTests
{
    /// <summary>执行 IResult，返回 (状态码, 响应体)。</summary>
    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        // Results.Json / Results.Ok 需要 IOptions<JsonOptions> → 给一个最小 ServiceProvider
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        ctx.RequestServices = services.BuildServiceProvider();
        var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        await result.ExecuteAsync(ctx);
        buffer.Position = 0;
        var body = await new StreamReader(buffer).ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    [Fact]
    public async Task DbUnavailable_Timeout_Returns503_WithoutInternalDetail()
    {
        var result = await ApiEndpoints.RunDisposeDbCallAsync(
            () => throw new TimeoutException("connect timeout after 30s"));

        var (status, body) = await ExecuteAsync(result);

        status.ShouldBe(503);
        body.ShouldContain("Database unavailable");
        body.ShouldNotContain("connect timeout after 30s");   // 不回显内部异常细节
    }

    [Fact]
    public async Task DbUnavailable_MySqlException_Returns503()
    {
        var ex = await CaptureAsync(async () =>
        {
            using var conn = new MySqlConnection(
                "Server=127.0.0.1;Port=1;Database=x;User=u;Password=p;ConnectionTimeout=2;");
            await conn.OpenAsync();
        });

        ex.ShouldBeOfType<MySqlException>();   // 真实连接失败 → MySqlException
        ApiEndpoints.IsDatabaseUnavailable(ex).ShouldBeTrue();

        var result = await ApiEndpoints.RunDisposeDbCallAsync(() => throw ex);
        var (status, body) = await ExecuteAsync(result);

        status.ShouldBe(503);
        body.ShouldContain("Database unavailable");
    }

    [Fact]
    public async Task OtherException_Returns500_WithMessage()
    {
        var result = await ApiEndpoints.RunDisposeDbCallAsync(
            () => throw new InvalidOperationException("mapping blew up"));

        var (status, body) = await ExecuteAsync(result);

        status.ShouldBe(500);
        body.ShouldContain("mapping blew up");
    }

    [Fact]
    public async Task Success_PassesThrough()
    {
        var result = await ApiEndpoints.RunDisposeDbCallAsync(
            () => Task.FromResult(Results.Ok(new { count = 3 })));

        var (status, body) = await ExecuteAsync(result);

        status.ShouldBe(200);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void IsDatabaseUnavailable_ClassifiesExceptions()
    {
        ApiEndpoints.IsDatabaseUnavailable(new TimeoutException("t")).ShouldBeTrue();
        ApiEndpoints.IsDatabaseUnavailable(new IOException("io")).ShouldBeTrue();
        ApiEndpoints.IsDatabaseUnavailable(new InvalidOperationException("x")).ShouldBeFalse();
        ApiEndpoints.IsDatabaseUnavailable(new Exception("plain")).ShouldBeFalse();
        ApiEndpoints.IsDatabaseUnavailable(null).ShouldBeFalse();

        // 被包装在 InnerException 里的连接类异常也算 DB 不可达
        ApiEndpoints.IsDatabaseUnavailable(new Exception("outer", new TimeoutException("inner"))).ShouldBeTrue();
        ApiEndpoints.IsDatabaseUnavailable(new Exception("outer", new InvalidOperationException("inner"))).ShouldBeFalse();
    }

    private static async Task<Exception> CaptureAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("expected an exception but none was thrown");
    }
}
