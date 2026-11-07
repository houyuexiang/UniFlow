using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Services;
using UniFlow.DMSAutoOrder.Models;
using UniFlow.DMSAutoOrder.Services;

namespace UniFlow.Tests.DMSAutoOrder.Services;

/// <summary>
/// 触发式样本清理的"删除结果 → Aptio 联动 + 计时"用例（P2 跟进 F-2）。
/// <para>
/// 历史缺陷：删除**回滚**（返回 0 行）后仍无条件发 Aptio 取消/完成报文，并清掉超时计时 →
/// ① 仪器侧已被取消/完成，而 DMS 行还在 → 两边状态不一致；
/// ② 计时被清 → 下一轮重新计时 → 样本永远等不到超时 → 永远不被清理。
/// 现在：只有 affected &gt; 0（确实删掉了）才发报文 + 清计时；返回 0 → 记 Warning、保留计时重试。
/// </para>
/// </summary>
public class SampleCleanupServiceTests
{
    private const string TestName = "RMSMP";
    private const string Sid = "S1";
    private const string Oid = "O1";
    private const string TimingKey = TestName + "\u0001" + Sid;

    private readonly IDmsDatabaseService _db = Substitute.For<IDmsDatabaseService>();
    private readonly IAptioSocketClient _socket = Substitute.For<IAptioSocketClient>();
    private readonly IErrorReporter _errors = Substitute.For<IErrorReporter>();
    private readonly SampleCleanupService _svc;

    public SampleCleanupServiceTests()
    {
        var cfg = new DmsOrderConfig
        {
            DbName = "dms",
            SampleCleanup = new DmsSampleCleanupConfig
            {
                // TimeoutMinutes=0 → 第 2 轮即视为超时（无需真的等待）
                TriggerRules = new List<TriggerRule> { new() { TestName = TestName, TimeoutMinutes = 0 } },
                AptioAction = AptioActionMode.Complete
            }
        };
        _svc = new SampleCleanupService(NullLogger<SampleCleanupService>.Instance, _db, cfg, _socket, _errors);

        // 没有含 codsid/codoid 列的表 → 删除模板为空（不影响本用例关注的"删除结果"语义）
        _db.GetTableColumnsAsync(Arg.Any<string>()).Returns(new List<string>());
        _db.GetTriggerSampleIdsAsync(TestName).Returns(new List<string> { Sid });
        _db.GetOidBySidAsync(Sid).Returns(Oid);
        _db.GetTestsBySidAsync(Sid).Returns(new List<string> { "T1" });
        _socket.Connected.Returns(true);
        _socket.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("OK:SENT"));
    }

    [Fact]
    public async Task DeleteSample_AffectedZero_SkipsAptioAction_AndKeepsTimingForRetry()
    {
        // 删除回滚（或没有匹配行）→ 返回 0
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(0);

        await _svc.ExecuteAsync();     // 第 1 轮：登记计时（不删）
        await _svc.ExecuteAsync();     // 第 2 轮：超时 → 尝试删除 → 返回 0

        // 关键 ①：绝不能发 Aptio 报文（DMS 行还在，仪器侧不能被取消/完成）
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // 关键 ②：计时必须保留 → 下一轮还会再删（而不是重新计时、永远删不掉）
        _svc.TrackedTimingKeys.ShouldContain(TimingKey);
        await _svc.ExecuteAsync();     // 第 3 轮
        await _db.Received(2).DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>());
        _svc.TrackedTimingKeys.ShouldContain(TimingKey);
    }

    [Fact]
    public async Task DeleteSample_AffectedPositive_SendsAptioAction_AndClearsTiming()
    {
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(1);

        await _svc.ExecuteAsync();     // 第 1 轮：登记计时（不删）
        await _svc.ExecuteAsync();     // 第 2 轮：超时 → 删除成功（1 行）

        // 发了 Aptio 联动报文（COMMENT S002^S1\COMPLETE^S）
        await _socket.Received(1).SendAsync(
            Arg.Is<string>(s => s.Contains("COMPLETE") && s.Contains(Sid)),
            Arg.Any<CancellationToken>());

        // 计时被清除 → 第 3 轮只重新登记、不再重复删除
        await _svc.ExecuteAsync();
        await _db.Received(1).DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>());
    }

    [Fact]
    public async Task DeleteSample_AffectedPositive_MultipleRows_StillSendsAptioAction()
    {
        _db.DeleteSampleAsync(Sid, Oid, Arg.Any<List<string>>()).Returns(5);

        await _svc.ExecuteAsync();
        await _svc.ExecuteAsync();

        await _socket.Received(1).SendAsync(
            Arg.Is<string>(s => s.Contains("COMPLETE")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoTimeoutYet_NeitherDeletesNorSends()
    {
        // TimeoutMinutes=60 → 第 1 轮只登记计时，不删、不发
        var cfg = new DmsOrderConfig
        {
            DbName = "dms",
            SampleCleanup = new DmsSampleCleanupConfig
            {
                TriggerRules = new List<TriggerRule> { new() { TestName = TestName, TimeoutMinutes = 60 } },
                AptioAction = AptioActionMode.Complete
            }
        };
        var svc = new SampleCleanupService(NullLogger<SampleCleanupService>.Instance, _db, cfg, _socket, _errors);

        await svc.ExecuteAsync();

        await _db.DidNotReceive().DeleteSampleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<List<string>>());
        await _socket.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        svc.TrackedTimingKeys.ShouldContain(TimingKey);
    }
}
