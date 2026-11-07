using Microsoft.AspNetCore.Http;

namespace UniFlow.WebAdmin.Services;

/// <summary>
/// 破坏性端点的管理鉴权过滤器（一次性 nonce + 哈希，无长会话 token）。
/// <para>
/// 口径：
/// - 管理密码（WebAdmin:AdminPassword）为空/未配置 → <b>403</b> {success:false,error:"Admin password not configured"}；
/// - 头缺失 / nonce 过期 / 哈希不匹配 → <b>401</b> {success:false,error:"Admin authentication failed"}；
/// - 通过 → 继续执行原端点。
/// </para>
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
        var nonce = request.Headers["X-Admin-Nonce"].ToString();
        var hash = request.Headers["X-Admin-Hash"].ToString();

        if (!_auth.TryValidate(nonce, hash, adminPassword))
            return Results.Json(
                new { success = false, error = "Admin authentication failed" },
                statusCode: StatusCodes.Status401Unauthorized);

        return await next(context);
    }
}
