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
    private FeatureConfig _features = new();
    private AptioConfig _aptio = new();
    private AptioDisposeSampleConfig _dc = new();
    private HashSet<int> _runDays = new();
    private List<TimeRange> _timeRanges = new();
    private HashSet<string> _allowErrors = new();
    private SysStatus _state = SysStatus.None;
    private string? _preBarcode;
    private int _connectRetries;
    private bool _pauseReported;
    private string _lastCountSig = "";

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
    }

    private void RefreshConfig()
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json"));
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (doc == null) return;
            if (doc.TryGetValue("Features", out var fe)) _features = JsonSerializer.Deserialize<FeatureConfig>(fe.GetRawText()) ?? new();
            if (doc.TryGetValue("Aptio", out var ap))
            {
                // 解析失败保留上一次可用的配置（绝不静默回退出厂默认）
                try
                {
                    var parsed = JsonSerializer.Deserialize<AptioConfig>(ap.GetRawText());
                    if (parsed != null) _aptio = parsed;
                }
                catch (Exception ex) { _logger.LogError(ex, "Aptio config parse failed, keeping previous values: {Raw}", ap.GetRawText()); }
                _dc = _aptio.DisposeSample ?? new();
            }
            // 统一入口：新 JSON 格式（RunDays/TimeRanges/AllowedSrmErrorCodes）优先，旧串兜底
            _runDays = _dc.GetRunDays();
            _timeRanges = _dc.GetTimeRanges();
            _allowErrors = _dc.GetAllowedErrors();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DisposeSample config reload failed, keeping previous values");
        }
    }

    public SysStatus CurrentState { get => _state; set => _state = value; }
    public string? CurrentBarcode { get => _preBarcode; set => _preBarcode = value; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DisposeSample started, node={NodeId}, interval={Interval}s",
            _aptio.SrmNodeIds.FirstOrDefault() ?? "?", _dc.LoopIntervalSeconds);
        try { await Task.Delay(3000, ct); }
        catch (OperationCanceledException) { return; }

        // sam_dispose_status 表不存在时自动创建
        try { await _db.EnsureSamDisposeTableAsync(); }
        catch (Exception ex) { _logger.LogWarning("Ensure sam_dispose_status failed: {Msg}", ex.Message); }

        while (!ct.IsCancellationRequested)
        {
            RefreshConfig();
            if (!_features.Aptio.DisposeSample)
            {
                // 关闭时上报一次 stopped，立即从健康统计中排除
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("DisposeSample", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                if (!await EnsureConnectedAsync(ct))
                { await _health.RecordHealthAsync("DisposeSample", "degraded", "Aptio not connected"); await Task.Delay(5000, ct); continue; }

                if (!await _db.PingAsync())
                { await _health.RecordHealthAsync("DisposeSample", "degraded", "Database unavailable"); await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct); continue; }

                await TickAsync(ct);
                await _health.RecordHealthAsync("DisposeSample", "healthy");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Worker error"); try { await _health.RecordHealthAsync("DisposeSample", "degraded", ex.Message); await _health.RecordErrorAsync("DisposeSample", "ERROR", ex.Message); } catch { } }

            try { await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct); }
            catch (OperationCanceledException) { break; }
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

    // ===== Timed Dispose State Machine =====

    public async Task HandleNoneAsync(int threshold, CancellationToken ct = default)
    {
        await _db.DeleteHistoryAsync();

        var totalCount = 0;
        var snapshot = new List<string>();
        foreach (var nodeId in _aptio.SrmNodeIds)
        {
            var count = await _db.CheckSrmSampleCountAsync(nodeId);
            totalCount += count;
            snapshot.Add($"{nodeId}={count}");
        }
        // 循环降噪：仅数量变化时记录（巡检重复不打）
        var sig = string.Join(",", snapshot) + $";T={totalCount}";
        if (sig != _lastCountSig)
        {
            _logger.LogInformation("Node counts [{Snapshot}], total={Total}, threshold={Threshold}",
                string.Join(",", snapshot), totalCount, threshold);
            _lastCountSig = sig;
        }

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