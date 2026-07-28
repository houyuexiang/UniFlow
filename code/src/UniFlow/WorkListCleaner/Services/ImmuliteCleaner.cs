using Microsoft.Extensions.Logging;
using UniFlow.WorkListCleaner.Models;

namespace UniFlow.WorkListCleaner.Services;

public class ImmuliteCleaner : IWorkListCleaner
{
    private readonly ILogger<ImmuliteCleaner> _logger;
    private readonly AccessDatabaseService? _accessDb;
    private readonly AccessAgentClient? _agent;
    private readonly CentralinkService _centralink;
    private readonly CleanerConfig _config;

    public string Name => string.IsNullOrEmpty(_config.InstrumentName) ? "Immulite" : _config.InstrumentName;

    public ImmuliteCleaner(
        ILogger<ImmuliteCleaner> logger,
        CentralinkService centralink,
        CleanerConfig config,
        AccessDatabaseService? accessDb = null,
        AccessAgentClient? agent = null)
    {
        _logger = logger;
        _accessDb = accessDb;
        _agent = agent;
        _centralink = centralink;
        _config = config;
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var updated = new List<WorkListRecord>();
            var seen = new Dictionary<string, DateTime>();

            NoSampleProcess(updated, seen);
            NaResultProcess(updated);

            if (updated.Count > 0 && _config.EnableCentralink)
            {
                _centralink.SendLasFlag(updated, _config.CentralinkIp, _config.CentralinkPort,
                    _config.CentralinkDriver, _config.NoSampleLasFlag, _config.NaResultLasFlag, "");
            }

            if (updated.Count > 0)
                _logger.LogInformation("ImmuLite: processed {Count} records", updated.Count);
        }, ct);
    }

    private void NoSampleProcess(List<WorkListRecord> updated, Dictionary<string, DateTime> seen)
    {
        var dt = GetDataTable("select Unique_Record_ID_Num, Accession_Num, Test_Type from Worklist");
        if (dt == null) return;

        foreach (System.Data.DataRow row in dt.Rows)
        {
            var sid = row[1].ToString() ?? "";
            var test = row[2].ToString() ?? "";
            if (sid.Length == 0 || sid.StartsWith(_config.Prefix)) continue;

            if (_config.CheckSampleIdLen &&
                (sid.Length < _config.SampleIdLenMin || sid.Length > _config.SampleIdLenMax))
                continue;

            var key = $"{sid}|{test}";
            if (seen.ContainsKey(key))
            {
                var elapsed = DateTime.Now - seen[key];
                if (elapsed.TotalMinutes > _config.TimeSpanMinutes)
                {
                    var newVal = _config.Prefix + sid;
                    var id = row[0].ToString();
                    var updateSql = "update Worklist set Accession_Num = ? where Unique_Record_ID_Num = ? and Accession_Num = ? and Test_Type = ?";
                    ExecuteUpdate(updateSql, ("", newVal), ("", id), ("", sid), ("", test));

                    updateSql = "update [Result Information] set Accession_Num = ? where Unique_Record_ID_Num = ? and Accession_Num = ? and Test_Type = ?";
                    ExecuteUpdate(updateSql, ("", newVal), ("", id), ("", sid), ("", test));

                    _logger.LogInformation("Marked no-sample: {Sample}", sid);
                    updated.Add(new WorkListRecord { UniqueRecordIdNum = id ?? "", AccessionNum = sid, TestType = test });
                }
            }
            else
            {
                seen[key] = DateTime.Now;
            }
        }
    }

    private void NaResultProcess(List<WorkListRecord> updated)
    {
        var where = string.IsNullOrEmpty(_config.NaResultSqlWhere) ? "" : $" where {_config.NaResultSqlWhere}";
        var sql = $"SELECT Unique_Record_ID_Num, Accession_Num, Dilution_Factor, Errors, Test_Type, Result, CPS " +
                  $"FROM [Result Information]{where}";
        var dt = GetDataTable(sql);
        if (dt == null) return;

        foreach (System.Data.DataRow row in dt.Rows)
        {
            var sid = row["Accession_Num"].ToString() ?? "";
            if (sid.Length == 0 || sid.StartsWith(_config.NaPrefix)) continue;

            if (_config.CheckSampleIdLen &&
                (sid.Length < _config.SampleIdLenMin || sid.Length > _config.SampleIdLenMax))
                continue;

            var record = new WorkListRecord
            {
                UniqueRecordIdNum = row["Unique_Record_ID_Num"].ToString() ?? "",
                AccessionNum = sid,
                TestType = row["Test_Type"].ToString() ?? "",
                DilutionFactor = row["Dilution_Factor"].ToString() ?? "",
                Cps = row["CPS"].ToString() ?? ""
            };

            var newVal = _config.NaPrefix + record.AccessionNum;
            var uid = record.UniqueRecordIdNum;
            var updateSql = "UPDATE [Result Information] SET Accession_Num = ? WHERE Unique_Record_ID_Num = ? AND Accession_Num = ? AND Test_Type = ?";
            ExecuteUpdate(updateSql, ("", newVal), ("", uid), ("", record.AccessionNum), ("", record.TestType));

            _logger.LogInformation("Marked NA: {Sample}", record.AccessionNum);
            updated.Add(record);
        }
    }

    private System.Data.DataTable? GetDataTable(string sql)
    {
        if (_agent != null)
        {
            var tableName = sql.Contains("Worklist") ? "Worklist" : "Result Information";
            var where = "";
            var idx = sql.IndexOf("where", StringComparison.OrdinalIgnoreCase);
            if (idx > 0) where = sql[(idx + 5)..].Trim();
            var rows = _agent.GetRecordsAsync(tableName, where).GetAwaiter().GetResult();
            return ConvertToDataTable(rows);
        }
        var connStr = _config.MdbFilePath;
        if (string.IsNullOrEmpty(connStr)) return null;
        return _accessDb?.GetDataTable(connStr, sql);
    }

    private int ExecuteUpdate(string sql, params (string name, object value)[] parameters)
    {
        if (_agent != null)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < parameters.Length; i++)
                dict[$"p{i}"] = parameters[i].value;
            return _agent.UpdateAsync(sql, dict).GetAwaiter().GetResult();
        }
        return _accessDb?.UpdateRecord(_config.MdbFilePath, sql, parameters) ?? 0;
    }

    private static System.Data.DataTable? ConvertToDataTable(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return null;
        var dt = new System.Data.DataTable();
        foreach (var key in rows[0].Keys) dt.Columns.Add(key);
        foreach (var row in rows)
        {
            var dr = dt.NewRow();
            foreach (var kv in row) dr[kv.Key] = kv.Value ?? DBNull.Value;
            dt.Rows.Add(dr);
        }
        return dt;
    }
}
