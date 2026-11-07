using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Services;
using UniFlow.DMSAutoOrder.Models;
using UniFlow.DMSAutoOrder.Services;

namespace UniFlow.Tests.DMSAutoOrder.Services;

/// <summary>
/// M-08：删除回滚（affected=0）告警按 sid 节流的用例。
/// <para>
/// 历史缺陷：删除回滚会**保留计时**并在下一轮继续重试 → 每轮都打一条
/// <c>LogWarning("... affected 0 row(s) ...")</c>；持续失败 = 每轮刷一条日志。
/// 现在：同一 sid 的回滚告警最多 10 分钟一条；首次失败与累计失败升级的 Error 始终可见。
/// </para>
/// </summary>
public class SampleCleanupServiceThrottleTests
{
    private const string TestName = "RMSMP";
    private const string Sid = "S1";
    private const string Oid = "O1";

    private readonly IDmsDatabaseService _db = Substitute.For<IDmsDatabaseService>();
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IErrorReporter _errors = Substitute.For<IErrorReporter>();
    private readonly CapturingLogger _logger = new();
    private DateTime _now = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private SampleCleanupService CreateService()
    {
        var cfg = new DmsOrderConfig
        {
            DbName = "dms",
            SampleCleanup = new DmsSampleCleanupConfig
            {
                // TimeoutMinutes=0 → 第 2 轮即视为超时
                TriggerRules = new List<TriggerRule> { new() { TestName = TestName, TimeoutMinutes = 0 } },
                AptioAction = AptioActionMode.Complete
            }
        };
        var svc = new SampleCleanupService(_logger, _db, cfg, _socket, _errors, () => _now);
        _db.GetTableColumnsAsync(Arg.Any<string>()).Returns(new List<string>());
        _db.GetTriggerSampleIdsAsync(TestName).Returns(new List<string> { Sid });
        _db.GetOidBySidAsync(Sid).Returns(Oid);
        _db.GetTestsBySidAsync(Sid).Returns(new List<string> { "T1" });
        _socket.Connected.Returns(true);
        _socket.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OK:SENT"));
        return svc;
    }

    private int RollbackWarnings() => _logger.Entries.Count(e =>
        e.Level == LogLevel.Warning && e.Message.Contains("affected 0 row(s)"));

    private int RollbackErrors() => _logger.Entries.Count(e =>
        e.Level == LogLevel.Error && e.Message.Contains("affected 0 row(s)"));

    [Fact]
    public async Task Rollback_LogsWarningOnlyOnce_WithinThrottleWindow()
    {
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 第 1 轮：登记计时（不删）
        await svc.ExecuteAsync();   // 第 2 轮：超时 → 删除 → 回滚 → 第 1 条 Warning
        await svc.ExecuteAsync();   // 第 3 轮：仍在 10 分钟窗口内 → 应被节流
        await svc.ExecuteAsync();   // 第 4 轮：同上
        await svc.ExecuteAsync();   // 第 5 轮：同上

        // 关键：节流窗口内只记一条 Warning（不是每轮一条）
        RollbackWarnings().ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_FirstFailure_IsAlwaysVisible()
    {
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 第 1 轮：登记计时
        await svc.ExecuteAsync();   // 第 2 轮：首次回滚 → 必须可见

        RollbackWarnings().ShouldBe(1);
        _logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(Sid)).ShouldBeTrue();
    }

    [Fact]
    public async Task Rollback_WarnsAgain_AfterThrottleWindowExpires()
    {
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 登记计时
        await svc.ExecuteAsync();   // 首次回滚 → Warning #1
        RollbackWarnings().ShouldBe(1);

        // 推进到节流窗口之外（10 分钟 + 1 秒）
        _now = _now.Add(SampleCleanupService.RollbackWarnInterval).AddSeconds(1);
        await svc.ExecuteAsync();

        // 窗口外 → 再次告警（持续失败仍然可见）
        RollbackWarnings().ShouldBe(2);
    }

    [Fact]
    public async Task Rollback_EscalatesToError_AfterRepeatedFailures()
    {
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 登记计时
        // 连续失败到阈值（默认 10 次）→ 升级 Error（关键失败必须可见）
        for (var i = 0; i < SampleCleanupService.RollbackErrorEscalationCount; i++)
            await svc.ExecuteAsync();

        RollbackErrors().ShouldBe(1);
        // Warning 仍然只有首次那一条（升级的那次是 Error，不是重复 Warning）
        RollbackWarnings().ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_EscalationError_IsLogged_OnlyOnce_NotEveryRound()
    {
        // 复核 D1：旧实现 `escalated = fails >= 10` → 第 10 轮起**每轮**一条 Error（3s/条刷屏）。
        // 现在：只在累计失败次数是阈值倍数时记一条 Error（第 10 次一条，第 20 次再一条）。
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 登记计时（第 1 轮不删）
        // 连续失败 13 轮（累计失败 13 次）→ 越过阈值 10，但**不能**每轮都记 Error
        for (var i = 0; i < 13; i++)
            await svc.ExecuteAsync();

        RollbackErrors().ShouldBe(1, "第 10 次失败记一条 Error 之后，第 11~13 次不应再记 Error");
        // Warning 仍然只有首次那一条（其余被 10 分钟节流）
        RollbackWarnings().ShouldBe(1);
    }

    [Fact]
    public async Task Rollback_EscalationError_Repeats_EveryN_Failures()
    {
        // 每 10 次提醒一次（第 10、20 次各一条），不是每轮一条。
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 登记计时
        // 连续失败 19 轮 → 累计 19 次 → 只在第 10 次记一条 Error
        for (var i = 0; i < 19; i++)
            await svc.ExecuteAsync();
        RollbackErrors().ShouldBe(1);

        await svc.ExecuteAsync();   // 第 20 次失败 → 再记一条 Error
        RollbackErrors().ShouldBe(2, "第 20 次失败应再记一条升级 Error");
        RollbackWarnings().ShouldBe(1, "升级用的是 Error，不应产生额外 Warning");
    }

    [Fact]
    public async Task Rollback_Success_Resets_FailCounter()
    {
        // 删除成功后计数被清零 → 再次失败从 1 重新累计（不会立刻又升级）。
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 登记计时
        for (var i = 0; i < SampleCleanupService.RollbackErrorEscalationCount - 1; i++)
            await svc.ExecuteAsync();   // 累计失败 9 次（还没到升级点）
        RollbackErrors().ShouldBe(0);

        // 第 10 次改成成功 → 计数清零、计时清除
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(1);
        await svc.ExecuteAsync();
        RollbackErrors().ShouldBe(0);

        // 再次失败 → 从 1 重新累计（不会因为旧计数直接跳到升级点）
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);
        await svc.ExecuteAsync();   // 重新登记计时（成功后计时被清除）
        await svc.ExecuteAsync();   // 第 1 次失败
        RollbackErrors().ShouldBe(0, "计数已清零 → 第 1 次失败不应升级为 Error");
    }

    [Fact]
    public async Task Rollback_Throttle_IsPerSid()
    {
        var svc = CreateService();
        // 两个不同 sid：各自的失败互不影响（各自首条都要可见）
        _db.GetTriggerSampleIdsAsync(TestName).Returns(new List<string> { "S1", "S2" });
        _db.GetOidBySidAsync("S2").Returns("O2");
        _db.GetTestsBySidAsync("S2").Returns(new List<string> { "T1" });
        _db.DeleteSampleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();   // 第 1 轮：登记计时
        await svc.ExecuteAsync();   // 第 2 轮：S1 与 S2 都回滚 → 各自一条 Warning

        RollbackWarnings().ShouldBe(2);
    }

    [Fact]
    public async Task Rollback_Throttle_DoesNotSuppress_AptioSafetyBehaviour()
    {
        // 节流只影响**日志**，不影响"回滚不发 Aptio 报文 + 保留计时重试"的安全语义
        var svc = CreateService();
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await svc.ExecuteAsync();
        await svc.ExecuteAsync();
        await svc.ExecuteAsync();

        // 绝不发 Aptio 报文（DMS 行还在）
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // 计时保留 → 下一轮还会重试删除
        svc.TrackedTimingKeys.ShouldContain(TestName + "\u0001" + Sid);
        await _db.Received(2).DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>());
    }

    /// <summary>捕获日志的 ILogger（断言 Warning/Error 级日志）。</summary>
    private sealed class CapturingLogger : ILogger<SampleCleanupService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
