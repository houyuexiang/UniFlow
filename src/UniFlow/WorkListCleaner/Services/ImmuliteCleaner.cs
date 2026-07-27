using Microsoft.Extensions.Logging;
using UniFlow.WorkListCleaner.Models;

namespace UniFlow.WorkListCleaner.Services;

public class ImmuliteCleaner : IWorkListCleaner
{
    private readonly ILogger<ImmuliteCleaner> _logger;
    private readonly AccessDatabaseService _accessDb;
    private readonly CentralinkService _centralink;
    private readonly CleanerConfig _config;

    public string Name => "ImmuliteWorkList";

    public ImmuliteCleaner(
        ILogger<ImmuliteCleaner> logger,
        AccessDatabaseService accessDb,
        CentralinkService centralink,
        CleanerConfig config)
    {
        _logger = logger;
        _accessDb = accessDb;
        _centralink = centralink;
        _config = config;
    }

    public async Task CleanAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var updated = new List<WorkListRecord>();

            NoSampleProcess(updated);
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

    private void NoSampleProcess(List<WorkListRecord> updated)
    {
        var sql = "select Unique_Record_ID_Num, Accession_Num, Test_Type from Worklist";
        var dt = _accessDb.GetDataTable(_config.MdbFilePath, sql);

        foreach (System.Data.DataRow row in dt.Rows)
        {
            var sid = row[1].ToString() ?? "";
            var test = row[2].ToString() ?? "";
            if (sid.Length == 0 || sid.StartsWith(_config.Prefix)) continue;

            if (_config.CheckSampleIdLen &&
                (sid.Length < _config.SampleIdLenMin || sid.Length > _config.SampleIdLenMax))
                continue;

            var record = new WorkListRecord
            {
                UniqueRecordIdNum = row[0].ToString() ?? "",
                AccessionNum = sid,
                TestType = test
            };
            updated.Add(record);

            if (updated.Count(r => r.AccessionNum == sid && r.TestType == test) >= 2)
            {
                var updates = updated.Where(r => r.AccessionNum == sid && r.TestType == test).ToList();
                foreach (var u in updates)
                {
                    var updateSql = $"update Worklist set Accession_Num = '{_config.Prefix}{u.AccessionNum}' " +
                        $"where Unique_Record_ID_Num = {u.UniqueRecordIdNum} and Accession_Num = '{u.AccessionNum}' " +
                        $"and Test_Type = '{u.TestType}'";
                    _accessDb.UpdateRecord(_config.MdbFilePath, updateSql);

                    updateSql = $"update [Result Information] set Accession_Num = '{_config.Prefix}{u.AccessionNum}' " +
                        $"where Unique_Record_ID_Num = {u.UniqueRecordIdNum} and Accession_Num = '{u.AccessionNum}' " +
                        $"and Test_Type = '{u.TestType}'";
                    _accessDb.UpdateRecord(_config.MdbFilePath, updateSql);

                    _logger.LogInformation("Marked no-sample: {Sample}", u.AccessionNum);
                }
            }
        }
    }

    private void NaResultProcess(List<WorkListRecord> updated)
    {
        var where = string.IsNullOrEmpty(_config.NaResultSqlWhere) ? "" : $" where {_config.NaResultSqlWhere}";
        var sql = $"SELECT Unique_Record_ID_Num, Accession_Num, Dilution_Factor, Errors, Test_Type, Result, CPS " +
                  $"FROM [Result Information]{where}";
        var dt = _accessDb.GetDataTable(_config.MdbFilePath, sql);

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

            var updateSql = $"UPDATE [Result Information] SET Accession_Num = '{_config.NaPrefix}{record.AccessionNum}' " +
                $"WHERE Unique_Record_ID_Num = {record.UniqueRecordIdNum} " +
                $"AND Accession_Num = '{record.AccessionNum}' " +
                $"AND Test_Type = '{record.TestType}'";
            _accessDb.UpdateRecord(_config.MdbFilePath, updateSql);

            _logger.LogInformation("Marked NA: {Sample}", record.AccessionNum);
            updated.Add(record);
        }
    }
}
