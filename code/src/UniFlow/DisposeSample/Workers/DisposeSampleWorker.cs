using System.Text.Json;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class DisposeSampleWorker : BackgroundService
{
    private readonly ILogger<DisposeSampleWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioCommandService _commands;
    private readonly SrmStatusDecoder _statusDecoder;
    private readonly HealthStore _health;
    private FeatureConfig _features;
    private AptioConfig _aptio = default!;
    private AptioAutoDisposeConfig _dc = new();
    private AptioAutoDeliverConfig? _dlv;
    private AptioAutoPriorityConfig? _prt;
    private HashSet<int> _runDays = new();
    private List<TimeRange> _timeRanges = new();
    private HashSet<string> _allowErrors = new();
    private SysStatus _state = SysStatus.None;
    private string? _preBarcode;
    private int _connectRetries;

    public DisposeSampleWorker(
        ILogger<DisposeSampleWorker> logger,
        IAptioSocketClient socket,
        IDisposeDatabaseService db,
        AptioCommandService commands,
        SrmStatusDecoder statusDecoder,
        HealthStore health)
    {
        _logger = logger;
        _socket = socket;
        _db = db;
        _commands = commands;
        _statusDecoder = statusDecoder;
        _health = health;
        RefreshConfig();

        _socket.Host = _aptio.Ip;
        _socket.Port = _aptio.Port;
    }

    private void RefreshConfig()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc == null) return;
            if (doc.TryGetValue("Features", out var fe)) _features = JsonSerializer.Deserialize<FeatureConfig>(fe.GetRawText()) ?? new();
            if (doc.TryGetValue("Aptio", out var ap)) _aptio = JsonSerializer.Deserialize<AptioConfig>(ap.GetRawText()) ?? new();
            if (doc.TryGetValue("AptioAutoProcess", out var aa))
            {
                var aap = JsonSerializer.Deserialize<AptioAutoProcessConfig>(aa.GetRawText());
                if (aap != null) { _dc = aap.Dispose ?? new(); _dlv = aap.Deliver; _prt = aap.Priority; }
            }
            _runDays = WorkTimeChecker.ParseRunDays(_dc.DiscardRunDate);
            _timeRanges = WorkTimeChecker.ParseTimeRanges(_dc.DiscardTimeRange);
            _allowErrors = new HashSet<string>(
                _dc.AllowSrmErrorCode.Split(',', StringSplitOptions.TrimEntries)
                    .Where(c => !string.IsNullOrEmpty(c)));
        }
        catch { }
    }

    public SysStatus CurrentState { get => _state; set => _state = value; }
    public string? CurrentBarcode { get => _preBarcode; set => _preBarcode = value; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DisposeSample started, node={NodeId}, interval={Interval}s",
            _aptio.SrmNodeIds.FirstOrDefault() ?? "?", _dc.LoopIntervalSeconds);
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            RefreshConfig();
            try
            {
                try { await HandleCommandsAsync(ct); } catch { }

                if (!await EnsureConnectedAsync(ct))
                { await _health.RecordHealthAsync("DisposeSample", "degraded", "Aptio not connected"); await Task.Delay(5000, ct); continue; }

                if (!await _db.PingAsync())
                { await _health.RecordHealthAsync("DisposeSample", "degraded", "Database unavailable"); await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct); continue; }

                await TickAsync(ct);
                await _health.RecordHealthAsync("DisposeSample", "healthy");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Worker error"); try { await _health.RecordHealthAsync("DisposeSample", "degraded", ex.Message); await _health.RecordErrorAsync("DisposeSample", "ERROR", ex.Message); } catch { } }

            await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct);
        }

        try { await _db.DeleteUnsendAsync(); _logger.LogInformation("DisposeSample stopped"); }
        catch { _logger.LogWarning("DisposeSample cleanup skipped"); }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_socket.Connected) { _connectRetries = 0; return true; }
        try
        {
            await _socket.ConnectAsync(ct);
            _connectRetries = 0;
            return true;
        }
        catch
        {
            _connectRetries++;
            if (_connectRetries % 6 == 1)
                _logger.LogWarning("Waiting for Aptio (attempt {Retry})", _connectRetries);
            return false;
        }
    }

    public async Task TickAsync(CancellationToken ct = default)
    {
        await HandleCommandsAsync(ct);

        var checker = new WorkTimeChecker();
        if (!checker.IsWorkTime(_runDays, _timeRanges, out var threshold)) return;

        switch (_state)
        {
            case SysStatus.None:
                await HandleNoneAsync(threshold, ct);
                break;
            case SysStatus.Ready:
                await HandleReadyAsync(ct);
                break;
            case SysStatus.Ack:
                await HandleAckAsync(ct);
                break;
            case SysStatus.Dispose:
                await HandleDisposeAsync(ct);
                break;
        }
    }

    // ===== Batch Command Processing =====

    public async Task HandleCommandsAsync(CancellationToken ct = default)
    {
        if (_features.Delivery && _dlv != null)
        {
            try { await ProcessDeliverAsync(ct); try { await _health.RecordHealthAsync("Delivery", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("Delivery error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("Delivery", "degraded", ex.Message); await _health.RecordErrorAsync("Delivery", "ERROR", ex.Message); } catch { } }
        }
        else try { await _health.RecordHealthAsync("Delivery", "stopped"); } catch { }

        if (_features.Priority && _prt != null)
        {
            try { await ProcessPriorityAsync(ct); try { await _health.RecordHealthAsync("Priority", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("Priority error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("Priority", "degraded", ex.Message); await _health.RecordErrorAsync("Priority", "ERROR", ex.Message); } catch { } }
        }
        else try { await _health.RecordHealthAsync("Priority", "stopped"); } catch { }

        if (_features.TestNameDispose && _dc.EnableTestNameDispose && !string.IsNullOrEmpty(_dc.DisposeTestName))
        {
            try { await ProcessTestNameDisposeAsync(ct); try { await _health.RecordHealthAsync("TestNameDispose", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("TestNameDispose error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("TestNameDispose", "degraded", ex.Message); await _health.RecordErrorAsync("TestNameDispose", "ERROR", ex.Message); } catch { } }
        }
        else try { await _health.RecordHealthAsync("TestNameDispose", "stopped"); } catch { }

        if (_features.Delivery && _dlv != null && !string.IsNullOrEmpty(_dlv.DeliveryListFilePath))
        {
            try { await ProcessDeliveryListFileAsync(ct); try { await _health.RecordHealthAsync("DeliveryFile", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("DeliveryFile error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DeliveryFile", "degraded", ex.Message); await _health.RecordErrorAsync("DeliveryFile", "ERROR", ex.Message); } catch { } }
        }
        else try { await _health.RecordHealthAsync("DeliveryFile", "stopped"); } catch { }

        if (_prt is { Enabled: true } && _features.Priority)
        {
            try { await ProcessPriorityAsync(ct); try { await _health.RecordHealthAsync("Priority", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("Priority error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("Priority", "degraded", ex.Message); } catch { } }
        }

        if (_dc.EnableTestNameDispose && !string.IsNullOrEmpty(_dc.DisposeTestName) && _features.TestNameDispose)
        {
            try { await ProcessTestNameDisposeAsync(ct); try { await _health.RecordHealthAsync("TestNameDispose", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("TestNameDispose error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("TestNameDispose", "degraded", ex.Message); } catch { } }
        }

        if (_dlv is { Enabled: true } && !string.IsNullOrEmpty(_dlv.DeliveryListFilePath) && _features.Delivery)
        {
            try { await ProcessDeliveryListFileAsync(ct); try { await _health.RecordHealthAsync("DeliveryFile", "healthy"); } catch { } }
            catch (Exception ex) { _logger.LogWarning("DeliveryFile error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DeliveryFile", "degraded", ex.Message); } catch { } }
        }
    }

    private async Task ProcessDeliverAsync(CancellationToken ct)
    {
        var records = await _db.GetDeliverRecordsAsync(_dlv!.TestName, _dc.MaxOnetimeSelectDiscardCount);
        foreach (var r in records)
        {
            if (ct.IsCancellationRequested) break;
            if (!_socket.Connected) break;
            var ok = await _commands.SendDeliverAsync(r.Barcode!, ct);
            if (ok)
            {
                await _db.UpdateStatusAsync(r.Barcode!, "send");
                _logger.LogInformation("Delivered {Barcode}", r.Barcode);
            }
            await Task.Delay(500, ct);
        }
    }

    private async Task ProcessPriorityAsync(CancellationToken ct)
    {
        var records = await _db.GetPriorityRecordsAsync(_prt!.TestName, _dc.MaxOnetimeSelectDiscardCount);
        foreach (var r in records)
        {
            if (ct.IsCancellationRequested) break;
            if (!_socket.Connected) break;
            var ok = await _commands.SendStatPriorityAsync(r.Barcode!, ct);
            if (ok)
            {
                await _db.SetPriorityDoneAsync(r.Barcode!);
                _logger.LogInformation("Priority set for {Barcode}", r.Barcode);
            }
            await Task.Delay(300, ct);
        }
    }

    private async Task ProcessTestNameDisposeAsync(CancellationToken ct) { var records = await _db.GetTestNameDisposeRecordsAsync(_dc.DisposeTestName, _dc.MaxOnetimeSelectDiscardCount); foreach (var r in records) { if (ct.IsCancellationRequested) break; if (!_socket.Connected) break; var ok = await _commands.SendDisposeAsync(r.Barcode!, ct); if (ok) { await _db.UpdateStatusAsync(r.Barcode!, "send"); _logger.LogInformation("Test-name disposed {Barcode}", r.Barcode); } await Task.Delay(500, ct); } }

    private async Task ProcessDeliveryListFileAsync(CancellationToken ct)
    {
        var dir = new DirectoryInfo(_dlv!.DeliveryListFilePath);
        if (!dir.Exists) { dir.Create(); dir.CreateSubdirectory("Success"); dir.CreateSubdirectory("Failed"); return; }

        var success = new DirectoryInfo(Path.Combine(_dlv.DeliveryListFilePath, "Success"));
        var fail = new DirectoryInfo(Path.Combine(_dlv.DeliveryListFilePath, "Failed"));
        if (!success.Exists) success.Create();
        if (!fail.Exists) fail.Create();

        foreach (var file in dir.EnumerateFiles())
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                _logger.LogInformation("Processing delivery file: {File}", file.Name);
                var lines = await File.ReadAllLinesAsync(file.FullName, ct);
                foreach (var line in lines)
                {
                    if (ct.IsCancellationRequested) break;
                    var sid = line.Trim();
                    if (sid.Length < 1 || sid.Length > 20) continue;
                    sid = sid.Replace(",", "").Replace("，", "").Replace("|", "").Replace(" ", "");
                    var ok = await _commands.SendDeliverAsync(sid, ct);
                    if (ok) _logger.LogInformation("File-delivered {Barcode}", sid);
                    await Task.Delay(100, ct);
                }
                file.MoveTo(Path.Combine(success.FullName, $"{DateTime.Now:yyyyMMddHHmmss}_{file.Name}"));
                _logger.LogInformation("Delivery file processed: {File}", file.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Delivery file failed {File}: {Msg}", file.Name, ex.Message);
                try { file.MoveTo(Path.Combine(fail.FullName, $"{DateTime.Now:yyyyMMddHHmmss}_{file.Name}")); } catch { }
            }
        }
    }

    // ===== Timed Dispose State Machine =====

    public async Task HandleNoneAsync(int threshold, CancellationToken ct = default)
    {
        await _db.DeleteHistoryAsync();

        var totalCount = 0;
        foreach (var nodeId in _aptio.SrmNodeIds)
        {
            var count = await _db.CheckSrmSampleCountAsync(nodeId);
            totalCount += count;
            _logger.LogInformation("Node {Node} count={Count}", nodeId, count);
        }
        _logger.LogInformation("Total count={Count}, threshold={Threshold}", totalCount, threshold);

        if (totalCount <= threshold) return;

        var need = totalCount - threshold;
        _logger.LogInformation("Need dispose {Need}", need);

        var samples = await _db.GetDisposeSamplesAsync(
            _dc.CommandType, _dc.CommandName, _dc.MaxOnetimeSelectDiscardCount);

        if (samples.Count > 0)
        {
            var inserted = await _db.InsertDisposeRecordsAsync(need, samples);
            if (inserted > 0)
            {
                _state = SysStatus.Ready;
                _logger.LogInformation("State → Ready ({Inserted} records)", inserted);
            }
        }
    }

    private static string GetNodeIdFromLocation(string? location)
    {
        // Location format: &1-{NodeId}-{Rack}-...
        if (string.IsNullOrEmpty(location)) return "";
        var parts = location.Split('-');
        return parts.Length > 1 ? parts[1] : "";
    }

    public async Task HandleReadyAsync(CancellationToken ct = default)
    {
        var response = await _commands.SendStatusRequestAsync(ct);
        if (response == null || !response.StartsWith("STATUS"))
        {
            _logger.LogWarning("STATUS check failed: {Resp}", response);
            return;
        }

        var record = await _db.SelectOneSendRecordAsync();
        if (record == null)
        {
            _logger.LogInformation("No pending records → None");
            _state = SysStatus.None;
            return;
        }

        // Check if the specific node for this record is ready
        var nodeId = GetNodeIdFromLocation(record.Location);
        if (string.IsNullOrEmpty(nodeId) || !_aptio.SrmNodeIds.Contains(nodeId))
        {
            if (_dc.SkipOnUnknownNode)
            {
                _logger.LogWarning("Record {Barcode} node '{Node}' not found in configured nodes, skipping", record.Barcode, nodeId);
                return;
            }
            _logger.LogInformation("Record {Barcode} node '{Node}' not in configured nodes, attempting dispose anyway", record.Barcode, nodeId);
        }

        if (!_statusDecoder.IsNodeReady(response, nodeId, _allowErrors))
        {
            _logger.LogInformation("Node {Node} not ready, skipping", nodeId);
            return;
        }

        _preBarcode = record.Barcode;
        if (string.IsNullOrEmpty(_preBarcode))
        {
            _logger.LogWarning("Record has no barcode, skipping");
            return;
        }
        var acked = await _commands.SendDisposeAsync(_preBarcode, ct);

        if (acked)
        {
            await _db.UpdateStatusAsync(record.Barcode!, "send");
            _state = SysStatus.Ack;
            _logger.LogInformation("State → Ack (sent {Barcode})", record.Barcode);
        }
        else
        {
            _logger.LogWarning("No ACK for dispose: {Barcode}", record.Barcode);
        }
    }

    public async Task HandleAckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_preBarcode))
        {
            _state = SysStatus.Dispose;
            return;
        }

        // Check all configured nodes for disposal confirmation
        var disposed = false;
        foreach (var nodeId in _aptio.SrmNodeIds)
        {
            if (await _db.CheckDisposedAsync(_preBarcode, nodeId))
            {
                disposed = true;
                break;
            }
        }

        if (disposed)
        {
            await _db.UpdateStatusAsync(_preBarcode, "disposed");
            _state = SysStatus.Dispose;
            _logger.LogInformation("{Barcode} confirmed disposed → Dispose", _preBarcode);
        }
        else if (_dc.MaxWaitDiscardCount > 0)
        {
            var cnt = await _db.GetCheckCountAsync(_preBarcode);
            if (cnt < _dc.MaxWaitDiscardCount)
            {
                await _db.UpdateStatusAsync(_preBarcode, "checkcount");
                _logger.LogInformation("Waiting ({Check}/{Max})", cnt, _dc.MaxWaitDiscardCount);
            }
            else
            {
                _state = SysStatus.Dispose;
                _logger.LogInformation("Max check reached → Dispose");
            }
        }
        else
        {
            _state = SysStatus.Dispose;
        }
    }

    public async Task HandleDisposeAsync(CancellationToken ct = default)
    {
        var resp = await _commands.SendStatusRequestAsync(ct);
        if (resp != null && resp.StartsWith("STATUS"))
        {
            _state = SysStatus.None;
            _logger.LogInformation("State → None (cycle complete)");
        }
    }
}