using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace UniFlow.WebAdmin.Services;

/// <summary>
/// 管理鉴权服务：一次性 nonce + SHA-256(密码 + nonce) 校验。
/// <para>
/// 口径（每次动作一次鉴权，不建长会话）：
/// 1. 前端 GET /api/auth/challenge 取一次性 nonce（盐）；
/// 2. 前端用纯 JS 算 hash = SHA-256(密码 + nonce)，十六进制小写；
/// 3. 请求带 X-Admin-Nonce / X-Admin-Hash 两个头；
/// 4. 后端用同一 nonce 重算哈希并做**常量时间**比较；
/// 5. nonce 60 秒有效、**一次性**（无论成败都消费掉）。
/// </para>
/// <para>
/// 安全说明：明文 HTTP 环境下哈希等价于明文口令在链路上传输（只防旁观者直接读密码，
/// 不防 MITM）。详见 docs/CONFIG-REFERENCE.md「管理密码」小节。
/// </para>
/// </summary>
public sealed class AdminAuthService
{
    /// <summary>nonce 有效期（秒）。</summary>
    public const int NonceTtlSeconds = 60;

    /// <summary>内存中最多保留的 nonce 数量（防御：无限刷 /auth/challenge 撑爆内存）。</summary>
    private const int MaxNonces = 4096;

    // nonce → 过期时刻（UTC）。仅存内存：进程重启即全部失效。
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public AdminAuthService(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前内存中的有效 nonce 数量（测试/观测用）。</summary>
    public int ActiveNonceCount => _nonces.Count;

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

    /// <summary>计算 SHA-256(密码 + nonce) 的十六进制小写表示（与前端纯 JS 实现一致）。</summary>
    public static string ComputeHash(string password, string nonce)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password + nonce))).ToLowerInvariant();

    /// <summary>惰性清理过期 nonce（每次发挑战时顺带做，避免定时器线程）。</summary>
    private void CleanupExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var kv in _nonces)
            if (kv.Value <= now)
                _nonces.TryRemove(kv.Key, out _);
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
