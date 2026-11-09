using Microsoft.Extensions.Configuration;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// "样本丢弃按架排序"（DisposeByRack，默认开启）的口径断言。
/// <para>
/// 业务目标：候选按"架"排序 —— 合格样本最多、最老的架优先，
/// 使发送窗口（<c>MaxOnetimeSelectDiscardCount</c>，默认 10）内的 N 条**尽量来自同一架**。
/// </para>
/// <para>
/// 架键规则（C# 与 SQL 两侧同口径）：
/// <list type="bullet">
///   <item><c>&amp;1-{节点}-{架号}-{层}-{槽}-{位}</c> → <c>&amp;1-{节点}-{架号}</c></item>
///   <item><c>&amp;2-{节点}-000000-00-{槽}-{位}</c> → <c>&amp;2-{节点}-{槽}</c>（架/槽在第 5 段）</item>
///   <item><c>&amp;3-*</c>（已丢弃哨兵） → <b>排除</b>（SQL 侧 NULL，不返回）</item>
///   <item><c>&amp;0-*</c> / NULL / 其它前缀 / <b>短格式（不足 6 段）</b> → 归 <c>~other</c> 组
///         （<b>保留、排最后，不静默丢弃</b>；C# 侧 <c>GetLocationRackNo</c> 对这些返回 <c>""</c>）</item>
/// </list>
/// </para>
/// <para>
/// 本开关**只改候选顺序**，不改窗口 / 发送 / 重排队逻辑；
/// <c>CommandType=1</c>（存储过程）时排序由数据库侧决定，本开关不生效。
/// </para>
/// </summary>
public class DisposeDatabaseServiceRackTests
{
    // ===== 1. BuildDisposeSamplesSql(byRack: true) =====

    [Fact]
    public void BuildDisposeSamplesSql_ByRack_ContainsRackKeyAndOrdering()
    {
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_overtimestoragesample", true);

        // 架键列（内外两层都用到）
        sql.ShouldContain("uni_rack_key");
        // 排序：其它组（~other）压最后 → 合格数降序 → 架级最早 update_time 升序 → location 升序（稳定）
        // 注：r.oldest 是"架级"常量 → 只决定架与架之间的先后；同架内由 v.location 升序决定。
        sql.ShouldContain("ORDER BY CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC, r.cnt DESC, r.oldest ASC, v.location ASC");
        // 窗口条数仍由调用方传入（不多取、不少取）
        sql.ShouldContain("LIMIT @Max");
        // 排除 NULL 架键（现在只有 &3 哨兵会是 NULL；&0/NULL/短格式 → '~other'，不被丢弃）
        sql.ShouldContain("WHERE v.uni_rack_key IS NOT NULL");
    }

    [Fact]
    public void BuildDisposeSamplesSql_ByRack_HasAmp1AndAmp2CaseBranches()
    {
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_overtimestoragesample", true);

        // &1 架键：节点(第2段) + 架号(第3段)；LIKE 里的 5 个 '-' = 6 段（与 C# 侧 parts.Length < 6 守卫同口径）
        sql.ShouldContain("WHEN location LIKE '&1-%-%-%-%-%' THEN CONCAT('&1-'");
        // &2 架键：节点(第2段) + 槽(第5段)；同样要求 6 段
        sql.ShouldContain("WHEN location LIKE '&2-%-%-%-%-%' THEN CONCAT('&2-'");
        // &3（已丢弃哨兵）→ NULL（排除，不返回）
        sql.ShouldContain("WHEN location LIKE '&3-%' THEN NULL");
        // 其余（&0 / NULL / 短格式 / 其它前缀）→ '~other'（保留、排最后，**不静默丢弃**）
        sql.ShouldContain("ELSE '~other'");
        // '~other' 组必须排在最后（不因 cnt 大而挤掉真正的架）
        sql.ShouldContain("CASE WHEN r.uni_rack_key = '~other' THEN 1 ELSE 0 END ASC");
    }

    [Fact]
    public void BuildDisposeSamplesSql_ByRack_UsesSanitizedNameWithoutParentheses()
    {
        // safeName 已过白名单；schema.view 也可用 → FROM 后面直接跟名字（不加括号）
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("flexlab.view_x", true);
        sql.ShouldContain("FROM flexlab.view_x z");
        sql.ShouldNotContain("(flexlab.view_x)");
    }

    [Fact]
    public void BuildDisposeSamplesSql_ByRack_ReferencesViewTwiceForJoin()
    {
        // 外层取样本 + 内层按架聚合 → 视图被引用两次（一次出样本列，一次出 update_time 聚合）
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_v", true);
        var occurrences = sql.Split("FROM view_v z").Length - 1;
        occurrences.ShouldBe(2);
    }

    // ===== 2. BuildDisposeSamplesSql(byRack: false) = 原顺序（可随时回退）=====

    [Fact]
    public void BuildDisposeSamplesSql_NotByRack_IsPlainSelect()
    {
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_overtimestoragesample", false);
        sql.ShouldBe("SELECT * FROM view_overtimestoragesample LIMIT @Max");
    }

    [Fact]
    public void BuildDisposeSamplesSql_NotByRack_HasNoRackKey()
    {
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_overtimestoragesample", false);
        sql.ShouldNotContain("uni_rack_key");
        sql.ShouldNotContain("ORDER BY");
    }

    [Fact]
    public void BuildDisposeSamplesSql_NotByRack_SchemaPrefixedName()
    {
        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("flexlab.view_x", false);
        sql.ShouldBe("SELECT * FROM flexlab.view_x LIMIT @Max");
    }

    // ===== 3. GetLocationRackNo：架键口径 =====

    [Theory]
    // &1：架键 = 节点 + 架号（第 3 段）
    [InlineData("&1-13-000078-05-14-01", "&1-13-000078")]
    [InlineData("&1-13-000088-06-08-01", "&1-13-000088")]
    [InlineData("&1-09-000001-01-01-01", "&1-09-000001")]
    // &2：架键 = 节点 + 槽（第 5 段）
    [InlineData("&2-01-000000-00-18-01", "&2-01-18")]
    [InlineData("&2-13-000000-00-19-02", "&2-13-19")]
    [InlineData("&2-01-000000-00-20-01", "&2-01-20")]
    public void GetLocationRackNo_ValidLocations_ReturnRackKey(string loc, string expected)
    {
        DisposeDatabaseService.GetLocationRackNo(loc).ShouldBe(expected);
    }

    [Theory]
    // &3 = 已丢弃/归档哨兵 → 排除
    [InlineData("&3-13-000000-00-00-00")]
    // &0 = 特殊/未定位 → 排除
    [InlineData("&0-00-000000-00-00-00")]
    // 其它前缀 / 非规范格式 → 排除
    [InlineData("abc")]
    [InlineData("")]
    // 短格式（段数不足 6 段）→ 排除（SQL 侧归 '~other' 保留，两侧都不丢弃，只是不产生错误架键）
    [InlineData("&1-13")]
    [InlineData("&1-13-000078")]          // 3 段
    [InlineData("&2-01-000000-00")]       // 4 段
    public void GetLocationRackNo_ExcludedLocations_ReturnEmpty(string loc)
    {
        DisposeDatabaseService.GetLocationRackNo(loc).ShouldBe("");
    }

    [Fact]
    public void GetLocationRackNo_Null_ReturnsEmpty()
    {
        DisposeDatabaseService.GetLocationRackNo(null).ShouldBe("");
    }

    [Fact]
    public void GetLocationRackNo_SameRack_SameKey()
    {
        // 同一架的不同槽位 → 同一架键（这是"按架排序"能生效的前提）
        var a = DisposeDatabaseService.GetLocationRackNo("&1-13-000078-05-14-01");
        var b = DisposeDatabaseService.GetLocationRackNo("&1-13-000078-05-14-02");
        a.ShouldBe(b);
        a.ShouldNotBe(DisposeDatabaseService.GetLocationRackNo("&1-13-000079-05-14-01"));
    }

    [Fact]
    public void GetLocationRackNo_LegacyRackNoImplementation_WouldReturnWrongAmp2Key()
    {
        // 历史实现：parts.Length > 2 ? parts[2] : ""（即固定取第 3 段）
        // → &2 的第 3 段恒为 000000（不是架/槽），按架排序会退化成"整个节点算一架"
        var legacy = "&2-01-000000-00-18-01".Split('-');
        var legacyKey = legacy.Length > 2 ? legacy[2].Trim() : "";
        legacyKey.ShouldBe("000000", "旧实现对 &2 取到的是占位段 000000（历史缺陷对照）");

        // 新实现取第 5 段（真正的架/槽）
        DisposeDatabaseService.GetLocationRackNo("&2-01-000000-00-18-01").ShouldBe("&2-01-18");
    }

    [Fact]
    public void GetLocationRackNo_KeyLengthFitsRackColumn()
    {
        // sam_dispose_status.rack 是 VARCHAR(34) → 架键长度必须放得下
        foreach (var loc in new[] { "&1-13-000078-05-14-01", "&2-01-000000-00-18-01" })
        {
            var key = DisposeDatabaseService.GetLocationRackNo(loc);
            key.Length.ShouldBeLessThanOrEqualTo(34);
            key.Length.ShouldBeGreaterThan(0);
        }
    }

    // ===== 4. 配置：默认开启 + 热生效 =====

    [Fact]
    public void DisposeByRack_Default_IsTrue()
    {
        new AptioDisposeSampleConfig().DisposeByRack.ShouldBeTrue();
        new AptioConfig().DisposeSample.DisposeByRack.ShouldBeTrue();
    }

    [Fact]
    public void DisposeByRack_ConfigPath_IsHotAndBool()
    {
        // 页面能保存该开关：路径可解析 + 热生效（无需重启）+ 值类型为 bool
        var (level, known, kind) = RestartLevelResolver.ResolveWithKind("Aptio:DisposeSample:DisposeByRack");
        known.ShouldBeTrue();
        level.ShouldBe(RestartLevel.Hot);
        kind.ShouldBe("bool");

        var (normalized, error) = RestartLevelResolver.ValidateValue(kind, System.Text.Json.JsonDocument.Parse("true").RootElement);
        error.ShouldBeNull();
        normalized.ShouldBe(true);

        var (normalized2, error2) = RestartLevelResolver.ValidateValue(kind, System.Text.Json.JsonDocument.Parse("false").RootElement);
        error2.ShouldBeNull();
        normalized2.ShouldBe(false);
    }

    [Fact]
    public void DisposeByRack_DoesNotChangeWindowSize()
    {
        // 本开关只改候选顺序 → 窗口仍是 MaxOnetimeSelectDiscardCount（默认 10）
        var dc = new AptioDisposeSampleConfig();
        dc.MaxOnetimeSelectDiscardCount.ShouldBe(10);
        dc.DisposeByRack = false;
        dc.MaxOnetimeSelectDiscardCount.ShouldBe(10);
    }

    [Fact]
    public void DisposeByRack_Binding_KeepsTrueWhenKeyAbsentFromConfigFile()
    {
        // 现场 appsettings.json 里还没有这个键 → 绑定后必须仍是代码默认值 true
        // （若绑定把它冲成 false，页面显示"关"而后台按"开"跑 → 口径不一致）
        var data = new Dictionary<string, string?>
        {
            ["Aptio:Ip"] = "10.0.0.1",
            ["Aptio:Port"] = "2055",
            ["Aptio:DisposeSample:CommandType"] = "0",
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        var bound = global::Program.BindAptioConfig(config);
        bound.DisposeSample.DisposeByRack.ShouldBeTrue();
        bound.DisposeSample.MaxOnetimeSelectDiscardCount.ShouldBe(10);
    }

    [Fact]
    public void DisposeByRack_Binding_RespectsExplicitFalseInConfigFile()
    {
        // 现场显式写 false → 绑定后必须是 false（回退开关生效）
        var data = new Dictionary<string, string?>
        {
            ["Aptio:DisposeSample:DisposeByRack"] = "false",
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        var bound = global::Program.BindAptioConfig(config);
        bound.DisposeSample.DisposeByRack.ShouldBeFalse();
    }

    // ===== 5.（可选）本机 MySQL 集成验证 =====

    /// <summary>
    /// 用本机 MySQL 的测试库 <c>uniflow_loc_analysis</c> 跑真实 SQL，断言：
    /// <list type="number">
    ///   <item>真实候选视图上，返回的 10 条候选**架键相同**（按架排序生效）；</item>
    ///   <item>合成视图上，<c>&amp;0</c> / NULL / 短格式的样本**不被静默丢弃**（归 <c>~other</c> 组）
    ///         且**排在最后**，<c>&amp;3</c> 哨兵仍被排除。</item>
    /// </list>
    /// <para>
    /// 连接参数全部来自环境变量（<c>UNIFLOW_TEST_MYSQL_*</c> / <c>MYSQL_PWD</c>），
    /// 连不上则整条用例**跳过**（<b>绝不连生产库</b>；只执行 SELECT + 建/删临时视图）。
    /// </para>
    /// </summary>
    [MySqlTestFactAttribute]
    public async Task BuildDisposeSamplesSql_OnRealMySql_OrdersByRack_AndKeepsOtherLocations()
    {
        await AssertRealViewOrdersByRackAsync();
        await AssertOtherLocationsKeptAndSortedLastAsync();
    }

    /// <summary>真实候选视图：窗口条数不变 + 同架键（按架排序生效）。</summary>
    private static async Task AssertRealViewOrdersByRackAsync()
    {
        var connString = MySqlTestFactAttribute.BuildConnectionString()!;

        await using var conn = new MySqlConnector.MySqlConnection(connString);
        await conn.OpenAsync();

        var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_overtimestoragesample", true);
        var cmd = new MySqlConnector.MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Max", 10);
        await using var reader = await cmd.ExecuteReaderAsync();

        var locations = new List<string>();
        while (await reader.ReadAsync())
        {
            var loc = reader.IsDBNull(reader.GetOrdinal("location")) ? "" : reader.GetString(reader.GetOrdinal("location"));
            locations.Add(loc);
        }

        // 断言 1：窗口条数没变（最多 10 条）
        locations.Count.ShouldBeLessThanOrEqualTo(10);

        // 断言 2：所有候选的架键相同（按架排序生效）
        if (locations.Count > 1)
        {
            var keys = locations.Select(DisposeDatabaseService.GetLocationRackNo).Distinct().ToList();
            keys.ShouldHaveSingleItem();
            keys[0].ShouldNotBeNullOrWhiteSpace();
        }

        // 断言 3：全部落在可排序的 &1/&2 架上（没有 &3/&0/NULL）
        foreach (var loc in locations)
            DisposeDatabaseService.GetLocationRackNo(loc).ShouldNotBe("");
    }

    /// <summary>
    /// 合成视图验证「&amp;0 / NULL / 短格式**不静默丢弃**（归 <c>~other</c> 组、排最后），
    /// <c>&amp;3</c> 哨兵仍排除；同架内按 <c>location</c> 升序（<b>不是</b> update_time）。
    /// </summary>
    private static async Task AssertOtherLocationsKeptAndSortedLastAsync()
    {
        var connString = MySqlTestFactAttribute.BuildConnectionString()!;

        await using var conn = new MySqlConnector.MySqlConnection(connString);
        await conn.OpenAsync();

        // 合成数据：A1/A2=同一 &1 架（A2 的 update_time 更早）、B1=另一 &1 架、C1=&2 架、
        //             D1=&0、E1=NULL location、F1=&3 哨兵、G1/H1=两种短格式
        const string createSql = """
CREATE OR REPLACE VIEW view_rack_other_test AS
SELECT 'A1' AS barcode, '&1-13-000001-01-01-01' AS location, '20260101' AS update_time
UNION ALL SELECT 'A2', '&1-13-000001-01-01-02', '20251201'
UNION ALL SELECT 'B1', '&1-13-000002-01-01-01', '20260102'
UNION ALL SELECT 'C1', '&2-01-000000-00-18-01', '20260103'
UNION ALL SELECT 'D1', '&0-00-000000-00-00-00', '20260104'
UNION ALL SELECT 'E1', NULL, '20260105'
UNION ALL SELECT 'F1', '&3-13-000000-00-00-00', '20260106'
UNION ALL SELECT 'G1', '&1-13-000078', '20260107'
UNION ALL SELECT 'H1', '&2-01-000000-00', '20260108'
""";
        await using (var create = new MySqlConnector.MySqlCommand(createSql, conn))
            await create.ExecuteNonQueryAsync();

        try
        {
            var sql = DisposeDatabaseService.BuildDisposeSamplesSql("view_rack_other_test", true);
            var cmd = new MySqlConnector.MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Max", 100);
            await using var reader = await cmd.ExecuteReaderAsync();

            var barcodes = new List<string>();
            while (await reader.ReadAsync())
                barcodes.Add(reader.IsDBNull(reader.GetOrdinal("barcode")) ? "" : reader.GetString(reader.GetOrdinal("barcode")));

            // 断言 1：只有 &3 哨兵被排除（9 行 → 8 行），其余一行都没丢
            barcodes.Count.ShouldBe(8);
            barcodes.ShouldNotContain("F1");

            // 断言 2：架组在前（cnt DESC → 2 条的 &1-13-000001 最先；同架内 location 升序 → A1 在 A2 前，
            //          注意 A2 的 update_time 更早 —— 同架内**不是**按 update_time 排）
            barcodes.Take(4).ShouldBe(new List<string> { "A1", "A2", "B1", "C1" });
            barcodes.IndexOf("A1").ShouldBeLessThan(barcodes.IndexOf("A2"));

            // 断言 3：架与架之间才用 update_time（B1 的架更早 → 排在 C1 前面）
            barcodes.IndexOf("B1").ShouldBeLessThan(barcodes.IndexOf("C1"));

            // 断言 4：&0 / NULL / 短格式全部保留，且一律排在最后（~other 组）
            barcodes.Skip(4).OrderBy(x => x).ShouldBe(new List<string> { "D1", "E1", "G1", "H1" });
        }
        finally
        {
            await using var drop = new MySqlConnector.MySqlCommand("DROP VIEW IF EXISTS view_rack_other_test", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 覆盖「按架 SQL 失败 → 自动回退原查询」这条**唯一的保险**（复核 P2-2；此前零自动化覆盖）：
    /// 造一个**没有 update_time 列**的视图 → 按架 SQL（内层聚合用 update_time）必然失败
    /// → 必须自动回退到原查询，且**不抛异常**、仍返回满窗口 10 行。
    /// <para>
    /// 只在测试库建/删临时视图 <c>view_rack_fallback_test</c>（不建表、不改表数据、不碰生产库）；
    /// <c>finally</c> 里必定 DROP。
    /// </para>
    /// </summary>
    [MySqlTestFactAttribute]
    public async Task GetDisposeSamplesAsync_OnRealMySql_RackQueryFails_FallsBackToPlainQuery()
    {
        var parts = MySqlTestFactAttribute.GetParts()!.Value;
        var (host, port, db, user, pwd) = parts;
        var connString = MySqlTestFactAttribute.BuildConnectionString()!;

        await using var conn = new MySqlConnector.MySqlConnection(connString);
        await conn.OpenAsync();

        // 关键：这个视图**没有 update_time 列** → 按架 SQL 必然报错 → 触发回退分支
        const string createSql = """
CREATE OR REPLACE VIEW view_rack_fallback_test AS
SELECT sample_id AS barcode, patient, s_type AS stype, t_location AS location, res_1, res_2
FROM t_sample LIMIT 50
""";
        await using (var create = new MySqlConnector.MySqlCommand(createSql, conn))
            await create.ExecuteNonQueryAsync();

        try
        {
            var factory = new UniFlow.Common.Services.MySqlConnectionFactory(host, port, db, user, pwd);
            var cfg = new AptioConfig();          // DisposeByRack 默认 true → 走按架分支 → 必然失败 → 必须回退
            var monitor = new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(cfg);
            var svc = new DisposeDatabaseService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DisposeDatabaseService>.Instance,
                factory, monitor);

            // 不抛异常（回退成功）且仍取满窗口
            var samples = await svc.GetDisposeSamplesAsync(0, "view_rack_fallback_test", 10);
            samples.Count.ShouldBe(10);
            foreach (var s in samples) s.Barcode.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            await using var drop = new MySqlConnector.MySqlCommand("DROP VIEW IF EXISTS view_rack_fallback_test", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// 端到端跑一次**真实服务**（<c>DisposeDatabaseService.GetDisposeSamplesAsync</c>）：
    /// 验证「按架 SQL + Dapper 映射（多出的 <c>uni_rack_key</c> 列不能把映射打挂）+ 配置开关生效」整条链路。
    /// <para>只读（SELECT），连的是测试库，不碰生产库。</para>
    /// </summary>
    [MySqlTestFactAttribute]
    public async Task GetDisposeSamplesAsync_OnRealMySql_ByRack_ReturnsSingleRackSamples()
    {
        var parts = MySqlTestFactAttribute.GetParts()!.Value;
        var (host, port, db, user, pwd) = parts;

        var factory = new UniFlow.Common.Services.MySqlConnectionFactory(host, port, db, user, pwd);
        var monitor = new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(new AptioConfig());
        var svc = new DisposeDatabaseService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DisposeDatabaseService>.Instance,
            factory, monitor);

        // DisposeByRack 默认 true（配置里没写 → 走按架分支）
        var samples = await svc.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10);

        samples.Count.ShouldBeLessThanOrEqualTo(10);
        if (samples.Count > 1)
        {
            var keys = samples.Select(s => DisposeDatabaseService.GetLocationRackNo(s.Location)).Distinct().ToList();
            keys.ShouldHaveSingleItem();
        }

        // 断言：Dapper 映射没被多出的 uni_rack_key 列打挂（关键列必须都有值）
        foreach (var s in samples)
        {
            s.Barcode.ShouldNotBeNullOrWhiteSpace();
            s.Location.ShouldNotBeNullOrWhiteSpace();
        }
    }

    /// <summary>
    /// 端到端验证**回退开关**：<c>DisposeByRack=false</c> → 走原顺序查询（仍返回 10 条，不报错）。
    /// </summary>
    [MySqlTestFactAttribute]
    public async Task GetDisposeSamplesAsync_OnRealMySql_NotByRack_ReturnsPlainOrder()
    {
        var parts = MySqlTestFactAttribute.GetParts()!.Value;
        var (host, port, db, user, pwd) = parts;

        var factory = new UniFlow.Common.Services.MySqlConnectionFactory(host, port, db, user, pwd);
        var cfg = new AptioConfig();
        cfg.DisposeSample.DisposeByRack = false;
        var monitor = new UniFlow.Tests.Common.TestOptionsMonitor<AptioConfig>(cfg);
        var svc = new DisposeDatabaseService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DisposeDatabaseService>.Instance,
            factory, monitor);

        var samples = await svc.GetDisposeSamplesAsync(0, "view_overtimestoragesample", 10);
        samples.Count.ShouldBeLessThanOrEqualTo(10);
        samples.Count.ShouldBeGreaterThan(0);
    }
}

/// <summary>
/// 需要本机 MySQL 测试库的用例标记：环境变量未配置或连不上时**跳过**（而不是失败）。
/// <para>
/// 环境变量：<c>UNIFLOW_TEST_MYSQL_HOST</c> / <c>UNIFLOW_TEST_MYSQL_PORT</c> /
/// <c>UNIFLOW_TEST_MYSQL_DB</c> / <c>UNIFLOW_TEST_MYSQL_USER</c> /
/// <c>UNIFLOW_TEST_MYSQL_PWD</c>（或 <c>MYSQL_PWD</c>）。
/// </para>
/// </summary>
public sealed class MySqlTestFactAttribute : FactAttribute
{
    public MySqlTestFactAttribute()
    {
        var connString = BuildConnectionString();
        if (connString == null)
        {
            Skip = "未配置 UNIFLOW_TEST_MYSQL_HOST / UNIFLOW_TEST_MYSQL_DB / MYSQL_PWD —— 跳过 MySQL 集成断言";
            return;
        }
        try
        {
            using var conn = new MySqlConnector.MySqlConnection(connString);
            conn.Open();
        }
        catch (Exception ex)
        {
            Skip = $"本机 MySQL 测试库不可用（{ex.GetType().Name}: {ex.Message}）—— 跳过集成断言";
        }
    }

    public static string? BuildConnectionString()
    {
        var parts = GetParts();
        if (parts == null) return null;
        var (host, port, db, user, pwd) = parts.Value;
        return $"Server={host};Port={port};Database={db};User ID={user};Password={pwd};Allow User Variables=true;";
    }

    /// <summary>解析测试库连接参数（host/port/db/user/pwd）；缺参数 → null。</summary>
    public static (string host, int port, string db, string user, string pwd)? GetParts()
    {
        var host = Environment.GetEnvironmentVariable("UNIFLOW_TEST_MYSQL_HOST");
        var db = Environment.GetEnvironmentVariable("UNIFLOW_TEST_MYSQL_DB");
        var pwd = Environment.GetEnvironmentVariable("UNIFLOW_TEST_MYSQL_PWD")
                  ?? Environment.GetEnvironmentVariable("MYSQL_PWD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(pwd))
            return null;

        var portRaw = Environment.GetEnvironmentVariable("UNIFLOW_TEST_MYSQL_PORT") ?? "3306";
        if (!int.TryParse(portRaw, out var port)) port = 3306;
        var user = Environment.GetEnvironmentVariable("UNIFLOW_TEST_MYSQL_USER") ?? "root";
        return (host, port, db, user, pwd);
    }
}
