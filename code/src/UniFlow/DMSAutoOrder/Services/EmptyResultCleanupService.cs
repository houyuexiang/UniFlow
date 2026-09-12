using Dapper;
using UniFlow.Common.Services;

namespace UniFlow.DMSAutoOrder.Services;

// 清理数据库空结果：删除 reqtestresult 中 flgstatus 为 E/R/P 或 valresult1 为空的记录
public class EmptyResultCleanupService
{
    private readonly ILogger<EmptyResultCleanupService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly IErrorReporter _errors;
    private const string WhereClause = "flgstatus IN ('E','R','P') OR valresult1 = ''";

    public EmptyResultCleanupService(
        ILogger<EmptyResultCleanupService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config,
        IErrorReporter errors)
    {
        _logger = logger;
        _db = db;
        _config = config;
        _errors = errors;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            // 先查出将被清理的记录，逐条写入日志
            using var conn = _db.NewConnection();
            var records = (await conn.QueryAsync<(string codsid, string codtest)>(
                $"SELECT codsid, codtest FROM {_config.DbName}.reqtestresult WHERE {WhereClause}")).AsList();
            if (records.Count == 0) return;

            var n = await _db.ExecuteSqlAsync(
                $"DELETE FROM {_config.DbName}.reqtestresult WHERE {WhereClause}");

            _logger.LogInformation("Cleaned error/empty results: Count={Count}", n);
            foreach (var r in records)
                _logger.LogInformation("Cleaned empty result: Sid={Sid}, Test={Test}", r.codsid, r.codtest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clean error results failed");
            _errors.Report("DmsEmptyResultCleanup", "ERROR", ex.Message);
        }
    }
}