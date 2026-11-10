using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace UniFlow.WebAdmin.Services;

/// <summary>
/// 管理鉴权结果：Ok=鉴权是否通过；Token=下发的「验证记忆」短令牌（未开启/未通过时为 null）；
/// ExpiresInSeconds=令牌剩余有效期（秒）。
/// </summary>
public sealed record AuthTokenResult(bool Ok, string? Token, int ExpiresInSeconds);

/// <summary>
/// 管理鉴权服务：一次性 nonce + SHA-256(密码 + nonce) 校验，另加「验证记忆」短令牌。
/// <para>
/// 口径（每次动作一次鉴权，不建长会话）：
/// 1. 前端 GET /api/auth/challenge 取一次性 nonce（盐）；
/// 2. 前端用纯 JS 算 hash = SHA-256(密码 + nonce)，十六进制小写；
/// 3. 请求带 X-Admin-Nonce / X-Admin-Hash 两个头；
/// 4. 后端用同一 nonce 重算哈希并做**常量时间**比较；
/// 5. nonce 60 秒有效、**一次性**（无论成败都消费掉）。
/// </para>
/// <para>
/// 验证记忆（默认 5 分钟，见 WebAdmin:AuthMemoryMinutes）：
/// - 密码校验通过后服务端下发一个**短令牌**（32 字节随机 hex），放在响应头 X-Admin-Token；
/// - 之后的破坏性动作只带 X-Admin-Token 即可（前端不必重复弹密码）；
/// - 令牌**只存服务端内存**（进程重启即失效）、**不保存明文密码**（只存管理密码的 SHA-256 指纹）；
/// - 时长固定、**不滑动续期**；过期 / 密码被改 / 进程重启 → 401（前端重新弹密码）。
/// </para>
/// <para>
/// 安全说明：明文 HTTP 环境下哈希等价于明文口令在链路上传输（只防旁观者直接读密码，
/// 不防 MITM）。短令牌同样是 **bearer 凭证**（谁拿到谁能用），HTTP 抓包者可在其有效期内重放。
/// 详见 docs/CONFIG-REFERENCE.md「管理密码」小节。
/// </para>
/// </summary>
public sealed class AdminAuthService
{
    /// <summary>nonce 有效期（秒）。</summary>
    public const int NonceTtlSeconds = 60;

    /// <summary>验证记忆分钟数上限（配置超出按此值截断）。</summary>
    public const int MaxMemoryMinutes = 60;

    /// <summary>验证记忆分钟数默认值（配置缺失/非法时的兜底）。</summary>
    public const int DefaultMemoryMinutes = 5;

    /// <summary>内存中最多保留的 nonce 数量（防御：无限刷 /auth/challenge 撑爆内存）。</summary>
    private const int MaxNonces = 4096;

    /// <summary>内存中最多保留的短令牌数量（防御：无限刷令牌撑爆内存）。</summary>
    private const int MaxTokens = 4096;

    // nonce → 过期时刻（UTC）。仅存内存：进程重启即全部失效。
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    // 短令牌 → 条目。仅存内存：进程重启即全部失效。
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>短令牌条目：过期时刻 + 管理密码指纹（SHA-256 hex，**不含明文**）。</summary>
    private sealed record TokenEntry(DateTimeOffset ExpiresAt, string PasswordFingerprint);

    public AdminAuthService(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前内存中的有效 nonce 数量（测试/观测用）。</summary>
    public int ActiveNonceCount => _nonces.Count;

    /// <summary>当前内存中的有效短令牌数量（测试/观测用）。</summary>
    public int ActiveTokenCount => _tokens.Count;

    /// <summary>
    /// 生成一次性 nonce（32 字节随机 → 64 位十六进制小写）。
    /// </summary>
    public string CreateChallenge()
    {
        CleanupExpired();
        // 达到上限时挤掉最旧的（避免被无限刷 challenge 撑爆内存）
        while (_nonces.Count >= MaxNonces)
        {
            var oldest = _nonces.OrderBy(kv => kv.Value).FirstOrDefault();
            if (oldest.Key == null) break;
            if (!_nonces.TryRemove(oldest.Key, out _)) break;
        }
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _nonces[nonce] = _time.GetUtcNow().AddSeconds(NonceTtlSeconds);
        return nonce;
    }

    /// <summary>
    /// 校验管理密码哈希。
    /// <para>
    /// - nonce 必须存在且未过期；
    /// - 哈希 = SHA-256(adminPassword + nonce)，十六进制小写；
    /// - 常量时间比较（避免时序侧信道）；
    /// - **无论成功失败都移除 nonce**（一次性，重放必然失败）。
    /// </para>
    /// </summary>
    public bool TryValidate(string? nonce, string? hash, string? adminPassword)
    {
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(hash)) return false;
        // 未配置管理密码 → 一律拒绝（调用方应先返回 403「未配置管理密码」）
        if (string.IsNullOrEmpty(adminPassword)) return false;

        // 一次性：TryRemove 原子取出，校验失败也不可复用
        if (!_nonces.TryRemove(nonce, out var expiresAt)) return false;
        if (_time.GetUtcNow() > expiresAt) return false;

        var expected = ComputeHash(adminPassword!, nonce!);
        return FixedTimeEquals(expected, hash!);
    }

    /// <summary>
    /// 校验管理密码哈希；通过后按 memoryMinutes 下发「验证记忆」短令牌。
    /// <para>
    /// - 走现有 nonce/hash 校验（一次性、60 秒、常量时间）；失败 → (false, null, 0)；
    /// - memoryMinutes &lt;= 0 → (true, null, 0)（不记令牌，每次动作都重新弹密码）；
    /// - 成功 → 生成 32 字节随机 hex 令牌，存内存（过期时刻 + 密码指纹），
    ///   过期时刻 = now + clamp(memoryMinutes, 0, 60) 分钟；上限 4096，满了挤掉最旧的。
    /// </para>
    /// </summary>
    public AuthTokenResult TryValidateAndIssueToken(
        string? nonce, string? hash, string? adminPassword, int memoryMinutes)
    {
        if (!TryValidate(nonce, hash, adminPassword))
            return new AuthTokenResult(false, null, 0);

        var minutes = Math.Clamp(memoryMinutes, 0, MaxMemoryMinutes);
        if (minutes <= 0) return new AuthTokenResult(true, null, 0);

        CleanupExpired();
        // 达到上限时挤掉最旧的（避免被无限刷令牌撑爆内存）
        while (_tokens.Count >= MaxTokens)
        {
            var oldest = _tokens.OrderBy(kv => kv.Value.ExpiresAt).FirstOrDefault();
            if (oldest.Key == null) break;
            if (!_tokens.TryRemove(oldest.Key, out _)) break;
        }

        var now = _time.GetUtcNow();
        var expiresAt = now.AddMinutes(minutes);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokens[token] = new TokenEntry(expiresAt, PasswordFingerprint(adminPassword!));
        var expiresIn = (int)Math.Ceiling((expiresAt - now).TotalSeconds);
        return new AuthTokenResult(true, token, expiresIn);
    }

    /// <summary>
    /// 校验「验证记忆」短令牌。
    /// <para>
    /// - 空 / 不存在 / 已过期 → false；
    /// - 指纹与当前管理密码不一致（密码已改）→ 移除该令牌并 false；
    /// - 有效 → true，expiresInSeconds = 剩余秒数。
    /// </para>
    /// <para>
    /// 令牌**可重复使用**（不像 nonce 那样一次性），直到过期 / 密码变更 / 进程重启。
    /// </para>
    /// </summary>
    public bool TryValidateToken(string? token, string? adminPassword, out int expiresInSeconds)
    {
        expiresInSeconds = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        // 未配置管理密码 → 一律拒绝（调用方应先返回 403「未配置管理密码」）
        if (string.IsNullOrEmpty(adminPassword)) return false;
        if (!_tokens.TryGetValue(token, out var entry)) return false;

        var now = _time.GetUtcNow();
        if (now >= entry.ExpiresAt)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        // 密码已变更 → 旧令牌立即失效（并顺手清掉）
        if (!FixedTimeEquals(entry.PasswordFingerprint, PasswordFingerprint(adminPassword)))
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        var remain = (int)Math.Ceiling((entry.ExpiresAt - now).TotalSeconds);
        if (remain <= 0)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }
        expiresInSeconds = remain;
        return true;
    }

    /// <summary>管理密码指纹：SHA-256 十六进制小写（**不保存明文密码**）。</summary>
    public static string PasswordFingerprint(string adminPassword)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword))).ToLowerInvariant();

    /// <summary>计算 SHA-256(密码 + nonce) 的十六进制小写表示（与前端纯 JS 实现一致）。</summary>
    public static string ComputeHash(string password, string nonce)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password + nonce))).ToLowerInvariant();

    /// <summary>惰性清理过期 nonce 与短令牌（每次发挑战/发令牌时顺带做，避免定时器线程）。</summary>
    private void CleanupExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _nonces)
            if (kv.Value <= now)
                _nonces.TryRemove(kv.Key, out _);
        foreach (var kv in _tokens)
            if (kv.Value.ExpiresAt <= now)
                _tokens.TryRemove(kv.Key, out _);
    }

    /// <summary>常量时间字符串比较（长度不同直接判否；等长逐字节异或累加）。</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        // 用 UTF8（不能用 ASCII：ASCII 会把非 ASCII 字符映射成 '?'，存在误判风险）
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        var diff = 0;
        for (var i = 0; i < ba.Length; i++)
            diff |= ba[i] ^ bb[i];
        return diff == 0;
    }
}
