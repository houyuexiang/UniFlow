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

    public async Task<int> CheckSrmSampleCountAsync(string nodeId)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var location = $"&1-{nodeId}-%";
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM t_sample WHERE t_status='C' AND t_location LIKE @Loc",
            new { Loc = location });
        _logger.LogInformation("SRM sample count: {Count}", count);
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
            await conn.ExecuteAsync("""
                INSERT INTO sam_dispose_status
                    (barcode,patient,stype,location,update_time,res_1,res_2,rack,send,disposed,checkcount)
                VALUES (@Barcode,@Patient,@Stype,@Location,@UpdateTime,@Res1,@Res2,@Rack,0,0,0)
                """, new { s.Barcode, s.Patient, s.Stype, s.Location, s.UpdateTime, s.Res1, s.Res2, Rack = rack });
            inserted++;
        }
        _logger.LogInformation("Inserted={Inserted} Exists={Exist}", inserted, exist);
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
        await conn.ExecuteAsync($"UPDATE sam_dispose_status SET {setClause} WHERE barcode=@B",
            new { B = barcode, Now = now });
        _logger.LogInformation("Update {Barcode} field={Field}", barcode, field);
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
        await conn.ExecuteAsync("DELETE FROM sam_dispose_status WHERE send=0");
        _logger.LogInformation("Deleted unsend records");
    }

    public async Task DeleteHistoryAsync()
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        var n = await conn.ExecuteAsync(
            "DELETE FROM sam_dispose_status WHERE send=1 AND TIMESTAMPDIFF(DAY,send_time,NOW())>10");
        if (n > 0) _logger.LogInformation("Deleted {Count} history records", n);
    }

    public async Task<List<SampleRecord>> GetDeliverRecordsAsync(string deliverTestName, int maxCount)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return (await conn.QueryAsync<SampleRecord>(@"
            SELECT sample_id AS Barcode FROM t_sample
            WHERE t_status='C' AND t_location LIKE '&1-%'
            AND test LIKE @TestName
            LIMIT @Max", new { TestName = $"%{deliverTestName}%", Max = maxCount })).AsList();
    }

    public async Task<List<SampleRecord>> GetPriorityRecordsAsync(string priorityTestName, int maxCount)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return (await conn.QueryAsync<SampleRecord>(@"
            SELECT sample_id AS Barcode FROM t_sample
            WHERE t_status='C' AND t_location LIKE '&1-%'
            AND test LIKE @TestName
            LIMIT @Max", new { TestName = $"%{priorityTestName}%", Max = maxCount })).AsList();
    }

    public async Task<List<SampleRecord>> GetTestNameDisposeRecordsAsync(string disposeTestName, int maxCount)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        return (await conn.QueryAsync<SampleRecord>(@"
            SELECT sample_id AS Barcode FROM t_sample
            WHERE t_status='C' AND t_location LIKE '&1-%'
            AND test LIKE @TestName
            LIMIT @Max", new { TestName = $"%{disposeTestName}%", Max = maxCount })).AsList();
    }

    public async Task SetPriorityDoneAsync(string barcode)
    {
        using var conn = NewConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE sam_dispose_status SET disposed=1 WHERE barcode=@B",
            new { B = barcode });
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
