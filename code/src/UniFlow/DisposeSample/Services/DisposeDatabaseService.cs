using System.Data;
using Dapper;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace UniFlow.DisposeSample.Services;

public class DisposeDatabaseService : IDisposeDatabaseService
{
    private readonly ILogger<DisposeDatabaseService> _logger;
    private readonly MySqlConnectionFactory _factory;

    public DisposeDatabaseService(ILogger<DisposeDatabaseService> logger, MySqlConnectionFactory factory)
    {
        _logger = logger;
        _factory = factory;
    }

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

    // sam_dispose_status 表不存在时自动创建（避免启动报错）
    public async Task EnsureSamDisposeTableAsync()
    {
        var sql = """
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
                PRIMARY KEY (ID),
                INDEX idx_barcode (barcode),
                INDEX idx_send (send)
            )
            """;
        try
        {
            using var conn = NewConnection();
            await conn.OpenAsync();
            await conn.ExecuteAsync(sql);
            _logger.LogInformation("sam_dispose_status table ensured");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ensure sam_dispose_status table failed");
            throw;
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

    public async Task<List<SampleRecord>> GetDisposeSamplesAsync(int cmdType, string cmdName, int maxCount)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();

        var safeName = SanitizeSqlName(cmdName);
        List<SampleRecord> samples;
        if (cmdType == 0)
            samples = (await conn.QueryAsync<SampleRecord>(
                $"SELECT * FROM {safeName} LIMIT @Max", new { Max = maxCount })).AsList();
        else
            samples = (await conn.QueryAsync<SampleRecord>(
                safeName, commandType: CommandType.StoredProcedure)).AsList();

        _logger.LogInformation("GetDisposeSamples returned {Count}", samples.Count);
        return samples;
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

    public async Task UpdateStatusAsync(string barcode, string field)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var now = DateTime.Now.ToString("yyyyMMddHHmmss");
        var setClause = field switch
        {
            "send" => "send=1, send_time=@Now",
            "disposed" => "disposed=1, disposed_time=@Now",
            "checkcount" => "checkcount=checkcount+1, check_time=@Now",
            _ => throw new ArgumentException($"Unknown field: {field}")
        };
        var sql = $"UPDATE sam_dispose_status SET {setClause} WHERE barcode=@B";
        try
        {
            await conn.ExecuteAsync(sql, new { B = barcode, Now = now });
            _logger.LogInformation("Update dispose record: Barcode={Barcode}, Field={Field}", barcode, field);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update dispose record failed: Barcode={Barcode}, Field={Field}, SQL={Sql}", barcode, field, sql);
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
        var sql = "DELETE FROM sam_dispose_status WHERE send=1 AND TIMESTAMPDIFF(DAY,send_time,NOW())>10";
        try
        {
            var barcodes = (await conn.QueryAsync<string>(
                "SELECT barcode FROM sam_dispose_status WHERE send=1 AND TIMESTAMPDIFF(DAY,send_time,NOW())>10")).AsList();
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

    private static string GetLocationRackNo(string? loc)
    {
        if (string.IsNullOrEmpty(loc)) return "";
        var parts = loc.Split('-');
        return parts.Length > 2 ? parts[2].Trim() : "";
    }

    private static string SanitizeSqlName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "view_overtimestoragesample";
        // Only allow alphanumeric, underscore, and backtick characters
        var sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_`]", "");
        return string.IsNullOrEmpty(sanitized) ? "view_overtimestoragesample" : sanitized;
    }
}
