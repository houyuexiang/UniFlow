using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using UniFlow.Common.Services;
using UniFlow.DMSAutoOrder.Models;
using UniFlow.DMSAutoOrder.Services;

namespace UniFlow.Tests.DMSAutoOrder.Services;

/// <summary>
/// DMS 删除语句的 DB 层用例（SQLite 跑同一套 SQL，不依赖 MySQL 实例）。
/// <list type="bullet">
/// <item>PitStop 定点删除：只删指定 id 的 running 记录，其它 running 记录**必须保留**（旧实现清整表）；</item>
/// <item>样本整组删除：<b>直接调用生产事务方法</b> <c>DmsDatabaseService.DeleteSampleWithConnectionAsync</c>
///       —— 任一表失败 → 整体回滚（一行都不删）；全部成功 → 提交。
///       （旧用例自己 Begin/Rollback、只调 Core → 是"假闸门"：把生产事务删掉测试仍全绿。）</item>
/// </list>
/// </summary>
public class DmsDatabaseServiceDeleteTests : IDisposable
{
    private readonly string _dbPath;      // 主库文件
    private readonly string _dmsPath;     // 模拟 MySQL 的 "dms" 库（attach 出来的独立文件 → 第二个连接也能看到已提交数据）
    private readonly SqliteConnection _conn;
    private readonly IErrorReporter _errors = Substitute.For<IErrorReporter>();

    public DmsDatabaseServiceDeleteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"uniflow-dms-{Guid.NewGuid():N}.db");
        _dmsPath = Path.Combine(Path.GetTempPath(), $"uniflow-dms-schema-{Guid.NewGuid():N}.db");
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        _conn.Execute("PRAGMA busy_timeout=2000");
        // MySQL 的 "库名.表名" 语法在 SQLite 里用 ATTACH 模拟（同名 schema "dms"，落在独立文件上）
        _conn.Execute($"ATTACH DATABASE '{_dmsPath}' AS dms");
        _conn.Execute("CREATE TABLE dms.pitstop20261107 (idpitstop TEXT, flgrunning TEXT, note TEXT)");
        _conn.Execute("CREATE TABLE dms.reqtube (codsid TEXT, codoid TEXT)");
        _conn.Execute("CREATE TABLE dms.orders (codoid TEXT, note TEXT)");
    }

    private void InsertPitstop(string id, string running, string note = "") =>
        _conn.Execute("INSERT INTO dms.pitstop20261107 (idpitstop, flgrunning, note) VALUES (@Id,@Run,@Note)",
            new { Id = id, Run = running, Note = note });

    private List<string> PitstopIds() =>
        _conn.Query<string>("SELECT idpitstop FROM dms.pitstop20261107 ORDER BY idpitstop").ToList();

    private int Count(string table) =>
        _conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM dms.{table}");

    /// <summary>第二个连接（attach 同一个 dms 文件）→ 只能看到**已提交**的数据。</summary>
    private SqliteConnection OpenOther()
    {
        var c = new SqliteConnection($"Data Source={_dbPath}");
        c.Open();
        c.Execute("PRAGMA busy_timeout=2000");
        c.Execute($"ATTACH DATABASE '{_dmsPath}' AS dms");
        return c;
    }

    private static DmsDatabaseService CreateService(IErrorReporter errors) =>
        new DmsDatabaseService(
            NullLogger<DmsDatabaseService>.Instance,
            new MySqlConnectionFactory("127.0.0.1", 3306, "dms", "u", "p"),
            new DmsOrderConfig { DbName = "dms" },
            errors);

    // ---------- PitStop 定点删除 ----------

    [Fact]
    public async Task DeletePitstopRecords_DeletesOnlyGivenIds_KeepsOtherRunningRows()
    {
        InsertPitstop("A", "Y");
        InsertPitstop("B", "Y");      // 另一条仍在运行、未超时 → 必须保留
        InsertPitstop("C", "");       // flgrunning 为空 → 非运行中 → 条件不命中，保留

        var affected = await DmsDatabaseService.DeletePitstopRecordsCoreAsync(
            _conn, "dms.pitstop20261107", new[] { "A" });

        affected.ShouldBe(1);
        PitstopIds().ShouldBe(new[] { "B", "C" });
    }

    [Fact]
    public async Task DeletePitstopRecords_MultipleIds_DeletesOnlyThose()
    {
        InsertPitstop("A", "Y");
        InsertPitstop("B", "Y");
        InsertPitstop("C", "Y");

        var affected = await DmsDatabaseService.DeletePitstopRecordsCoreAsync(
            _conn, "dms.pitstop20261107", new[] { "A", "C" });

        affected.ShouldBe(2);
        PitstopIds().ShouldBe(new[] { "B" });
    }

    [Fact]
    public async Task DeletePitstopRecords_EmptyOrBlankIds_DeletesNothing()
    {
        InsertPitstop("A", "Y");

        (await DmsDatabaseService.DeletePitstopRecordsCoreAsync(_conn, "dms.pitstop20261107", Array.Empty<string>()))
            .ShouldBe(0);
        (await DmsDatabaseService.DeletePitstopRecordsCoreAsync(_conn, "dms.pitstop20261107", new[] { "", "  " }))
            .ShouldBe(0);

        PitstopIds().ShouldBe(new[] { "A" });
    }

    [Fact]
    public void IdentifierWhitelist_RejectsUnsafeNames()
    {
        // 表名/库名会拼进 SQL → 只允许字母/数字/下划线
        DmsDatabaseService.IsSafeIdentifier("pitstop20261107").ShouldBeTrue();
        DmsDatabaseService.IsSafeIdentifier("dms").ShouldBeTrue();
        DmsDatabaseService.IsSafeIdentifier("pit stop; DROP TABLE x").ShouldBeFalse();
        DmsDatabaseService.IsSafeIdentifier("pitstop-1").ShouldBeFalse();
        DmsDatabaseService.IsSafeIdentifier("").ShouldBeFalse();
        DmsDatabaseService.IsSafeIdentifier(null).ShouldBeFalse();
    }

    // ---------- 样本整组删除：生产事务方法（真闸门）----------

    private void InsertReqTube(string sid, string oid) =>
        _conn.Execute("INSERT INTO dms.reqtube (codsid, codoid) VALUES (@S,@O)", new { S = sid, O = oid });

    private void InsertOrder(string oid) =>
        _conn.Execute("INSERT INTO dms.orders (codoid, note) VALUES (@O,'x')", new { O = oid });

    private static readonly string[] OkTemplates =
    {
        "DELETE FROM dms.reqtube WHERE codsid=@sid",
        "DELETE FROM dms.orders WHERE codoid=@oid"
    };

    /// <summary>第二张表引用不存在的列 → 执行失败（触发回滚）。</summary>
    private static readonly string[] FailingTemplates =
    {
        "DELETE FROM dms.reqtube WHERE codsid=@sid",
        "DELETE FROM dms.orders WHERE codoid=@oid AND no_such_column=1"
    };

    [Fact]
    public async Task DeleteSampleWithConnection_SecondTableFails_RollsBack_KeepsBothTablesIntact_ReturnsZero()
    {
        InsertReqTube("S1", "O1");
        InsertOrder("O1");
        var svc = CreateService(_errors);

        var affected = await svc.DeleteSampleWithConnectionAsync(_conn, "S1", "O1", FailingTemplates);

        affected.ShouldBe(0, "回滚 → 一行都没删");
        Count("reqtube").ShouldBe(1, "第一张表的删除必须被回滚");
        Count("orders").ShouldBe(1);

        // 第二个连接（只看得到已提交数据）也必须看到行还在 → 证明没有"提交了半截"
        using var other = OpenOther();
        other.ExecuteScalar<int>("SELECT COUNT(*) FROM dms.reqtube").ShouldBe(1);
        other.ExecuteScalar<int>("SELECT COUNT(*) FROM dms.orders").ShouldBe(1);

        // 错误必须上报（运维可见）
        _errors.Received(1).Report("DmsCleanup", "ERROR", Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteSampleWithConnection_AfterRollback_NoDanglingWriteLock()
    {
        InsertReqTube("S1", "O1");
        InsertOrder("O1");
        var svc = CreateService(_errors);

        await svc.DeleteSampleWithConnectionAsync(_conn, "S1", "O1", FailingTemplates);

        // 回滚后事务必须结束（不能留着写锁）：第二个连接要能立刻写入
        using var other = OpenOther();
        Should.NotThrow(() => other.Execute("INSERT INTO dms.reqtube (codsid, codoid) VALUES ('S2','O2')"));
    }

    [Fact]
    public async Task DeleteSampleWithConnection_AllTablesOk_Commits_ReturnsRowCount()
    {
        InsertReqTube("S1", "O1");
        InsertOrder("O1");
        var svc = CreateService(_errors);

        var affected = await svc.DeleteSampleWithConnectionAsync(_conn, "S1", "O1", OkTemplates);

        affected.ShouldBe(2, "两张表各删 1 行");
        Count("reqtube").ShouldBe(0);
        Count("orders").ShouldBe(0);
        _errors.DidNotReceive().Report(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteSampleWithConnection_AllTablesOk_Commit_IsVisibleToOtherConnections()
    {
        InsertReqTube("S1", "O1");
        InsertOrder("O1");
        var svc = CreateService(_errors);

        await svc.DeleteSampleWithConnectionAsync(_conn, "S1", "O1", OkTemplates);

        // 第二个连接只看得到**已提交**的数据 → 行必须已经消失（证明 Commit 真的执行了）
        using var other = OpenOther();
        other.ExecuteScalar<int>("SELECT COUNT(*) FROM dms.reqtube").ShouldBe(0);
        other.ExecuteScalar<int>("SELECT COUNT(*) FROM dms.orders").ShouldBe(0);
    }

    [Fact]
    public async Task DeleteSampleWithConnection_StartsATransaction()
    {
        InsertReqTube("S1", "O1");
        InsertOrder("O1");
        var svc = CreateService(_errors);
        var events = new List<string>();

        using var spy = new SpyConnection(_conn, e => events.Add(e));
        await svc.DeleteSampleWithConnectionAsync(spy, "S1", "O1", OkTemplates);

        events.ShouldContain("begin", "生产方法必须真的开事务（逐表自动提交 = 半删状态）");
    }

    // ---------- 核心逐表执行（回调语义）----------

    [Fact]
    public async Task DeleteSampleCore_FailedTable_IsReportedBeforeThrow()
    {
        InsertReqTube("S1", "O1");

        string? failedSql = null;
        using var tx = _conn.BeginTransaction();
        await Should.ThrowAsync<Exception>(() => DmsDatabaseService.DeleteSampleCoreAsync(
            _conn, tx,
            new[] { "DELETE FROM dms.reqtube WHERE codsid=@sid AND no_such_column=1" },
            "S1", "O1",
            onFailed: (sql, _) => failedSql = sql));
        await tx.RollbackAsync();

        failedSql.ShouldNotBeNull();
        failedSql!.ShouldContain("no_such_column");
        Count("reqtube").ShouldBe(1);
    }

    public void Dispose()
    {
        _conn.Dispose();
        foreach (var p in new[] { _dbPath, _dmsPath })
        {
            try { File.Delete(p); } catch { /* ignore */ }
        }
    }

    // ===== 记录事务开启的连接代理（只观察 begin，不改行为）=====
    // 注：事务对象**不包装** —— Microsoft.Data.Sqlite 内部会把事务强转成 SqliteTransaction，
    //     包装会导致执行期 InvalidCastException；事务的提交/回滚语义由上面的数据断言把关。

    private sealed class SpyConnection : IDbConnection
    {
        private readonly DbConnection _inner;
        private readonly Action<string> _onEvent;

        public SpyConnection(DbConnection inner, Action<string> onEvent)
        {
            _inner = inner;
            _onEvent = onEvent;
        }

        public string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
        public int ConnectionTimeout => _inner.ConnectionTimeout;
        public string Database => _inner.Database;
        public string DataSource => _inner.DataSource;
        public string ServerVersion => _inner.ServerVersion;
        public ConnectionState State => _inner.State;

        public IDbTransaction BeginTransaction()
        {
            _onEvent("begin");
            return _inner.BeginTransaction();
        }

        public IDbTransaction BeginTransaction(IsolationLevel il)
        {
            _onEvent("begin");
            return _inner.BeginTransaction(il);
        }

        public void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
        public void Close() => _inner.Close();
        public IDbCommand CreateCommand() => _inner.CreateCommand();
        public void Open() => _inner.Open();
        public void Dispose() => _inner.Dispose();
    }
}
