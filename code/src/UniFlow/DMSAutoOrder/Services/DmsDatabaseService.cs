using Dapper;
using UniFlow.Common.Services;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Data;
using System.Data.Common;

namespace UniFlow.DMSAutoOrder.Services;

public class DmsDatabaseService : IDmsDatabaseService
{
    private readonly ILogger<DmsDatabaseService> _logger;
    private readonly MySqlConnectionFactory _factory;
    private readonly Models.DmsOrderConfig _config;
    private readonly IErrorReporter _errors;

    public DmsDatabaseService(ILogger<DmsDatabaseService> logger, MySqlConnectionFactory factory, Models.DmsOrderConfig config, IErrorReporter errors)
    {
        _logger = logger;
        _factory = factory;
        _config = config;
        _errors = errors;
    }

    public MySqlConnection NewConnection() => _factory.Create();

    public async Task PingAsync()
    {
        using var conn = NewConnection();
        await conn.PingAsync();
    }

    public async Task<List<string>> GetPitStopTablesAsync()
    {
        using var conn = NewConnection();
        var rows = await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = @Db AND table_name LIKE 'pitstop%'",
            new { Db = _config.DbName });
        return rows.ToList();
    }

    public async Task<List<Dictionary<string, object>>> GetRunningPitstopRecordsAsync(string tableName)
    {
        using var conn = NewConnection();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {_config.DbName}.{tableName} WHERE flgrunning <> ''");
        var result = new List<Dictionary<string, object>>();
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in row.GetType().GetProperties())
                dict[prop.Name] = prop.GetValue(row) ?? "";
            result.Add(dict);
        }
        return result;
    }

    /// <summary>
    /// 删除**指定 id** 的 PitStop 运行中记录（只删这些 id）。
    /// <para>
    /// 历史缺陷（P2）：曾按表整删（DELETE ... WHERE flgrunning &lt;&gt; ''）→
    /// 一条记录超时就把整张表的 running 记录全部清掉。现在只删传入的 id。
    /// </para>
    /// </summary>
    /// <returns>实际删除行数（0 = id 列表为空 / 表名不合法）。</returns>
    public async Task<int> DeletePitstopRecordsAsync(string tableName, IReadOnlyCollection<string> ids)
    {
        // 表名/库名要拼进 SQL → 必须过标识符白名单（只允许字母、数字、下划线）
        if (!IsSafeIdentifier(tableName) || !IsSafeIdentifier(_config.DbName))
        {
            _logger.LogError("PitStop delete rejected: unsafe table/db name (Table={Table}, Db={Db})",
                tableName, _config.DbName);
            return 0;
        }
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await DeletePitstopRecordsCoreAsync(conn, $"{_config.DbName}.{tableName}", ids);
    }

    /// <summary>
    /// PitStop 定点删除的核心 SQL（抽出为 public static 以便用 SQLite 做单元测试，不依赖 MySQL 实例）。
    /// <para>SQL：<c>DELETE FROM {tableRef} WHERE idpitstop IN @Ids AND flgrunning &lt;&gt; ''</c>
    /// —— 只命中指定 id，且只删"运行中"标记非空的行。</para>
    /// </summary>
    public static async Task<int> DeletePitstopRecordsCoreAsync(
        IDbConnection conn, string tableRef, IReadOnlyCollection<string> ids)
    {
        if (conn == null || ids == null || ids.Count == 0) return 0;
        var list = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (list.Count == 0) return 0;
        return await conn.ExecuteAsync(
            $"DELETE FROM {tableRef} WHERE idpitstop IN @Ids AND flgrunning <> ''",
            new { Ids = list });
    }

    /// <summary>标识符白名单校验（库名/表名拼进 SQL 前的唯一入口）。</summary>
    public static bool IsSafeIdentifier(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    public async Task<List<(string codsid, string codoid)>> GetUnknownPatientOrdersAsync()
    {
        using var conn = NewConnection();
        var sql = $"SELECT r.codsid, r.codoid FROM {_config.DbName}.reqtube r " +
                  $"WHERE r.codoid IN (SELECT o.codoid FROM {_config.DbName}.orders o, {_config.DbName}.anagpatient a " +
                  $"WHERE o.codaid = a.codaid AND a.codpid = 'UNKNOWN') " +
                  $"AND r.codsid IN (SELECT t.codsid FROM {_config.DbName}.reqtest t WHERE t.codtest <> 'UNKN') " +
                  $"OR r.codoid NOT IN (SELECT o.codoid FROM {_config.DbName}.orders o)";
        var rows = await conn.QueryAsync<(string, string)>(sql);
        return rows.ToList();
    }

    public async Task<int> ExecuteSqlAsync(string sql)
    {
        using var conn = NewConnection();
        return await conn.ExecuteAsync(sql);
    }

    public async Task<int> ExecuteSqlAsync(string sql, object parameters)
    {
        using var conn = NewConnection();
        return await conn.ExecuteAsync(sql, parameters);
    }

    public async Task<List<string>> GetTableColumnsAsync(string columnName)
    {
        using var conn = NewConnection();
        return (await conn.QueryAsync<string>($@"
            SELECT DISTINCT table_name FROM information_schema.columns
            WHERE column_name = @Col AND table_schema = @Db
              AND table_name IN (SELECT table_name FROM information_schema.tables
              WHERE table_type = 'BASE TABLE' AND table_schema = @Db)
            ORDER BY table_name", new { Col = columnName, Db = _config.DbName })).ToList();
    }

    /// <summary>
    /// 删除一个样本在所有关联表里的记录（整组删除）——<b>事务编排本体</b>。
    /// <para>
    /// 本方法**自带事务**：Begin → 逐表删（<see cref="DeleteSampleCoreAsync"/>）→ Commit；
    /// 任一表失败 → Rollback → 记错误 + 上报 ErrorReporter → 返回 0（一行都没删）。
    /// </para>
    /// <para>
    /// 参数用 <see cref="IDbConnection"/>（SQLite / MySqlConnector 通用）→
    /// 可脱离 MySQL 实例做单元测试（生产仍传 MySqlConnection）。
    /// </para>
    /// <para>注意：MyISAM 等非事务引擎的表不参与回滚（MySQL 本身的限制）。</para>
    /// </summary>
    /// <returns>实际删除行数；发生回滚时返回 0。</returns>
    public async Task<int> DeleteSampleWithConnectionAsync(
        IDbConnection conn, string sid, string oid, IReadOnlyList<string> deleteTableSqlTemplates)
    {
        if (conn == null) return 0;

        var total = 0;
        var okTables = 0;
        var failedTables = 0;

        IDbTransaction? tx = null;
        try
        {
            tx = await BeginTxAsync(conn);
            var (rows, ok) = await DeleteSampleCoreAsync(
                conn, tx, deleteTableSqlTemplates, sid, oid,
                onDeleted: (sql, affected) => _logger.LogInformation("Delete rows: {Count} | {Sql}", affected, sql),
                onFailed: (sql, ex) =>
                {
                    failedTables++;
                    _logger.LogError(ex, "Delete sample failed: Sid={Sid}, Oid={Oid}, SQL={Sql}", sid, oid, sql);
                });
            total = rows;
            okTables = ok;
            await CommitTxAsync(tx);
            // 提交后释放事务句柄
            tx.Dispose();
            tx = null;
        }
        catch (Exception ex)
        {
            if (tx != null)
            {
                var rolledBack = false;
                try { await RollbackTxAsync(tx); rolledBack = true; }
                catch (Exception rbEx)
                {
                    _logger.LogWarning(rbEx, "Sample delete rollback failed: Sid={Sid}, Oid={Oid}", sid, oid);
                }
                // 只在"确实回滚成功"后释放事务句柄：
                //   若漏写 Rollback，事务仍处于打开状态（未提交、持写锁）→ 缺陷可被外部观察到，
                //   而不是被 Dispose 的隐式回滚悄悄掩盖掉。
                if (rolledBack)
                {
                    try { tx.Dispose(); } catch { /* ignore */ }
                }
            }
            _logger.LogError(ex,
                "Sample delete rolled back — nothing deleted: Sid={Sid}, Oid={Oid}, OkTables={Ok}, FailedTables={Failed}",
                sid, oid, okTables, failedTables);
            _errors.Report("DmsCleanup", "ERROR",
                $"Sample delete rolled back (no rows deleted): Sid={sid}, Oid={oid} | {ex.Message}");
            return 0;
        }

        if (failedTables == 0)
            _logger.LogInformation("Sample deleted: Sid={Sid}, Oid={Oid}, TotalRows={Total}, Tables={Ok}",
                sid, oid, total, okTables);
        else
            _logger.LogWarning("Sample delete incomplete: Sid={Sid}, Oid={Oid}, TotalRows={Total}, OkTables={Ok}, FailedTables={Failed}",
                sid, oid, total, okTables, failedTables);
        return total;
    }

    /// <summary>
    /// 删除一个样本在所有关联表里的记录（<b>整组删除放在一个事务里</b>）。
    /// <para>
    /// 历史缺陷（P2）：逐表单独 ExecuteAsync，某张表失败只计数、不回滚 →
    /// 出现"部分表已删、部分表还在"的半删状态（reqtube 没了但 orders 还在 → 数据不一致）。
    /// 现在：全部删除在**同一事务**内执行，任一表失败 → 整体回滚（一行都不删），
    /// 并记录错误 + 上报 ErrorReporter（不向上抛，保持既有调用方语义：调用方按返回值处理）。
    /// </para>
    /// </summary>
    /// <returns>实际删除行数；发生回滚时返回 0。</returns>
    public async Task<int> DeleteSampleAsync(string sid, string oid, List<string> deleteTableSqlTemplates)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        // 事务编排在 DeleteSampleWithConnectionAsync（可单测，生产与测试同一份代码路径）
        return await DeleteSampleWithConnectionAsync(conn, sid, oid, deleteTableSqlTemplates);
    }

    // ===== 事务原语（DbConnection 走异步；其它 IDbConnection 退化为同步接口）=====

    private static async Task<IDbTransaction> BeginTxAsync(IDbConnection conn)
        => conn is DbConnection db ? await db.BeginTransactionAsync() : conn.BeginTransaction();

    private static async Task CommitTxAsync(IDbTransaction tx)
    {
        if (tx is DbTransaction dbTx) await dbTx.CommitAsync();
        else tx.Commit();
    }

    private static async Task RollbackTxAsync(IDbTransaction tx)
    {
        if (tx is DbTransaction dbTx) await dbTx.RollbackAsync();
        else tx.Rollback();
    }

    /// <summary>
    /// 事务内逐表删除的核心逻辑（抽出为 public static 以便用 SQLite 做单元测试，不依赖 MySQL 实例）。
    /// <para>任一表执行失败 → 回调 onFailed 后**直接抛出**（由调用方回滚）；不吞异常。</para>
    /// </summary>
    /// <param name="onDeleted">每张表删除成功后的回调（参数：SQL、影响行数）——用于日志。</param>
    /// <param name="onFailed">某张表删除失败后的回调（参数：SQL、异常）——用于日志；随后仍会抛出。</param>
    public static async Task<(int Total, int OkTables)> DeleteSampleCoreAsync(
        IDbConnection conn,
        IDbTransaction? tx,
        IReadOnlyList<string> deleteTableSqlTemplates,
        string sid,
        string oid,
        Action<string, int>? onDeleted = null,
        Action<string, Exception>? onFailed = null)
    {
        int total = 0, okTables = 0;
        if (conn == null || deleteTableSqlTemplates == null) return (total, okTables);
        foreach (var sql in deleteTableSqlTemplates)
        {
            try
            {
                var affected = await conn.ExecuteAsync(sql, new { sid, oid }, tx);
                okTables++;
                total += affected;
                if (affected > 0) onDeleted?.Invoke(sql, affected);
            }
            catch (Exception ex)
            {
                onFailed?.Invoke(sql, ex);
                throw;   // 任一表失败 → 抛出 → 调用方回滚（绝不留下"删一半"的中间态）
            }
        }
        return (total, okTables);
    }

    public async Task<string> GetOidBySidAsync(string sid)
    {
        using var conn = NewConnection();
        var oid = await conn.QueryFirstOrDefaultAsync<string>(
            $"SELECT codoid FROM {_config.DbName}.reqtube " +
            $"WHERE codsid = @Sid AND codoid IS NOT NULL AND codoid <> '' LIMIT 1",
            new { Sid = sid });
        return oid ?? "";
    }

    // 取某样本在 reqtest 中的全部测试名（去重、非空），用于拼取消报文
    public async Task<List<string>> GetTestsBySidAsync(string sid)
    {
        using var conn = NewConnection();
        var tests = await conn.QueryAsync<string>(
            $"SELECT DISTINCT codtest FROM {_config.DbName}.reqtest " +
            $"WHERE codsid = @Sid AND codtest IS NOT NULL AND codtest <> ''",
            new { Sid = sid });
        return tests?.Distinct().Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
    }

    // 取某个测试名下的全部样本 sid（去重、非空）——触发式清理的候选集
    public async Task<List<string>> GetTriggerSampleIdsAsync(string testName)
    {
        using var conn = NewConnection();
        var sids = await conn.QueryAsync<string>(
            $"SELECT DISTINCT codsid FROM {_config.DbName}.reqtest " +
            $"WHERE codtest = @TestName",
            new { TestName = testName });
        return sids?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
    }
}
