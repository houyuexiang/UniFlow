using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UniFlow.DMSAutoOrder.Models;
using UniFlow.DMSAutoOrder.Services;

namespace UniFlow.Tests.DMSAutoOrder.Services;

/// <summary>
/// PitStop 监控的定点清理用例（P2）。
/// <para>
/// 历史缺陷：一条 idpitstop 超时 → <c>DELETE ... WHERE flgrunning &lt;&gt; ''</c> 清掉**整表** running 记录，
/// 并且 <c>_seen[table].Clear()</c> 把整表计时清零。
/// 现在：只删超时的那条 id，其它 running 记录保留、计时继续。
/// </para>
/// </summary>
public class PitStopMonitorServiceTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = new(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc);
    }

    /// <summary>假 DMS 库：内存维护"运行中"记录，并记录被删的 id。</summary>
    private sealed class FakeDmsDb : IDmsDatabaseService
    {
        public List<string> Tables { get; } = new() { "pitstop20261107" };
        public Dictionary<string, Dictionary<string, string>> Running { get; } = new();
        public List<(string Table, List<string> Ids)> Deleted { get; } = new();

        public Task<List<string>> GetPitStopTablesAsync() => Task.FromResult(Tables);

        public Task<List<Dictionary<string, object>>> GetRunningPitstopRecordsAsync(string tableName)
        {
            var list = new List<Dictionary<string, object>>();
            if (Running.TryGetValue(tableName, out var rows))
                foreach (var kv in rows)
                    list.Add(new Dictionary<string, object>
                    {
                        ["idpitstop"] = kv.Key,
                        ["flgrunning"] = kv.Value
                    });
            return Task.FromResult(list);
        }

        public Task<int> DeletePitstopRecordsAsync(string tableName, IReadOnlyCollection<string> ids)
        {
            Deleted.Add((tableName, ids.ToList()));
            var removed = 0;
            if (Running.TryGetValue(tableName, out var rows))
                foreach (var id in ids)
                    if (rows.Remove(id)) removed++;
            return Task.FromResult(removed);
        }

        // ===== 触发式样本清理（本用例不涉及 → 抛 NotSupported 以免误用）=====

        public Task<List<string>> GetTableColumnsAsync(string columnName)
            => throw new NotSupportedException("PitStop 用例不涉及样本清理");

        public Task<List<string>> GetTriggerSampleIdsAsync(string testName)
            => throw new NotSupportedException("PitStop 用例不涉及样本清理");

        public Task<string> GetOidBySidAsync(string sid)
            => throw new NotSupportedException("PitStop 用例不涉及样本清理");

        public Task<List<string>> GetTestsBySidAsync(string sid)
            => throw new NotSupportedException("PitStop 用例不涉及样本清理");

        public Task<int> DeleteSampleAsync(string sid, string oid, List<string> deleteTableSqlTemplates)
            => throw new NotSupportedException("PitStop 用例不涉及样本清理");
    }

    private static PitStopMonitorService Create(FakeDmsDb db, FakeClock clock, int timeoutMinutes = 1)
    {
        var cfg = new DmsOrderConfig
        {
            PitStop = new DmsPitStopConfig { TimeoutMinutes = timeoutMinutes }
        };
        return new PitStopMonitorService(
            NullLogger<PitStopMonitorService>.Instance, db, cfg, () => clock.Now);
    }

    [Fact]
    public async Task Timeout_DeletesOnlyTheTimedOutId_OtherRunningRecordSurvives()
    {
        var db = new FakeDmsDb();
        var clock = new FakeClock();
        db.Running["pitstop20261107"] = new() { ["A"] = "Y" };
        var svc = Create(db, clock);

        // 第 1 轮：A 首次出现 → 只记起始时间，不删
        await svc.ExecuteAsync();
        db.Deleted.ShouldBeEmpty();

        // 10 分钟后：A 超时（超时阈值 1 分钟），同时新增一条 B
        clock.Now = clock.Now.AddMinutes(10);
        db.Running["pitstop20261107"]["B"] = "Y";
        await svc.ExecuteAsync();

        db.Deleted.Count.ShouldBe(1);
        db.Deleted[0].Table.ShouldBe("pitstop20261107");
        db.Deleted[0].Ids.ShouldBe(new[] { "A" });                    // 只删超时的那一条
        db.Running["pitstop20261107"].Keys.ToList().ShouldBe(new[] { "B" });   // B（未超时）保留
    }

    [Fact]
    public async Task Timeout_KeepsTimingOfOtherRecords_SoTheyAreNotReset()
    {
        var db = new FakeDmsDb();
        var clock = new FakeClock();
        db.Running["pitstop20261107"] = new() { ["A"] = "Y" };
        var svc = Create(db, clock);

        await svc.ExecuteAsync();                       // A 首次出现
        clock.Now = clock.Now.AddMinutes(10);
        db.Running["pitstop20261107"]["B"] = "Y";
        await svc.ExecuteAsync();                       // A 超时被删；B 首次出现（计时起点 = 现在）

        // 再过 10 分钟：B 也超时 → 说明 B 的计时**没有被清空**（老实现 _seen.Clear() 会让 B 重新计时、永不超时）
        clock.Now = clock.Now.AddMinutes(10);
        await svc.ExecuteAsync();

        db.Deleted.Count.ShouldBe(2);
        db.Deleted[1].Ids.ShouldBe(new[] { "B" });
        db.Running["pitstop20261107"].ShouldBeEmpty();
    }

    [Fact]
    public async Task NotYetTimeout_DeletesNothing()
    {
        var db = new FakeDmsDb();
        var clock = new FakeClock();
        db.Running["pitstop20261107"] = new() { ["A"] = "Y", ["B"] = "Y" };
        var svc = Create(db, clock, timeoutMinutes: 60);

        await svc.ExecuteAsync();
        clock.Now = clock.Now.AddMinutes(10);
        await svc.ExecuteAsync();

        db.Deleted.ShouldBeEmpty();
        db.Running["pitstop20261107"].Keys.ToList().ShouldBe(new[] { "A", "B" });
    }

    [Fact]
    public async Task MultipleTimedOutIds_AreDeletedInOneBatch()
    {
        var db = new FakeDmsDb();
        var clock = new FakeClock();
        db.Running["pitstop20261107"] = new() { ["A"] = "Y", ["B"] = "Y" };
        var svc = Create(db, clock);

        await svc.ExecuteAsync();
        clock.Now = clock.Now.AddMinutes(10);
        await svc.ExecuteAsync();

        db.Deleted.Count.ShouldBe(1);
        db.Deleted[0].Ids.OrderBy(x => x).ToList().ShouldBe(new[] { "A", "B" });
        db.Running["pitstop20261107"].ShouldBeEmpty();
    }
}
