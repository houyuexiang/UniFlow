using System.Data;
using Dapper;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace UniFlow.DisposeSample.Services;

public class DisposeDatabaseService : IDisposeDatabaseService
{
    private readonly ILogger<DisposeDatabaseService> _logger;
    private readonly MySqlConnectionFactory _factory;
    private readonly IOptionsMonitor<AptioConfig>? _aptio;

    // 按架查询失败 → 退回原查询时的告警节流：10 分钟最多一条 Warning
    private DateTime _lastRackWarnUtc = DateTime.MinValue;

    public DisposeDatabaseService(ILogger<DisposeDatabaseService> logger, MySqlConnectionFactory factory)
        : this(logger, factory, null)
    {
    }

    public DisposeDatabaseService(
        ILogger<DisposeDatabaseService> logger,
        MySqlConnectionFactory factory,
        IOptionsMonitor<AptioConfig>? aptio)
    {
        _logger = logger;
        _factory = factory;
        _aptio = aptio;
    }

    // 配置中的 SRM 节点（补查时逐节点确认丢弃位置）
    // 兜底：去重 + 去空白（配置绑定异常时不至于重复查询同一节点）
    public IReadOnlyList<string> SrmNodeIds =>
        (_aptio?.CurrentValue?.SrmNodeIds ?? (IReadOnlyList<string>)Array.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct()
            .ToList();

    private MySqlConnection NewConnection() => _factory.Create();

    public async Task<bool> PingAsync()
    {
        try
        {
            using var conn = NewConnection();
            await conn.OpenAsync();
            await conn.PingAsync();
            return true;
        }
        catch
        {
            _logger.LogWarning("Database ping failed");
            return false;
        }
    }

    // 重发计数字段是否就绪（建表/迁移成功 → true；旧表 ALTER 失败 → false）
    public bool ResendColumnReady { get; private set; } = true;

    // sam_dispose_status 建表 SQL（含 resend_count：新表直接带该列）
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS sam_dispose_status (
            ID INT NOT NULL AUTO_INCREMENT,
            barcode VARCHAR(25) NOT NULL,
            patient VARCHAR(61),
            stype VARCHAR(4),
            location VARCHAR(34),
            update_time VARCHAR(15),
            res_1 VARCHAR(12),
            res_2 VARCHAR(12),
            rack VARCHAR(34),
            send INT DEFAULT 0,
            send_time VARCHAR(15),
            disposed INT DEFAULT 0,
            disposed_time VARCHAR(15),
            checkcount INT DEFAULT 0,
            check_time VARCHAR(15),
            resend_count INT NOT NULL DEFAULT 0,
            PRIMARY KEY (ID),
            INDEX idx_barcode (barcode),
            INDEX idx_send (send)
        )
        """;

    // 迁移：查 information_schema 判断 resend_count 列是否已存在（MySQL 专有，SQLite 无法执行）
    public const string ColumnExistsSql = """
        SELECT COUNT(*) FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name = 'sam_dispose_status'
          AND column_name = 'resend_count'
        """;

    // 迁移：补列（只增不改，不动其它列）
    public const string AddResendCountSql =
        "ALTER TABLE sam_dispose_status ADD COLUMN resend_count INT NOT NULL DEFAULT 0";

    // sam_dispose_status 表不存在时自动创建（避免启动报错）；已存在的表做安全列迁移
    public async Task EnsureSamDisposeTableAsync()
    {
        try
        {
            using var conn = NewConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(CreateTableSql);
            // 新建表已含 resend_count → Ready
            ResendColumnReady = true;
            await EnsureResendCountColumnAsync(conn);
            _logger.LogInformation("sam_dispose_status table ensured");
        }
        catch (Exception ex)
        {
            // 表都建不出来 → 队列功能整体不可用
            ResendColumnReady = false;
            _logger.LogError(ex, "Ensure sam_dispose_status table failed");
            throw;
        }
    }

    // 旧表补 resend_count 列：先查 information_schema，不存在才 ALTER（幂等，不删改其它列）
    private async Task EnsureResendCountColumnAsync(IDbConnection conn)
    {
        try
        {
            var exists = await conn.QuerySingleAsync<int>(ColumnExistsSql);
            if (exists > 0)
            {
                ResendColumnReady = true;   // 已有该列
                return;
            }
            await conn.ExecuteAsync(AddResendCountSql);
            ResendColumnReady = true;
            _logger.LogInformation("sam_dispose_status migrated: added column resend_count");
        }
        catch (Exception ex)
        {
            // 迁移失败 → 依赖 resend_count 的查询会报 Unknown column，必须显式标记不可用
            ResendColumnReady = false;
            _logger.LogError(ex,
                "Ensure sam_dispose_status.resend_count column failed — auto resend and fail-marking are disabled until the column exists");
        }
    }

    public async Task<int> CheckSrmSampleCountAsync(string nodeId)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var location = $"&1-{nodeId}-%";
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM t_sample WHERE t_status='C' AND t_location LIKE @Loc",
            new { Loc = location });
        _logger.LogDebug("SRM sample count: {Count}", count);
        return count;
    }

    /// <summary>
    /// 架键表达式（SQL 片段，MySQL）：由 <c>location</c> 推出"架"。
    /// <list type="bullet">
    ///   <item><c>&amp;1-{节点}-{架号}-{层}-{槽}-{位}</c>（6 段） → <c>&amp;1-{节点}-{架号}</c>（如 <c>&amp;1-13-000078-05-14-01</c> → <c>&amp;1-13-000078</c>）</item>
    ///   <item><c>&amp;2-{节点}-000000-00-{槽}-{位}</c>（6 段） → <c>&amp;2-{节点}-{槽}</c>（真正的架/槽在第 5 段，如 <c>&amp;2-01-000000-00-18-01</c> → <c>&amp;2-01-18</c>）</item>
    ///   <item><c>&amp;3-*</c>（已丢弃/归档哨兵） → NULL（**排除**，不参与按架排序）</item>
    ///   <item><c>&amp;0-*</c>、NULL、其它前缀、**段数不足 6 段的短格式** → <c>'~other'</c>（**保留**，归入"其它"组，排最后，<b>不静默丢弃</b>）</item>
    /// </list>
    /// <para>
    /// 段数判断用 <c>LIKE '&amp;1-%-%-%-%-%'</c>（5 个 '-' → 6 段），
    /// 与 <see cref="GetLocationRackNo"/> 的 <c>parts.Length &lt; 6</c> 守卫**同口径**：
    /// 短格式（如 <c>&amp;1-13-000078</c>、<c>&amp;2-01-000000-00</c>）两侧都**不产生错误架键**
    /// —— SQL 归 <c>'~other'</c>，C# 返回 <c>""</c>，两者都是"不丢弃"，可接受。
    /// </para>
    /// 与 <see cref="GetLocationRackNo"/> 保持同一口径（C# 侧与 SQL 侧）。
    /// </summary>
    public const string DisposeRackKeyExpression = """
CASE
  WHEN location LIKE '&1-%-%-%-%-%' THEN CONCAT('&1-', SUBSTRING_INDEX(SUBSTRING_INDEX(location,'-',2),'-',-1), '-', SUBSTRING_INDEX(SUBSTRING_INDEX(location,'-',3),'-',-1))
  WHEN location LIKE '&2-%-%-%-%-%' THEN CONCAT('&2-', SUBSTRING_INDEX(SUBSTRING_INDEX(location,'-',2),'-',-1), '-', SUBSTRING_INDEX(SUBSTRING_INDEX(location,'-',5),'-',-1))
  WHEN location LIKE '&3-%' THEN NULL
  ELSE '~other'
END
""";

    /// <summary>
    /// 构造"取丢弃候选"的 SQL。
    /// <para>
    /// <paramref name="byRack"/> = false → 原顺序：<c>SELECT * FROM {safeName} LIMIT @Max</c>（行为与 1.0.7.0 完全一致）。
    /// <paramref name="byRack"/> = true → 按架排序：先按架统计（合格条数 / 最早 update_time），
    /// 再把样本按"合格数降序、最早时间升序、location 升序"排序后取前 N 条，
    /// 使窗口内的 N 条**尽量来自同一架**。
    /// </para>
    /// <para>
    /// 排序口径（<b>架组优先、其它组最后</b>）：
    /// <c>ORDER BY CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC, r.cnt DESC, r.oldest ASC, v.location ASC</c>
    /// —— 第一个条件把"其它组"（<c>&amp;0</c> / NULL / 短格式等）**一律压到最后**，
    /// 避免它们因"条数多"（<c>cnt DESC</c>）而挤掉真正的架；
    /// <c>r.oldest</c>（架级最早 update_time）只决定**架与架之间**的先后，
    /// 同架内由 <c>v.location</c> 升序决定（位置 01→48）。
    /// </para>
    /// <para>
    /// <paramref name="safeName"/> 必须已通过 <see cref="SanitizeSqlName"/> 白名单（<c>schema.view</c> 亦可）。
    /// </para>
    /// </summary>
    public static string BuildDisposeSamplesSql(string safeName, bool byRack)
    {
        if (!byRack) return $"SELECT * FROM {safeName} LIMIT @Max";
        return $"""
SELECT v.*
FROM (
  SELECT z.*, {DisposeRackKeyExpression} AS uni_rack_key
  FROM {safeName} z
) v
JOIN (
  SELECT uni_rack_key, COUNT(*) AS cnt, MIN(update_time) AS oldest
  FROM (
    SELECT update_time, {DisposeRackKeyExpression} AS uni_rack_key
    FROM {safeName} z
  ) t
  WHERE uni_rack_key IS NOT NULL
  GROUP BY uni_rack_key
) r ON r.uni_rack_key = v.uni_rack_key
WHERE v.uni_rack_key IS NOT NULL
ORDER BY CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC, r.cnt DESC, r.oldest ASC, v.location ASC
LIMIT @Max
""";
    }

    public async Task<List<SampleRecord>> GetDisposeSamplesAsync(int cmdType, string cmdName, int maxCount)
    {
        // 先做名称白名单校验（M-05）：不合法 → 直接拒绝，**不连库、不发任何 SQL**。
        // 旧实现是"剔除非法字符"（反引号还能通过）→ 名字被静默改成另一个对象，
        // 注入风险 + 排查困难。现在整名不匹配即拒绝。
        var safeName = SanitizeSqlName(cmdName);
        if (safeName == null)
        {
            _logger.LogError("GetDisposeSamples rejected: invalid command/view name {Name} " +
                             "(only letters, digits and underscore are allowed, " +
                             "optionally with one schema prefix like 'schema.view')", cmdName);
            throw new ArgumentException(
                $"Invalid command/view name '{cmdName}': only [A-Za-z0-9_] are allowed " +
                "(optionally with one 'schema.' prefix).", nameof(cmdName));
        }

        using var conn = NewConnection();
        await conn.OpenAsync();

        List<SampleRecord> samples;
        if (cmdType == 0)
        {
            // 按架排序（默认开启）：只改"候选顺序"，窗口条数不变（仍由调用方的 MaxOnetimeSelectDiscardCount 决定）。
            var byRack = _aptio?.CurrentValue?.DisposeSample?.DisposeByRack ?? true;
            try
            {
                samples = (await conn.QueryAsync<SampleRecord>(
                    BuildDisposeSamplesSql(safeName, byRack), new { Max = maxCount })).AsList();
            }
            catch (Exception ex) when (byRack)
            {
                // 兼容兜底：视图列名/MySQL 版本不支持时，退回原查询，绝不让丢弃停摆
                WarnRackQueryFallback(ex);
                samples = (await conn.QueryAsync<SampleRecord>(
                    $"SELECT * FROM {safeName} LIMIT @Max", new { Max = maxCount })).AsList();
            }
        }
        else
        {
            // 存储过程模式：排序由数据库侧自行决定，DisposeByRack 不生效（仅提示，不报错）
            _logger.LogDebug("GetDisposeSamples: stored procedure mode ({CmdName}) — DisposeByRack does not apply", cmdName);
            samples = (await conn.QueryAsync<SampleRecord>(
                safeName, commandType: CommandType.StoredProcedure)).AsList();
        }

        _logger.LogInformation("GetDisposeSamples returned {Count}", samples.Count);
        return samples;
    }

    // 按架查询失败（视图列名不匹配 / MySQL 版本不支持等）→ 退回原查询时的告警节流：10 分钟最多一条 Warning
    private void WarnRackQueryFallback(Exception ex)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRackWarnUtc).TotalMinutes < 10) return;
        _lastRackWarnUtc = now;
        _logger.LogWarning(ex,
            "rack-aware query failed, fallback to plain view query; disposal continues");
    }

    public async Task<int> InsertDisposeRecordsAsync(int needCount, List<SampleRecord> samples)
    {
        var exist = 0;
        var inserted = 0;
        using var conn = NewConnection();
        await conn.OpenAsync();

        foreach (var s in samples)
        {
            if (inserted >= needCount) break;
            var existing = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT barcode FROM sam_dispose_status WHERE barcode=@B", new { s.Barcode });
            if (existing != null) { exist++; continue; }

            var rack = GetLocationRackNo(s.Location);
            var sql = """
                INSERT INTO sam_dispose_status
                    (barcode,patient,stype,location,update_time,res_1,res_2,rack,send,disposed,checkcount)
                VALUES (@Barcode,@Patient,@Stype,@Location,@UpdateTime,@Res1,@Res2,@Rack,0,0,0)
                """;
            try
            {
                await conn.ExecuteAsync(sql, new { s.Barcode, s.Patient, s.Stype, s.Location, s.UpdateTime, s.Res1, s.Res2, Rack = rack });
                inserted++;
                _logger.LogInformation("Inserted dispose record: Barcode={Barcode}, Location={Location}, Rack={Rack}",
                    s.Barcode, s.Location, rack);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Insert dispose record failed: Barcode={Barcode}, SQL={Sql}", s.Barcode, sql);
            }
        }
        _logger.LogInformation("Insert summary: Inserted={Inserted} Exists={Exist}", inserted, exist);
        return inserted;
    }

    public async Task<DisposeStatus?> SelectOneSendRecordAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<DisposeStatus>(
            "SELECT barcode,location,stype,res_1 FROM sam_dispose_status WHERE send=0 ORDER BY ID LIMIT 1");
    }

    // 历史清理：send>=1 覆盖 send=1（已投递）与 send=2（skip sentinel），两者都按 10 天清理
    public const string DeleteHistorySql =
        "DELETE FROM sam_dispose_status WHERE send>=1 AND TIMESTAMPDIFF(DAY,send_time,NOW())>10";

    public static string BuildUpdateSetClause(string field) => field switch
    {
        // 重发次数只在「真正发送成功」时 +1：
        //   send_time IS NULL → 新插入的记录（首次发送）→ +0
        //   send_time 非空   → 被重排队放回待发队列的记录（再次发送）→ +1
        //   页面 reset 会把 send_time 置 NULL → 视为新周期 → +0
        // 注意：MySQL 的 SET 赋值按从左到右生效，resend_count 必须写在 send_time=@Now 之前，
        //       否则判断的是刚写入的新 send_time（永远非空 → 永远 +1）。
        "send" => "resend_count = resend_count + IF(send_time IS NULL, 0, 1), send=1, send_time=@Now, checkcount=0",
        // skip sentinel：send=2（未知节点跳过）。仍写 send_time，使其与 send=1 一样按 10 天历史清理
        "skip" => "send=2, send_time=@Now",
        "disposed" => "disposed=1, disposed_time=@Now",
        "checkcount" => "checkcount=checkcount+1, check_time=@Now",
        _ => throw new ArgumentException($"Unknown field: {field}")
    };

    /// <summary>
    /// 更新记录状态字段。返回 true = 写入成功；false = 失败（异常已 LogError，**不再向上抛**）。
    /// <para>
    /// 失败时数据库状态未推进（记录仍是 send=0 等原值），调用方必须据此决定是否推进状态机
    /// （见 DisposeSampleWorker.HandleReadyAsync / HandleAckAsync）。
    /// </para>
    /// </summary>
    public async Task<bool> UpdateStatusAsync(string barcode, string field)
    {
        var now = DateTime.Now.ToString("yyyyMMddHHmmss");
        var setClause = BuildUpdateSetClause(field);   // 未知 field → ArgumentException（编程错误，不吞）
        var sql = $"UPDATE sam_dispose_status SET {setClause} WHERE barcode=@B";
        try
        {
            using var conn = NewConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(sql, new { B = barcode, Now = now });
            _logger.LogInformation("Update dispose record: Barcode={Barcode}, Field={Field}", barcode, field);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update dispose record failed: Barcode={Barcode}, Field={Field}, SQL={Sql}", barcode, field, sql);
            return false;
        }
    }

    public async Task<bool> CheckDisposedAsync(string barcode, string nodeId)
    {
        var location = $"&3-{nodeId}-%";
        using var conn = NewConnection();
        await conn.OpenAsync();
        var result = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT sample_id FROM t_sample WHERE sample_id=@B AND t_location LIKE @Loc",
            new { B = barcode, Loc = location });
        return result != null;
    }

    public async Task<int> GetCheckCountAsync(string barcode)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<int>(
            "SELECT checkcount FROM sam_dispose_status WHERE barcode=@B", new { B = barcode });
    }

    // 后期补查：确认「已投递但未观测到丢弃」的记录（轮转窗口）
    //   1) 取 send=1 & disposed=0 且 ID > afterId 的记录（最多 max 条，ORDER BY ID）
    //   2) 对配置的全部 SrmNodeIds 各查一次 t_sample 位置 &3-{node}-%
    //   3) 命中的批量置 disposed=1
    //   4) 返回 (命中数, 本批最大 ID)；本批为空 → LastId=0（调用方把游标归零，下轮从头）
    public async Task<(int Hits, long LastId)> RecheckPendingDisposalsAsync(int max = 200, long afterId = 0)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var now = DateTime.Now.ToString("yyyyMMddHHmmss");
        var (hits, lastId) = await RecheckPendingDisposalsCoreAsync(conn, SrmNodeIds, max, now, afterId);
        // 只记 Debug：命中信息由 DisposeSampleWorker 以 Information 记录，避免重复日志
        if (hits > 0)
            _logger.LogDebug("Dispose recheck confirmed {Hits} pending disposal(s)", hits);
        return (hits, lastId);
    }

    /// <summary>
    /// 补查核心逻辑（抽出为 public static 以便用 SQLite 做单元测试，不依赖 MySQL 实例）。
    /// SQL 只用标准语法（SELECT/LIKE/IN/UPDATE），MySQL 与 SQLite 均可执行。
    /// </summary>
    public static async Task<(int Hits, long LastId)> RecheckPendingDisposalsCoreAsync(
        IDbConnection conn,
        IReadOnlyList<string>? nodeIds,
        int max,
        string now,
        long afterId = 0)
    {
        if (conn == null || nodeIds == null || nodeIds.Count == 0 || max <= 0) return (0, 0);

        // 轮转窗口：只取 ID > afterId 的一批（ORDER BY ID LIMIT max）
        var ids = (await conn.QueryAsync<long>(
            "SELECT ID FROM sam_dispose_status WHERE send=1 AND disposed=0 AND ID > @AfterId ORDER BY ID LIMIT @Max",
            new { AfterId = afterId, Max = max })).AsList();
        if (ids.Count == 0) return (0, 0);          // 查完 → 游标归零，下轮从头
        var lastId = ids[ids.Count - 1];

        var barcodes = (await conn.QueryAsync<string>(
            "SELECT barcode FROM sam_dispose_status WHERE ID IN @Ids", new { Ids = ids })).AsList();
        if (barcodes.Count == 0) return (0, lastId);

        var hits = new List<string>();
        foreach (var nodeId in nodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) continue;
            var loc = $"&3-{nodeId}-%";
            var found = (await conn.QueryAsync<string>(
                "SELECT sample_id FROM t_sample WHERE t_location LIKE @Loc AND sample_id IN @Barcodes",
                new { Loc = loc, Barcodes = barcodes })).AsList();
            foreach (var b in found)
                if (!hits.Contains(b)) hits.Add(b);
        }
        if (hits.Count == 0) return (0, lastId);

        var affected = await conn.ExecuteAsync(
            "UPDATE sam_dispose_status SET disposed=1, disposed_time=@Now WHERE barcode IN @Hits AND send=1 AND disposed=0",
            new { Hits = hits, Now = now });
        return (affected, lastId);
    }

    // ===== 队列查询 / 重发 / 失败处置 =====
    // 统一查询列（显式别名 → Dapper 属性映射；send_time / disposed_time 是 yyyyMMddHHmmss 字符串）
    private const string PendingColumns =
        "barcode AS Barcode, patient AS Patient, stype AS Stype, location AS Location, " +
        "send AS Send, disposed AS Disposed, checkcount AS Checkcount, resend_count AS ResendCount, " +
        "send_time AS SendTime, disposed_time AS DisposedTime";

    public async Task<int> CountPendingSendAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await CountPendingSendCoreAsync(conn);
    }

    public async Task<int> CountPendingAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await CountPendingCoreAsync(conn);
    }

    public async Task<int> CountPendingConfirmAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await CountPendingConfirmCoreAsync(conn);
    }

    public async Task<int> CountFailedAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await CountFailedCoreAsync(conn);
    }

    public async Task<List<DisposeStatus>> GetPendingRecordsAsync(int minResendCount, int limit)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await GetPendingRecordsCoreAsync(conn, minResendCount, limit);
    }

    public async Task<List<DisposeStatus>> GetResendCandidatesAsync(string cutoff, int maxResendCount, int limit)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await GetResendCandidatesCoreAsync(conn, cutoff, maxResendCount, limit);
    }

    public async Task<List<DisposeStatus>> GetExhaustedCandidatesAsync(int maxResendCount, int limit)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await GetExhaustedCandidatesCoreAsync(conn, maxResendCount, limit);
    }

    /// <summary>
    /// 重排队：send=1 &amp; disposed=0 → send=0（放回待发队列，由正常状态机按窗口一条条发送）。
    /// 不改 send_time、不增 resend_count（重发次数只在真正发送成功时 +1）。
    /// </summary>
    public async Task<int> RequeueForResendAsync(IReadOnlyCollection<string> barcodes)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await RequeueForResendCoreAsync(conn, barcodes);
    }

    public async Task<int> MarkFailedAsync(IReadOnlyCollection<string> barcodes)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await MarkFailedCoreAsync(conn, barcodes);
    }

    public async Task<int> ResetRecordsAsync(IReadOnlyCollection<string> barcodes)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await ResetRecordsCoreAsync(conn, barcodes);
    }

    public async Task<int> DeleteRecordsAsync(IReadOnlyCollection<string> barcodes)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return await DeleteRecordsCoreAsync(conn, barcodes);
    }

    public async Task<bool> RecheckRecordAsync(string barcode)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var ok = await RecheckRecordCoreAsync(conn, SrmNodeIds, barcode, DateTime.Now.ToString("yyyyMMddHHmmss"));
        if (ok) _logger.LogInformation("Dispose record {Barcode} confirmed disposed on recheck", barcode);
        return ok;
    }

    // ---- 以下 Core 方法抽出为 public static，便于用 SQLite 做单元测试（不依赖 MySQL 实例）----

    public static async Task<int> CountPendingSendCoreAsync(IDbConnection conn)
    {
        if (conn == null) return 0;
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sam_dispose_status WHERE send=0");
    }

    public static async Task<int> CountPendingCoreAsync(IDbConnection conn)
    {
        if (conn == null) return 0;
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sam_dispose_status WHERE send=0 OR (send=1 AND disposed=0)");
    }

    // 待确认：send=1 且 disposed=0（已投递但未观测到丢弃）
    public static async Task<int> CountPendingConfirmCoreAsync(IDbConnection conn)
    {
        if (conn == null) return 0;
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sam_dispose_status WHERE send=1 AND disposed=0");
    }

    // 失败：send=3 且 disposed=0（重发次数用尽，等待人工处置）
    public static async Task<int> CountFailedCoreAsync(IDbConnection conn)
    {
        if (conn == null) return 0;
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sam_dispose_status WHERE send=3 AND disposed=0");
    }

    public static async Task<List<DisposeStatus>> GetPendingRecordsCoreAsync(IDbConnection conn, int minResendCount, int limit)
    {
        if (conn == null || limit <= 0) return new List<DisposeStatus>();
        var sql = $"SELECT {PendingColumns} FROM sam_dispose_status " +
                  "WHERE send IN (1,3) AND disposed=0 AND resend_count >= @Min " +
                  "ORDER BY resend_count DESC, ID LIMIT @Limit";
        return (await conn.QueryAsync<DisposeStatus>(sql, new { Min = minResendCount, Limit = limit })).AsList();
    }

    public static async Task<List<DisposeStatus>> GetResendCandidatesCoreAsync(
        IDbConnection conn, string cutoff, int maxResendCount, int limit)
    {
        if (conn == null || limit <= 0) return new List<DisposeStatus>();
        var sql = $"SELECT {PendingColumns} FROM sam_dispose_status " +
                  "WHERE send=1 AND disposed=0 AND send_time IS NOT NULL AND send_time <= @Cutoff";
        if (maxResendCount > 0) sql += " AND resend_count < @Max";
        sql += " ORDER BY ID LIMIT @Limit";
        return (await conn.QueryAsync<DisposeStatus>(sql, new { Cutoff = cutoff, Max = maxResendCount, Limit = limit })).AsList();
    }

    public static async Task<List<DisposeStatus>> GetExhaustedCandidatesCoreAsync(
        IDbConnection conn, int maxResendCount, int limit)
    {
        // maxResendCount<=0 表示不限重发次数 → 没有"用尽"的概念
        if (conn == null || maxResendCount <= 0 || limit <= 0) return new List<DisposeStatus>();
        var sql = $"SELECT {PendingColumns} FROM sam_dispose_status " +
                  "WHERE send=1 AND disposed=0 AND resend_count >= @Max ORDER BY ID LIMIT @Limit";
        return (await conn.QueryAsync<DisposeStatus>(sql, new { Max = maxResendCount, Limit = limit })).AsList();
    }

    // 重排队：send=1 & disposed=0 → send=0（放回待发队列）
    //   不改 send_time（保留原投递时间）、不增 resend_count（重发次数只在真正发送成功时 +1）
    public static async Task<int> RequeueForResendCoreAsync(IDbConnection conn, IReadOnlyCollection<string> barcodes)
    {
        if (conn == null || barcodes == null || barcodes.Count == 0) return 0;
        var list = barcodes.Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
        if (list.Count == 0) return 0;
        return await conn.ExecuteAsync(
            "UPDATE sam_dispose_status SET send=0 WHERE barcode IN @Barcodes AND send=1 AND disposed=0",
            new { Barcodes = list });
    }

    public static async Task<int> MarkFailedCoreAsync(IDbConnection conn, IReadOnlyCollection<string> barcodes)
    {
        if (conn == null || barcodes == null || barcodes.Count == 0) return 0;
        var list = barcodes.Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
        if (list.Count == 0) return 0;
        return await conn.ExecuteAsync(
            "UPDATE sam_dispose_status SET send=3 WHERE barcode IN @Barcodes AND send=1 AND disposed=0",
            new { Barcodes = list });
    }

    public static async Task<int> ResetRecordsCoreAsync(IDbConnection conn, IReadOnlyCollection<string> barcodes)
    {
        if (conn == null || barcodes == null || barcodes.Count == 0) return 0;
        var list = barcodes.Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
        if (list.Count == 0) return 0;
        return await conn.ExecuteAsync(
            "UPDATE sam_dispose_status SET send=0, resend_count=0, checkcount=0, send_time=NULL " +
            "WHERE barcode IN @Barcodes AND send IN (1,3)",
            new { Barcodes = list });
    }

    public static async Task<int> DeleteRecordsCoreAsync(IDbConnection conn, IReadOnlyCollection<string> barcodes)
    {
        if (conn == null || barcodes == null || barcodes.Count == 0) return 0;
        var list = barcodes.Where(b => !string.IsNullOrEmpty(b)).Distinct().ToList();
        if (list.Count == 0) return 0;
        return await conn.ExecuteAsync(
            "DELETE FROM sam_dispose_status WHERE barcode IN @Barcodes",
            new { Barcodes = list });
    }

    // 复查单条：逐节点查 t_sample 位置 &3-{node}-%；命中则置 disposed=1（等价 UpdateStatusAsync(barcode,"disposed")，复用同一连接）
    public static async Task<bool> RecheckRecordCoreAsync(
        IDbConnection conn, IReadOnlyList<string>? nodeIds, string barcode, string now)
    {
        if (conn == null || string.IsNullOrEmpty(barcode) || nodeIds == null || nodeIds.Count == 0) return false;
        foreach (var nodeId in nodeIds)
        {
            if (string.IsNullOrWhiteSpace(nodeId)) continue;
            var loc = $"&3-{nodeId}-%";
            var found = await conn.QueryFirstOrDefaultAsync<string>(
                "SELECT sample_id FROM t_sample WHERE sample_id=@B AND t_location LIKE @Loc",
                new { B = barcode, Loc = loc });
            if (found == null) continue;
            await conn.ExecuteAsync(
                "UPDATE sam_dispose_status SET disposed=1, disposed_time=@Now WHERE barcode=@B",
                new { B = barcode, Now = now });
            return true;
        }
        return false;
    }

    public async Task DeleteUnsendAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var sql = "DELETE FROM sam_dispose_status WHERE send=0";
        try
        {
            var barcodes = (await conn.QueryAsync<string>("SELECT barcode FROM sam_dispose_status WHERE send=0")).AsList();
            var affected = await conn.ExecuteAsync(sql);
            _logger.LogInformation("Deleted unsend records: Count={Count}, Barcodes=[{Barcodes}]",
                affected, string.Join(",", barcodes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete unsend records failed: SQL={Sql}", sql);
        }
    }

    public async Task DeleteHistoryAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var sql = DeleteHistorySql;
        try
        {
            var barcodes = (await conn.QueryAsync<string>(
                "SELECT barcode FROM sam_dispose_status WHERE send>=1 AND TIMESTAMPDIFF(DAY,send_time,NOW())>10")).AsList();
            var affected = await conn.ExecuteAsync(sql);
            if (affected > 0)
                _logger.LogInformation("Deleted history records: Count={Count}, Barcodes=[{Barcodes}]",
                    affected, string.Join(",", barcodes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete history records failed: SQL={Sql}", sql);
        }
    }

    public async Task<List<SampleRecord>> GetAllScanableSamplesAsync(int maxCount, IReadOnlyCollection<string>? candidateTestNames = null)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();

        // t_sample 的 test 分列存储：test_1..test_80，每列格式 "状态;序号;类型;子类型;测试名;..."
        // 用 CONCAT_WS 拼接所有 test 列（^ 分隔，与列内 ; 区分），由内存侧按 ^ 拆列逐列匹配
        var testCols = string.Join(",", Enumerable.Range(1, 80).Select(i => $"test_{i}"));
        var sql = $@"SELECT sample_id AS Barcode, CONCAT_WS('^', {testCols}) AS TestName FROM t_sample
            WHERE t_status='C' AND t_location LIKE '&1-%'";
        var p = new DynamicParameters();

        var names = candidateTestNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names is { Count: > 0 })
        {
            var clauses = new List<string>();
            for (var i = 0; i < names.Count; i++)
            {
                var pn = $"t{i}";
                p.Add(pn, $"%{names[i]}%");
                clauses.Add($"CONCAT_WS('^', {testCols}) LIKE @{pn}");
            }
            sql += " AND (" + string.Join(" OR ", clauses) + ")";
        }
        // 初筛后匹配样本有限，不设 LIMIT，避免截断漏样本；
        // 单轮处理上限由 Router 分发处控制（MaxOnetimeScanCount）
        return (await conn.QueryAsync<SampleRecord>(sql, p)).AsList();
    }

    /// <summary>
    /// 由 <c>t_location</c> 推出**架键**（与 <see cref="DisposeRackKeyExpression"/> 的 SQL 侧口径一致）。
    /// <list type="bullet">
    ///   <item><c>&amp;1-{节点}-{架号}-{层}-{槽}-{位}</c>（6 段） → <c>&amp;1-{节点}-{架号}</c>（如 <c>&amp;1-13-000078-05-14-01</c> → <c>&amp;1-13-000078</c>）</item>
    ///   <item><c>&amp;2-{节点}-000000-00-{槽}-{位}</c>（6 段） → <c>&amp;2-{节点}-{槽}</c>（架/槽在第 5 段，如 <c>&amp;2-01-000000-00-18-01</c> → <c>&amp;2-01-18</c>）</item>
    ///   <item><c>&amp;3-*</c>（已丢弃/归档哨兵）、<c>&amp;0-*</c>、NULL、其它前缀 → <c>""</c>（排除）</item>
    ///   <item><b>段数不足 6 段的短格式</b>（如 <c>&amp;1-13-000078</c>、<c>&amp;2-01-000000-00</c>） → <c>""</c>（排除）</item>
    /// </list>
    /// <para>
    /// 6 段守卫（<c>parts.Length &lt; 6</c>）与 SQL 侧的 <c>LIKE '&amp;1-%-%-%-%-%'</c> <b>同口径</b>：
    /// 短格式在 SQL 侧归 <c>'~other'</c>（保留、排最后），在 C# 侧返回 <c>""</c>，
    /// 两侧都**不会产生错误的架键**，也都**不丢弃**该样本（可接受）。
    /// </para>
    /// </summary>
    public static string GetLocationRackNo(string? loc)
    {
        if (string.IsNullOrEmpty(loc)) return "";
        var parts = loc.Split('-');
        // 必须 6 段（&1-{节点}-{架号}-{层}-{槽}-{位} / &2-{节点}-000000-00-{槽}-{位}）
        // —— 与 SQL 侧 LIKE '&1-%-%-%-%-%' 的 5 个 '-' 对齐，避免短格式被截成错误架键。
        if (parts.Length < 6) return "";
        if (parts[0] == "&1") return $"&1-{parts[1]}-{parts[2]}";
        if (parts[0] == "&2") return $"&2-{parts[1]}-{parts[4]}";
        return "";   // &3 / &0 / 其它
    }

    /// <summary>
    /// 视图/存储过程名的**白名单**校验（唯一入口）：只允许 <c>[A-Za-z0-9_]</c>，
    /// 并允许**可选的一个 schema 前缀**（<c>schema.name</c>）。
    /// <para>
    /// 历史缺陷（M-05）：旧实现用正则<em>剔除</em>非法字符且允许反引号
    /// （<c>[^a-zA-Z0-9_`]</c>）→ 反引号可被拼进 SQL；且"剔除"语义会把
    /// <c>view over</c> 静默变成 <c>viewover</c>（查到另一个视图，排查不到）。
    /// 现在：**整名不匹配即拒绝**（返回 null），不做任何字符级修补。
    /// </para>
    /// <para>
    /// 复核 D4：现场 <c>CommandName</c> 可能带 schema 前缀（如 <c>flexlab.view_x</c>），
    /// 原规则 <c>^[A-Za-z0-9_]+$</c> 会把它整个拒掉 → 丢弃功能停摆。
    /// 现放宽为 <c>^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)?$</c>：
    /// 允许 <c>view_x</c>、<c>flexlab.view_x</c>；仍拒绝 <c>a.b.c</c>、反引号、分号、空格、连字符、非 ASCII。
    /// </para>
    /// <para>空名 → 回退默认视图（保留既有默认行为）；非空但含非法字符 → null（拒绝）。</para>
    /// </summary>
    /// <returns>合法的名称；或 null（名称非法，调用方必须拒绝执行）。</returns>
    public static string? SanitizeSqlName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return DefaultDisposeViewName;
        return SqlNamePattern.IsMatch(name) ? name : null;
    }

    /// <summary>默认丢弃视图名（配置为空时使用）。</summary>
    public const string DefaultDisposeViewName = "view_overtimestoragesample";

    // 只允许字母、数字、下划线；可选**一个** schema 前缀（"schema.name"）。
    // 反引号/分号/空格/连字符/多点("a.b.c")/非 ASCII 一律拒绝。
    private static readonly System.Text.RegularExpressions.Regex SqlNamePattern =
        new("^[A-Za-z0-9_]+(\\.[A-Za-z0-9_]+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
}
