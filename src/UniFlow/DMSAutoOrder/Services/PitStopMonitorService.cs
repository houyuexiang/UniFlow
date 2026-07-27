using Microsoft.Extensions.Logging;

namespace UniFlow.DMSAutoOrder.Services;

public class PitStopMonitorService
{
    private readonly ILogger<PitStopMonitorService> _logger;
    private readonly DmsDatabaseService _db;
    private readonly Models.DmsOrderConfig _config;
    private readonly Dictionary<string, Dictionary<string, DateTime>> _seen = new();

    public PitStopMonitorService(
        ILogger<PitStopMonitorService> logger,
        DmsDatabaseService db,
        Models.DmsOrderConfig config)
    {
        _logger = logger;
        _db = db;
        _config = config;
    }

    public async Task ExecuteAsync()
    {
        if (!_config.EnablePitStopMonitor) return;

        try
        {
            var tables = await _db.GetPitStopTablesAsync();
            foreach (var table in tables)
            {
                var records = await _db.GetRunningPitstopRecordsAsync(table);
                if (records.Count == 0) continue;

                var ids = new HashSet<string>();
                foreach (var r in records)
                {
                    var id = r.GetValueOrDefault("idpitstop", "")?.ToString() ?? "";
                    ids.Add(id);
                    var now = DateTime.UtcNow;

                    if (!_seen.ContainsKey(table))
                        _seen[table] = new Dictionary<string, DateTime>();

                    if (!_seen[table].ContainsKey(id))
                    {
                        _seen[table][id] = now;
                    }
                    else
                    {
                        var elapsed = now - _seen[table][id];
                        if (elapsed.TotalMinutes > _config.PitStopTimeoutMinutes)
                        {
                            await _db.DeletePitstopRecordsAsync(table);
                            _logger.LogWarning("PitStop stuck >{Timeout}m, cleaned table {Table}",
                                _config.PitStopTimeoutMinutes, table);
                            _seen[table].Clear();
                            break;
                        }
                    }
                }

                if (_seen.ContainsKey(table))
                {
                    var stale = _seen[table].Keys.Where(k => !ids.Contains(k)).ToList();
                    foreach (var k in stale) _seen[table].Remove(k);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PitStop monitoring error");
        }
    }
}
