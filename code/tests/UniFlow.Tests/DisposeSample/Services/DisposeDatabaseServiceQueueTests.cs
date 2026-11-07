using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// 队列查询 / 重发 / 失败处置的 DB 层用例（SQLite 跑同一套 SQL，不依赖 MySQL 实例）。
/// </summary>
public class DisposeDatabaseServiceQueueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _conn;

    public DisposeDatabaseServiceQueueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"uniflow-queue-{Guid.NewGuid():N}.db");
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        // MySQL 的 IF(cond, a, b) 在 SQLite 里没有同名内建函数 → 注册等价实现，
        // 使「生产同一条 UPDATE 语句」能在 SQLite 上原样执行（口径不漂移）。
        _conn.CreateFunction("if", (long c, long a, long b) => c != 0 ? a : b);
        _conn.Execute(
            "CREATE TABLE sam_dispose_status (ID INTEGER PRIMARY KEY AUTOINCREMENT, barcode TEXT, patient TEXT, " +
            "stype TEXT, location TEXT, send INTEGER DEFAULT 0, send_time TEXT, disposed INTEGER DEFAULT 0, " +
            "disposed_time TEXT, checkcount INTEGER DEFAULT 0, resend_count INTEGER NOT NULL DEFAULT 0)");
        _conn.Execute("CREATE TABLE t_sample (sample_id TEXT, t_location TEXT)");
    }

    private void InsertStatus(string barcode, int send, int disposed = 0, int resendCount = 0,
        int checkcount = 0, string? sendTime = "20260101000000", string? disposedTime = null)
    {
        _conn.Execute(
            "INSERT INTO sam_dispose_status (barcode, patient, stype, location, send, send_time, disposed, disposed_time, checkcount, resend_count) " +
            "VALUES (@B, 'P1', 'S', '&1-18-000001', @S, @ST, @D, @DT, @CC, @RC)",
            new { B = barcode, S = send, D = disposed, DT = disposedTime, ST = sendTime, CC = checkcount, RC = resendCount });
    }

    private void InsertSample(string sampleId, string location) =>
        _conn.Execute("INSERT INTO t_sample (sample_id, t_location) VALUES (@S, @L)", new { S = sampleId, L = location });

    private int GetInt(string barcode, string column) =>
        _conn.QuerySingle<int>($"SELECT {column} FROM sam_dispose_status WHERE barcode=@B", new { B = barcode });

    private string? GetText(string barcode, string column) =>
        _conn.QuerySingle<string?>($"SELECT {column} FROM sam_dispose_status WHERE barcode=@B", new { B = barcode });

    // ---------- 查询 ----------

    [Fact]
    public async Task GetPendingRecords_FiltersByMinResend()
    {
        InsertStatus("S001", send: 1, resendCount: 0);
        InsertStatus("S002", send: 1, resendCount: 2);
        InsertStatus("S003", send: 3, resendCount: 5);
        InsertStatus("S004", send: 1, resendCount: 9, disposed: 1);   // 已丢弃 → 不算待处理
        InsertStatus("S005", send: 2, resendCount: 9);                // skip sentinel → 不算待处理

        var all = await DisposeDatabaseService.GetPendingRecordsCoreAsync(_conn, 0, 100);
        all.Select(r => r.Barcode).ShouldBe(new[] { "S003", "S002", "S001" });   // resend_count DESC

        var min2 = await DisposeDatabaseService.GetPendingRecordsCoreAsync(_conn, 2, 100);
        min2.Select(r => r.Barcode).ShouldBe(new[] { "S003", "S002" });

        var min3 = await DisposeDatabaseService.GetPendingRecordsCoreAsync(_conn, 3, 100);
        min3.Select(r => r.Barcode).ShouldBe(new[] { "S003" });
    }

    [Fact]
    public async Task GetPendingRecords_OrdersByResendCountDesc()
    {
        InsertStatus("S001", send: 1, resendCount: 1);
        InsertStatus("S002", send: 1, resendCount: 7);
        InsertStatus("S003", send: 1, resendCount: 3);

        var rows = await DisposeDatabaseService.GetPendingRecordsCoreAsync(_conn, 0, 100);
        rows.Select(r => r.Barcode).ShouldBe(new[] { "S002", "S003", "S001" });
    }

    [Fact]
    public async Task GetPendingRecords_MapsResendCountAndTimes()
    {
        InsertStatus("S001", send: 1, resendCount: 4, sendTime: "20261106120000", disposedTime: null);

        var rows = await DisposeDatabaseService.GetPendingRecordsCoreAsync(_conn, 0, 10);
        var row = rows.Single();
        row.Barcode.ShouldBe("S001");
        row.Send.ShouldBe(1);
        row.Disposed.ShouldBe(0);
        row.ResendCount.ShouldBe(4);
        row.SendTime.ShouldBe("20261106120000");
        row.DisposedTime.ShouldBeNull();
        row.Checkcount.ShouldBe(0);
    }

    [Fact]
    public async Task GetResendCandidates_RespectsCutoffAndMax()
    {
        InsertStatus("OLD", send: 1, resendCount: 1, sendTime: "20260101000000");
        InsertStatus("NEW", send: 1, resendCount: 1, sendTime: "20990101000000");
        InsertStatus("NOSENT", send: 1, resendCount: 1, sendTime: null);
        InsertStatus("DONE", send: 1, resendCount: 1, sendTime: "20260101000000", disposed: 1);

        var rows = await DisposeDatabaseService.GetResendCandidatesCoreAsync(_conn, "20260102000000", 0, 100);
        rows.Select(r => r.Barcode).ShouldBe(new[] { "OLD" });

        // maxResendCount 生效：resend_count >= max 的不再重发
        InsertStatus("MAXED", send: 1, resendCount: 24, sendTime: "20260101000000");
        var limited = await DisposeDatabaseService.GetResendCandidatesCoreAsync(_conn, "20260102000000", 24, 100);
        limited.Select(r => r.Barcode).ShouldBe(new[] { "OLD" });
    }

    [Fact]
    public async Task GetExhaustedCandidates_OnlyWhenMaxResendCountPositive()
    {
        InsertStatus("S001", send: 1, resendCount: 24);
        InsertStatus("S002", send: 1, resendCount: 3);

        var rows = await DisposeDatabaseService.GetExhaustedCandidatesCoreAsync(_conn, 24, 100);
        rows.Select(r => r.Barcode).ShouldBe(new[] { "S001" });

        // 0 = 不限重发次数 → 没有"用尽"概念
        var none = await DisposeDatabaseService.GetExhaustedCandidatesCoreAsync(_conn, 0, 100);
        none.ShouldBeEmpty();
    }

    [Fact]
    public async Task CountPending_SendZeroAndUndisposed()
    {
        InsertStatus("A", send: 0);
        InsertStatus("B", send: 1, disposed: 0);
        InsertStatus("C", send: 1, disposed: 1);
        InsertStatus("D", send: 3, disposed: 0);

        (await DisposeDatabaseService.CountPendingSendCoreAsync(_conn)).ShouldBe(1);
        (await DisposeDatabaseService.CountPendingCoreAsync(_conn)).ShouldBe(2);   // A + B
    }

    [Fact]
    public async Task CountPendingConfirm_And_CountFailed_AreSeparateBuckets()
    {
        InsertStatus("A", send: 0);                       // 待发
        InsertStatus("B", send: 1, disposed: 0);          // 待确认
        InsertStatus("C", send: 1, disposed: 1);          // 已完成
        InsertStatus("D", send: 3, disposed: 0);          // 失败
        InsertStatus("E", send: 3, disposed: 1);          // 失败但已丢弃
        InsertStatus("F", send: 2, disposed: 0);          // skip sentinel

        (await DisposeDatabaseService.CountPendingSendCoreAsync(_conn)).ShouldBe(1);       // A
        (await DisposeDatabaseService.CountPendingCoreAsync(_conn)).ShouldBe(2);            // A + B
        (await DisposeDatabaseService.CountPendingConfirmCoreAsync(_conn)).ShouldBe(1);    // B
        (await DisposeDatabaseService.CountFailedCoreAsync(_conn)).ShouldBe(1);             // D
    }

    // ===== P2-4：补查轮转窗口（>200 条时尾部最终会被覆盖）=====

    [Fact]
    public async Task Recheck_RotatingWindow_Covers201stRecord()
    {
        for (var i = 1; i <= 201; i++)
            InsertStatus($"S{i:000}", send: 1, disposed: 0);
        InsertSample("S201", "&3-18-000001");      // 只有第 201 条已被丢弃

        // 第 1 轮：前 200 条（ID 1..200）
        var (hits1, lastId1) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000", 0);
        hits1.ShouldBe(0);
        lastId1.ShouldBe(200);

        // 第 2 轮：ID > 200 → 覆盖到第 201 条
        var (hits2, lastId2) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000", lastId1);
        hits2.ShouldBe(1);
        lastId2.ShouldBe(201);
        GetInt("S201", "disposed").ShouldBe(1);

        // 第 3 轮：查完 → LastId=0（调用方把游标归零，下轮从头）
        var (hits3, lastId3) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000", lastId2);
        hits3.ShouldBe(0);
        lastId3.ShouldBe(0);
    }

    // ---------- 写操作 ----------

    // ===== 重排队（send=1 & disposed=0 → send=0）=====

    [Fact]
    public async Task Requeue_SetsSend0_KeepsSendTimeAndResendCount()
    {
        InsertStatus("S001", send: 1, disposed: 0, resendCount: 2, checkcount: 3, sendTime: "20260101000000");
        InsertStatus("DONE", send: 1, disposed: 1, resendCount: 2, sendTime: "20260101000000");
        InsertStatus("NEW", send: 0, resendCount: 2, sendTime: "20260101000000");

        var affected = await DisposeDatabaseService.RequeueForResendCoreAsync(_conn, new[] { "S001", "DONE", "NEW" });

        affected.ShouldBe(1);
        GetInt("S001", "send").ShouldBe(0);
        GetInt("S001", "resend_count").ShouldBe(2);          // 重排队不增重发次数
        GetText("S001", "send_time").ShouldBe("20260101000000");   // 重排队不改投递时间
        GetInt("DONE", "send").ShouldBe(1);                 // 已丢弃的不动
        GetInt("NEW", "send").ShouldBe(0);                   // 本来就是待发，不受影响
    }

    [Fact]
    public async Task Requeue_EmptyList_ReturnsZero()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        (await DisposeDatabaseService.RequeueForResendCoreAsync(_conn, Array.Empty<string>())).ShouldBe(0);
        GetInt("S001", "send").ShouldBe(1);
    }

    // ===== 重发次数：只在「真正发送成功」时 +1 =====

    [Fact]
    public async Task UpdateStatus_Send_IncrementsResendCountOnlyForRequeued()
    {
        // 新记录：send_time 为 NULL → 首次发送 → resend_count +0
        InsertStatus("NEW", send: 0, resendCount: 0, sendTime: null);
        // 被重排队的记录：send_time 非空 → 再次发送 → resend_count +1
        InsertStatus("REQUEUED", send: 0, resendCount: 3, sendTime: "20260101000000");

        // 用生产同一条 SET 子句（DisposeDatabaseService.BuildUpdateSetClause("send")）
        var clause = DisposeDatabaseService.BuildUpdateSetClause("send");
        await _conn.ExecuteAsync($"UPDATE sam_dispose_status SET {clause} WHERE barcode=@B",
            new { B = "NEW", Now = "20261106120000" });
        await _conn.ExecuteAsync($"UPDATE sam_dispose_status SET {clause} WHERE barcode=@B",
            new { B = "REQUEUED", Now = "20261106120000" });

        GetInt("NEW", "resend_count").ShouldBe(0);
        GetInt("REQUEUED", "resend_count").ShouldBe(4);
        GetInt("NEW", "send").ShouldBe(1);
        GetInt("REQUEUED", "send").ShouldBe(1);
        GetText("NEW", "send_time").ShouldBe("20261106120000");
        GetText("REQUEUED", "send_time").ShouldBe("20261106120000");
    }

    [Fact]
    public async Task UpdateStatus_Send_AfterPageReset_CountsAsNewCycle()
    {
        // 页面 reset：send=0, resend_count=0, send_time=NULL → 再次发送视为新周期 → +0
        InsertStatus("R", send: 1, disposed: 0, resendCount: 7, sendTime: "20260101000000");
        await DisposeDatabaseService.ResetRecordsCoreAsync(_conn, new[] { "R" });
        GetText("R", "send_time").ShouldBeNull();

        var clause = DisposeDatabaseService.BuildUpdateSetClause("send");
        await _conn.ExecuteAsync($"UPDATE sam_dispose_status SET {clause} WHERE barcode=@B",
            new { B = "R", Now = "20261106120000" });

        GetInt("R", "resend_count").ShouldBe(0);
        GetInt("R", "send").ShouldBe(1);
    }

    [Fact]
    public async Task MarkFailed_SetsSend3()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertStatus("S002", send: 1, disposed: 0);
        InsertStatus("DONE", send: 1, disposed: 1);

        var affected = await DisposeDatabaseService.MarkFailedCoreAsync(_conn, new[] { "S001", "S002", "DONE" });

        affected.ShouldBe(2);
        GetInt("S001", "send").ShouldBe(3);
        GetInt("S002", "send").ShouldBe(3);
        GetInt("DONE", "send").ShouldBe(1);
    }

    [Fact]
    public async Task MarkFailed_EmptyList_ReturnsZero()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        (await DisposeDatabaseService.MarkFailedCoreAsync(_conn, Array.Empty<string>())).ShouldBe(0);
        GetInt("S001", "send").ShouldBe(1);
    }

    [Fact]
    public async Task Reset_RequeuesToSend0()
    {
        InsertStatus("S001", send: 1, disposed: 0, resendCount: 5, checkcount: 3);
        InsertStatus("S002", send: 3, disposed: 0, resendCount: 24, checkcount: 1);
        InsertStatus("S003", send: 0, disposed: 0);

        var affected = await DisposeDatabaseService.ResetRecordsCoreAsync(_conn, new[] { "S001", "S002", "S003" });

        affected.ShouldBe(2);
        GetInt("S001", "send").ShouldBe(0);
        GetInt("S001", "resend_count").ShouldBe(0);
        GetInt("S001", "checkcount").ShouldBe(0);
        GetText("S001", "send_time").ShouldBeNull();
        GetInt("S002", "send").ShouldBe(0);
        GetInt("S002", "resend_count").ShouldBe(0);
    }

    [Fact]
    public async Task Delete_RemovesRows()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertStatus("S002", send: 0);

        var affected = await DisposeDatabaseService.DeleteRecordsCoreAsync(_conn, new[] { "S001", "S999" });

        affected.ShouldBe(1);
        _conn.QuerySingle<int>("SELECT COUNT(*) FROM sam_dispose_status WHERE barcode='S001'").ShouldBe(0);
        _conn.QuerySingle<int>("SELECT COUNT(*) FROM sam_dispose_status").ShouldBe(1);
    }

    [Fact]
    public async Task RecheckRecord_MarksDisposedWhenLocationMatches()
    {
        InsertStatus("S001", send: 1, disposed: 0, resendCount: 2);
        InsertSample("S001", "&3-18-000001");

        var ok = await DisposeDatabaseService.RecheckRecordCoreAsync(_conn, new[] { "18" }, "S001", "20261106120000");

        ok.ShouldBeTrue();
        GetInt("S001", "disposed").ShouldBe(1);
        GetText("S001", "disposed_time").ShouldBe("20261106120000");
    }

    [Fact]
    public async Task RecheckRecord_NotDiscardedYet_StaysFalse()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertSample("S001", "&1-18-000001");

        var ok = await DisposeDatabaseService.RecheckRecordCoreAsync(_conn, new[] { "18" }, "S001", "20261106120000");

        ok.ShouldBeFalse();
        GetInt("S001", "disposed").ShouldBe(0);
    }

    // ---------- 迁移 SQL（information_schema 为 MySQL 专有，SQLite 无法执行 → 只断言文本）----------

    [Fact]
    public void CreateTableSql_ContainsResendCountColumn()
    {
        DisposeDatabaseService.CreateTableSql.ShouldContain("resend_count");
        DisposeDatabaseService.CreateTableSql.ShouldContain("resend_count INT NOT NULL DEFAULT 0");
    }

    [Fact]
    public void MigrationSql_AddsResendCountColumnSafely()
    {
        DisposeDatabaseService.AddResendCountSql.ShouldContain("resend_count");
        DisposeDatabaseService.AddResendCountSql.ShouldContain("ALTER TABLE sam_dispose_status ADD COLUMN");
        DisposeDatabaseService.ColumnExistsSql.ShouldContain("information_schema.columns");
        DisposeDatabaseService.ColumnExistsSql.ShouldContain("column_name = 'resend_count'");
        DisposeDatabaseService.ColumnExistsSql.ShouldContain("DATABASE()");
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }
}
