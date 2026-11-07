using Shouldly;
using UniFlow.Common.Services;

namespace UniFlow.Tests.Common.Services;

/// <summary>
/// 单实例保护（Mutex）用例（P3）。
/// <para>
/// 老 Delphi 版有 Mutex，.NET 版此前缺失 → 两个实例会同时抢 Web 端口 / Aptio 连接 / 重复清理数据。
/// 现在：<see cref="SingleInstanceGuard"/> 用命名互斥体（Global\UniFlow → 权限不足时退回 Local\UniFlow）。
/// </para>
/// <para>
/// 注意：本用例**不会**启动 Program.cs（测试宿主只起自己的 Kestrel），
/// 因此不会与被测进程抢同一个全局锁；这里用独立名字做等价验证。
/// </para>
/// </summary>
public class SingleInstanceGuardTests
{
    [Fact]
    public void TryAcquire_First_Wins_Second_ReportsAlreadyRunning_AfterRelease_WinsAgain()
    {
        var name = "UniFlow.Test." + Guid.NewGuid().ToString("N");
        var local = name + ".Local";

        var g1 = SingleInstanceGuard.TryAcquire(name, local, out var s1, out var e1);
        g1.ShouldNotBeNull("第一个实例应拿到锁");
        s1.ShouldBe(SingleInstanceStatus.Acquired);
        e1.ShouldBeNull();

        var g2 = SingleInstanceGuard.TryAcquire(name, local, out var s2, out var e2);
        g2.ShouldBeNull("第二个实例必须拿不到锁");
        s2.ShouldBe(SingleInstanceStatus.AlreadyRunning);
        e2.ShouldNotBeNull();

        g1!.Dispose();

        var g3 = SingleInstanceGuard.TryAcquire(name, local, out var s3, out _);
        g3.ShouldNotBeNull("释放后应能重新拿到锁");
        s3.ShouldBe(SingleInstanceStatus.Acquired);
        g3!.Dispose();
    }

    [Fact]
    public void TryAcquire_Dispose_IsIdempotent()
    {
        var name = "UniFlow.Test." + Guid.NewGuid().ToString("N");
        var g = SingleInstanceGuard.TryAcquire(name, name + ".Local", out _, out _);
        g.ShouldNotBeNull();
        g!.Dispose();
        Should.NotThrow(() => g.Dispose());
    }

    [Fact]
    public void Guard_KeepsMutexName_ForDiagnostics()
    {
        var name = "UniFlow.Test." + Guid.NewGuid().ToString("N");
        var g = SingleInstanceGuard.TryAcquire(name, name + ".Local", out _, out _);
        g.ShouldNotBeNull();
        g!.Name.ShouldBe(name);
        g.Dispose();
    }
}
