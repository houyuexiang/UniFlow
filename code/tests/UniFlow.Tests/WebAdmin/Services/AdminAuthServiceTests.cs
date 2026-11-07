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
}
