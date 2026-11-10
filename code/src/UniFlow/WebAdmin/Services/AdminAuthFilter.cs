using Microsoft.AspNetCore.Http;

namespace UniFlow.WebAdmin.Services;

/// <summary>
/// 破坏性端点的管理鉴权过滤器（一次性 nonce + 哈希；另支持「验证记忆」短令牌）。
/// <para>
/// 口径：
/// - 管理密码（WebAdmin:AdminPassword）为空/未配置 → <b>403</b> {success:false,error:"Admin password not configured"}；
/// - 头缺失 / nonce 过期 / 哈希不匹配 / 令牌无效或过期 → <b>401</b> {success:false,error:"Admin authentication failed"}；
/// - 通过 → 继续执行原端点。
/// </para>
/// <para>
/// 「验证记忆」（WebAdmin:AuthMemoryMinutes，默认 5 分钟，0=关闭）：
/// 密码校验通过时下发短令牌（响应头 X-Admin-Token / X-Admin-Token-Expires-In），
/// 之后的请求只带 X-Admin-Token 即可放行（不再重复校验密码）。
/// 令牌只存服务端内存，进程重启 / 密码变更 / 过期 → 失效。
/// </para>
/// <para>
/// 密码与哈希**不写入日志**。
/// </summary>
public sealed class AdminAuthFilter : IEndpointFilter
{
    private readonly AdminAuthService _auth;
    private readonly ConfigService _config;

    public AdminAuthFilter(AdminAuthService auth, ConfigService config)
    {
        _auth = auth;
        _config = config;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // 读「已保存配置」里的管理密码（appsettings.json），因此后台改完立即生效，无需重启
        var adminPassword = _config.ReadAdminPassword();
        if (string.IsNullOrWhiteSpace(adminPassword))
            return Results.Json(
                new { success = false, error = "Admin password not configured" },
                statusCode: StatusCodes.Status403Forbidden);

        var request = context.HttpContext.Request;
        var token = request.Headers["X-Admin-Token"].ToString();
        var nonce = request.Headers["X-Admin-Nonce"].ToString();
        var hash = request.Headers["X-Admin-Hash"].ToString();

        // ① 验证记忆令牌优先：有效 → 直接放行（不再重复校验密码）
        if (!string.IsNullOrWhiteSpace(token) && _auth.TryValidateToken(token, adminPassword, out _))
            return await next(context);

        // ② 令牌无效/过期（或没带令牌）：带 nonce/hash 就继续走一次性 nonce 校验。
        //    同时带 nonce/hash 时不因「旧令牌」直接拒绝 —— 否则旧令牌会挡住「用新密码重新鉴权」。
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(hash))
            return Results.Json(
                new { success = false, error = "Admin authentication failed" },
                statusCode: StatusCodes.Status401Unauthorized);

        var memoryMinutes = _config.ReadAuthMemoryMinutes();
        var result = _auth.TryValidateAndIssueToken(nonce, hash, adminPassword, memoryMinutes);
        if (!result.Ok)
            return Results.Json(
                new { success = false, error = "Admin authentication failed" },
                statusCode: StatusCodes.Status401Unauthorized);

        // 响应头必须在 next 之前写：响应一旦开始输出就不能再改头
        if (result.Token is not null)
        {
            context.HttpContext.Response.Headers["X-Admin-Token"] = result.Token;
            context.HttpContext.Response.Headers["X-Admin-Token-Expires-In"] = result.ExpiresInSeconds.ToString();
        }

        return await next(context);
    }
}
