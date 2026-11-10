using Shouldly;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.WebAdmin.Services;

/// <summary>
/// 管理鉴权（一次性 nonce + SHA-256(密码+nonce)）单元测试。
/// </summary>
public class AdminAuthServiceTests
{
    private const string Password = "Advia1";

    // 可控时钟（避免测试依赖真实时间）
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    [Fact]
    public void CreateChallenge_Returns_64HexLowercase()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();

        nonce.ShouldNotBeNullOrWhiteSpace();
        nonce.Length.ShouldBe(64);                       // 32 字节 → 64 位十六进制
        nonce.ShouldBe(nonce.ToLowerInvariant());        // 小写
        nonce.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')).ShouldBeTrue();
    }

    [Fact]
    public void CreateChallenge_Generates_Different_Nonces()
    {
        var svc = new AdminAuthService();
        var a = svc.CreateChallenge();
        var b = svc.CreateChallenge();
        a.ShouldNotBe(b);
        svc.ActiveNonceCount.ShouldBe(2);
    }

    [Fact]
    public void TryValidate_WithCorrectHash_Succeeds()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);

        svc.TryValidate(nonce, hash, Password).ShouldBeTrue();
    }

    [Fact]
    public void ComputeHash_Matches_KnownVector()
    {
        // SHA-256("Advia1abc123") 的十六进制小写（与前端纯 JS 实现同口径）
        AdminAuthService.ComputeHash("Advia1", "abc123")
            .ShouldBe("32ef514ed69fac07284136503cf5e36a6c84683becfc8405b16aa453f339b79b");
    }

    [Fact]
    public void TryValidate_WithWrongHash_Fails()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var wrong = AdminAuthService.ComputeHash("wrong-password", nonce);

        svc.TryValidate(nonce, wrong, Password).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_WithEmptyOrMissingHash_Fails()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();

        svc.TryValidate(nonce, null, Password).ShouldBeFalse();
        svc.TryValidate(nonce, "", Password).ShouldBeFalse();
        svc.TryValidate(null, AdminAuthService.ComputeHash(Password, nonce), Password).ShouldBeFalse();
        svc.TryValidate("", AdminAuthService.ComputeHash(Password, nonce), Password).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_WithUnknownNonce_Fails()
    {
        var svc = new AdminAuthService();
        var fake = "0".PadRight(64, '0');
        svc.TryValidate(fake, AdminAuthService.ComputeHash(Password, fake), Password).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_AfterExpiry_Fails()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);

        clock.Advance(TimeSpan.FromSeconds(AdminAuthService.NonceTtlSeconds + 1));
        svc.TryValidate(nonce, hash, Password).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_JustBeforeExpiry_Succeeds()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);

        clock.Advance(TimeSpan.FromSeconds(AdminAuthService.NonceTtlSeconds - 1));
        svc.TryValidate(nonce, hash, Password).ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_IsOneShot_SecondUseFails()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);

        svc.TryValidate(nonce, hash, Password).ShouldBeTrue();
        svc.TryValidate(nonce, hash, Password).ShouldBeFalse();   // 重放必然失败
    }

    [Fact]
    public void TryValidate_FailedAttempt_AlsoConsumesNonce()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var wrong = AdminAuthService.ComputeHash("wrong", nonce);

        svc.TryValidate(nonce, wrong, Password).ShouldBeFalse();
        // 一次性：失败的 nonce 也不可再用（正确哈希同样失败）
        svc.TryValidate(nonce, AdminAuthService.ComputeHash(Password, nonce), Password).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_WithEmptyPassword_Fails()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);

        svc.TryValidate(nonce, hash, "").ShouldBeFalse();
        svc.TryValidate(nonce, hash, null).ShouldBeFalse();
    }

    [Fact]
    public void ExpiredNonces_AreCleanedUp_WhenCreatingNewChallenge()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var old = svc.CreateChallenge();
        clock.Advance(TimeSpan.FromSeconds(AdminAuthService.NonceTtlSeconds + 1));
        svc.CreateChallenge();

        // 过期 nonce 被清掉（只剩新生成的那个）
        svc.ActiveNonceCount.ShouldBe(1);
        svc.TryValidate(old, AdminAuthService.ComputeHash(Password, old), Password).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_WithHash_Of_DifferentLength_Fails()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();

        // 长度不同 / 非十六进制 → 一律失败
        svc.TryValidate(nonce, "abc", Password).ShouldBeFalse();
        svc.TryValidate(nonce, new string('0', 64), Password).ShouldBeFalse();
        svc.TryValidate(nonce, new string('?', 64), Password).ShouldBeFalse();
    }

    [Fact]
    public void NonceStore_Is_Bounded()
    {
        // 防御：无限刷 challenge 不能把内存撑爆（上限 4096）
        var svc = new AdminAuthService();
        for (int i = 0; i < 5000; i++) svc.CreateChallenge();

        svc.ActiveNonceCount.ShouldBeLessThanOrEqualTo(4096);
    }

    // ===== 管理验证记忆（短令牌）=====

    [Fact]
    public void TryValidateAndIssueToken_Succeeds_Returns64HexToken_And300Seconds()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);

        var res = svc.TryValidateAndIssueToken(nonce, hash, Password, 5);

        res.Ok.ShouldBeTrue();
        res.Token.ShouldNotBeNullOrWhiteSpace();
        res.Token!.Length.ShouldBe(64);                       // 32 字节 → 64 位十六进制
        res.Token.ShouldBe(res.Token.ToLowerInvariant());
        res.Token.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')).ShouldBeTrue();
        res.ExpiresInSeconds.ShouldBe(300);                  // 5 分钟
        svc.ActiveTokenCount.ShouldBe(1);
        // 令牌可立即用于后续请求
        svc.TryValidateToken(res.Token, Password, out var remain).ShouldBeTrue();
        remain.ShouldBe(300);
    }

    [Fact]
    public void TryValidateAndIssueToken_WithWrongHash_Fails_NoToken()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var wrong = AdminAuthService.ComputeHash("wrong-password", nonce);

        var res = svc.TryValidateAndIssueToken(nonce, wrong, Password, 5);

        res.Ok.ShouldBeFalse();
        res.Token.ShouldBeNull();
        res.ExpiresInSeconds.ShouldBe(0);
        svc.ActiveTokenCount.ShouldBe(0);
    }

    [Fact]
    public void TryValidateToken_Valid_JustBeforeExpiry_And_Expired_After()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var nonce = svc.CreateChallenge();
        var res = svc.TryValidateAndIssueToken(nonce, AdminAuthService.ComputeHash(Password, nonce), Password, 5);
        var token = res.Token!;

        // 4 分 59 秒：仍有效
        clock.Advance(TimeSpan.FromSeconds(4 * 60 + 59));
        svc.TryValidateToken(token, Password, out var remain).ShouldBeTrue();
        remain.ShouldBe(1);

        // 5 分 01 秒：已过期
        clock.Advance(TimeSpan.FromSeconds(2));
        svc.TryValidateToken(token, Password, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidateAndIssueToken_MemoryZero_NoToken()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();

        var res = svc.TryValidateAndIssueToken(nonce, AdminAuthService.ComputeHash(Password, nonce), Password, 0);

        res.Ok.ShouldBeTrue();          // 密码校验通过
        res.Token.ShouldBeNull();       // 但不记令牌（0 = 关闭验证记忆）
        res.ExpiresInSeconds.ShouldBe(0);
        svc.ActiveTokenCount.ShouldBe(0);
        svc.TryValidateToken("whatever", Password, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidateAndIssueToken_Clamps_Minutes_To_0_60()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var nonce = svc.CreateChallenge();

        // 超过上限 → 截断到 60 分钟
        var res = svc.TryValidateAndIssueToken(nonce, AdminAuthService.ComputeHash(Password, nonce), Password, 600);
        res.Ok.ShouldBeTrue();
        res.ExpiresInSeconds.ShouldBe(60 * 60);

        // 负值 → 按 0 处理（不记令牌）
        var n2 = svc.CreateChallenge();
        var res2 = svc.TryValidateAndIssueToken(n2, AdminAuthService.ComputeHash(Password, n2), Password, -3);
        res2.Ok.ShouldBeTrue();
        res2.Token.ShouldBeNull();
    }

    [Fact]
    public void TryValidateToken_AfterAdminPasswordChanged_Fails_AndTokenRemoved()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var res = svc.TryValidateAndIssueToken(nonce, AdminAuthService.ComputeHash(Password, nonce), Password, 5);
        var token = res.Token!;

        // 密码被改 → 旧令牌立即失效（且被清掉）
        svc.TryValidateToken(token, "NewPassword123", out _).ShouldBeFalse();
        svc.ActiveTokenCount.ShouldBe(0);
        // 原密码也不能再救活（令牌已被移除）
        svc.TryValidateToken(token, Password, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidateToken_Is_Reusable_But_Nonce_Stays_OneShot()
    {
        var svc = new AdminAuthService();
        var nonce = svc.CreateChallenge();
        var hash = AdminAuthService.ComputeHash(Password, nonce);
        var res = svc.TryValidateAndIssueToken(nonce, hash, Password, 5);
        var token = res.Token!;

        // 令牌可重复使用（不像 nonce 一次性）
        svc.TryValidateToken(token, Password, out _).ShouldBeTrue();
        svc.TryValidateToken(token, Password, out _).ShouldBeTrue();
        svc.TryValidateToken(token, Password, out _).ShouldBeTrue();

        // nonce 仍然一次性：重放必然失败
        var n2 = svc.CreateChallenge();
        var h2 = AdminAuthService.ComputeHash(Password, n2);
        svc.TryValidateAndIssueToken(n2, h2, Password, 5).Ok.ShouldBeTrue();
        svc.TryValidateAndIssueToken(n2, h2, Password, 5).Ok.ShouldBeFalse();
    }

    [Fact]
    public void TryValidateToken_With_Empty_Or_Unknown_Token_Fails()
    {
        var svc = new AdminAuthService();
        svc.TryValidateToken(null, Password, out _).ShouldBeFalse();
        svc.TryValidateToken("", Password, out _).ShouldBeFalse();
        svc.TryValidateToken("0".PadRight(64, '0'), Password, out _).ShouldBeFalse();
        // 未配置管理密码 → 一律拒绝
        svc.TryValidateToken("0".PadRight(64, '0'), "", out _).ShouldBeFalse();
        svc.TryValidateToken("0".PadRight(64, '0'), null, out _).ShouldBeFalse();
    }

    [Fact]
    public void PasswordFingerprint_Is_Sha256Hex_NotPlaintext()
    {
        var fp = AdminAuthService.PasswordFingerprint(Password);
        fp.Length.ShouldBe(64);
        fp.ShouldNotBe(Password);
        fp.ShouldBe(AdminAuthService.ComputeHash(Password, ""));   // SHA-256(密码+空串)
    }

    [Fact]
    public void TokenStore_Is_Bounded()
    {
        // 防御：无限发令牌不能把内存撑爆（上限 4096）
        var svc = new AdminAuthService();
        for (int i = 0; i < 5000; i++)
        {
            var n = svc.CreateChallenge();
            svc.TryValidateAndIssueToken(n, AdminAuthService.ComputeHash(Password, n), Password, 5);
        }

        svc.ActiveTokenCount.ShouldBeLessThanOrEqualTo(4096);
    }

    [Fact]
    public void ExpiredTokens_AreCleanedUp_WhenIssuingNewToken()
    {
        var clock = new FakeClock();
        var svc = new AdminAuthService(clock);
        var n1 = svc.CreateChallenge();
        svc.TryValidateAndIssueToken(n1, AdminAuthService.ComputeHash(Password, n1), Password, 5);
        svc.ActiveTokenCount.ShouldBe(1);

        clock.Advance(TimeSpan.FromMinutes(6));   // 令牌过期
        var n2 = svc.CreateChallenge();
        svc.TryValidateAndIssueToken(n2, AdminAuthService.ComputeHash(Password, n2), Password, 5);

        svc.ActiveTokenCount.ShouldBe(1);          // 过期令牌被清掉，只剩新令牌
    }
}
