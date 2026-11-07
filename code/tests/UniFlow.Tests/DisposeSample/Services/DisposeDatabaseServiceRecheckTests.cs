using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using UniFlow.DisposeSample.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// W4：后期补查确认丢弃。
/// 用 SQLite 跑同一套 SQL（仅用标准语法），避免依赖 MySQL 实例。
/// </summary>
public class DisposeDatabaseServiceRecheckTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _conn;

    public DisposeDatabaseServiceRecheckTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"uniflow-recheck-{Guid.NewGuid():N}.db");
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        Execute("CREATE TABLE sam_dispose_status (ID INTEGER PRIMARY KEY AUTOINCREMENT, barcode TEXT, send INTEGER DEFAULT 0, send_time TEXT, disposed INTEGER DEFAULT 0, disposed_time TEXT, checkcount INTEGER DEFAULT 0)");
        Execute("CREATE TABLE t_sample (sample_id TEXT, t_location TEXT)");
    }

    private void Execute(string sql) => _conn.Execute(sql);

    private void InsertStatus(string barcode, int send, int disposed)
    {
        _conn.Execute(
            "INSERT INTO sam_dispose_status (barcode, send, send_time, disposed, disposed_time, checkcount) " +
            "VALUES (@B, @S, '20260101000000', @D, NULL, 0)",
            new { B = barcode, S = send, D = disposed });
    }

    private void InsertSample(string sampleId, string location)
    {
        _conn.Execute("INSERT INTO t_sample (sample_id, t_location) VALUES (@S, @L)",
            new { S = sampleId, L = location });
    }

    private int GetInt(string barcode, string column) =>
        _conn.QuerySingle<int>($"SELECT {column} FROM sam_dispose_status WHERE barcode=@B", new { B = barcode });

    private string? GetText(string barcode, string column) =>
        _conn.QuerySingle<string?>($"SELECT {column} FROM sam_dispose_status WHERE barcode=@B", new { B = barcode });

    [Fact]
    public async Task Recheck_LocationAlreadyDiscarded_MarksDisposed()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertSample("S001", "&3-18-000001");

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000");

        hits.ShouldBe(1);
        GetInt("S001", "disposed").ShouldBe(1);
        GetText("S001", "disposed_time").ShouldBe("20261106120000");
    }

    [Fact]
    public async Task Recheck_NotDiscardedYet_DoesNotMarkDisposed()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertSample("S001", "&1-18-000001");   // 仍在 &1（未丢弃）

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000");

        hits.ShouldBe(0);
        GetInt("S001", "disposed").ShouldBe(0);
        GetText("S001", "disposed_time").ShouldBeNull();
    }

    [Fact]
    public async Task Recheck_NodeNotConfigured_DoesNotMarkDisposed()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertSample("S001", "&3-99-000001");   // node 99 未配置

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000");

        hits.ShouldBe(0);
        GetInt("S001", "disposed").ShouldBe(0);
    }

    [Fact]
    public async Task Recheck_AlreadyDisposed_NotCountedAgain()
    {
        InsertStatus("S001", send: 1, disposed: 1);
        InsertSample("S001", "&3-18-000001");

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000");

        hits.ShouldBe(0);
    }

    [Fact]
    public async Task Recheck_NotSentYet_NotSelected()
    {
        InsertStatus("S001", send: 0, disposed: 0);   // send=0（未投递）
        InsertSample("S001", "&3-18-000001");

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 200, "20261106120000");

        hits.ShouldBe(0);
        GetInt("S001", "disposed").ShouldBe(0);
    }

    [Fact]
    public async Task Recheck_RespectsMaxLimit()
    {
        for (var i = 1; i <= 5; i++)
        {
            InsertStatus($"S{i:000}", send: 1, disposed: 0);
            InsertSample($"S{i:000}", "&3-18-000001");
        }

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, new[] { "18" }, 2, "20261106120000");

        hits.ShouldBe(2);
    }

    [Fact]
    public async Task Recheck_NoNodeConfigured_ReturnsZero()
    {
        InsertStatus("S001", send: 1, disposed: 0);
        InsertSample("S001", "&3-18-000001");

        var (hits, _) = await DisposeDatabaseService.RecheckPendingDisposalsCoreAsync(
            _conn, Array.Empty<string>(), 200, "20261106120000");

        hits.ShouldBe(0);
    }

    [Fact]
    public void UpdateStatusClause_Skip_UsesSendEqualsTwoSentinel()
    {
        // skip sentinel：send=2（未知节点跳过），不改变表结构
        DisposeDatabaseService.BuildUpdateSetClause("skip").ShouldContain("send=2");
    }

    [Fact]
    public void DeleteHistorySql_CoversSendOneAndSkipSentinel()
    {
        // send>=1：send=1（已投递）与 send=2（skip sentinel）都按 10 天历史清理
        DisposeDatabaseService.DeleteHistorySql.ShouldContain("send>=1");
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }
}
