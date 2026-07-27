using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;

namespace UniFlow.DisposeSample.Workers;

public class DisposeSampleWorker : BackgroundService
{
    private readonly ILogger<DisposeSampleWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioCommandService _commands;
    private readonly SrmStatusDecoder _statusDecoder;
    private readonly DisposeConfig _config;
    private readonly HashSet<int> _runDays;
    private readonly List<TimeRange> _timeRanges;
    private readonly HashSet<string> _allowErrors;
    private SysStatus _state = SysStatus.None;
    private string? _preBarcode;
    private int _connectRetries;

    public DisposeSampleWorker(
        ILogger<DisposeSampleWorker> logger,
        IAptioSocketClient socket,
        IDisposeDatabaseService db,
        AptioCommandService commands,
        SrmStatusDecoder statusDecoder,
        IOptions<AptioConfig> aptioConfig,
        IOptions<DisposeConfig> disposeConfig)
    {
        _logger = logger;
        _socket = socket;
        _db = db;
        _commands = commands;
        _statusDecoder = statusDecoder;
        _config = disposeConfig.Value;

        _socket.Host = aptioConfig.Value.Ip;
        _socket.Port = aptioConfig.Value.Port;

        _runDays = WorkTimeChecker.ParseRunDays(_config.DiscardRunDate);
        _timeRanges = WorkTimeChecker.ParseTimeRanges(_config.DiscardTimeRange);

        _allowErrors = new HashSet<string>(
            _config.AllowSrmErrorCode.Split(',', StringSplitOptions.TrimEntries)
                .Where(c => !string.IsNullOrEmpty(c)));
    }

    public SysStatus CurrentState { get => _state; set => _state = value; }
    public string? CurrentBarcode { get => _preBarcode; set => _preBarcode = value; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DisposeSample started, node={NodeId}, interval={Interval}s",
            _config.SrmNodeId, _config.LoopIntervalSeconds);
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await EnsureConnectedAsync(ct))
                { await Task.Delay(5000, ct); continue; }

                if (!await _db.PingAsync())
                { await Task.Delay(_config.LoopIntervalSeconds * 1000, ct); continue; }

                await TickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Worker error"); }

            await Task.Delay(_config.LoopIntervalSeconds * 1000, ct);
        }

        await _db.DeleteUnsendAsync();
        _logger.LogInformation("DisposeSample stopped");
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
        if (!IsWorkTime(out var threshold))
        {
            await HandleCommandsAsync(ct);
            return;
        }

        await HandleCommandsAsync(ct);

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

    private bool IsWorkTime(out int threshold)
    {
        threshold = 0;
        var now = DateTime.Now;
        var dotNetDay = (int)now.DayOfWeek;
        var delphiDay = dotNetDay == 0 ? 7 : dotNetDay;
        if (!_runDays.Contains(delphiDay)) return false;

        var nowTime = TimeOnly.FromDateTime(now);
        foreach (var r in _timeRanges)
        {
            if (r.Start <= r.End)
            {
                if (nowTime >= r.Start && nowTime <= r.End)
                { threshold = r.Threshold; return true; }
            }
            else
            {
                if (nowTime >= r.Start || nowTime <= r.End)
                { threshold = r.Threshold; return true; }
            }
        }
        return false;
    }

    // ===== Batch Command Processing (Deliver / Priority) =====

    public async Task HandleCommandsAsync(CancellationToken ct = default)
    {
        if (_config.EnableDeliver)
            await ProcessDeliverAsync(ct);

        if (_config.EnableUpdatePriority)
            await ProcessPriorityAsync(ct);
    }

    private async Task ProcessDeliverAsync(CancellationToken ct)
    {
        var records = await _db.GetDeliverRecordsAsync(_config.DeliverTestName, _config.MaxOnetimeSelectDiscardCount);
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
        var records = await _db.GetPriorityRecordsAsync(_config.PriorityTestName, _config.MaxOnetimeSelectDiscardCount);
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

    // ===== Dispose State Machine =====

    public async Task HandleNoneAsync(int threshold, CancellationToken ct = default)
    {
        await _db.DeleteHistoryAsync();

        var srmCount = await _db.CheckSrmSampleCountAsync(_config.SrmNodeId);
        _logger.LogInformation("Count={Count}, threshold={Threshold}", srmCount, threshold);

        if (srmCount <= threshold) return;

        var need = srmCount - threshold;
        _logger.LogInformation("Need dispose {Need}", need);

        var samples = await _db.GetDisposeSamplesAsync(
            _config.CommandType, _config.CommandName, _config.MaxOnetimeSelectDiscardCount);

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

    public async Task HandleReadyAsync(CancellationToken ct = default)
    {
        var response = await _commands.SendStatusRequestAsync(ct);
        if (response == null || !response.StartsWith("STATUS"))
        {
            _logger.LogWarning("STATUS check failed: {Resp}", response);
            return;
        }

        if (!_statusDecoder.Decode(response, _allowErrors, out var msg, out var ok))
        {
            _logger.LogWarning("Cannot decode STATUS: {Resp}", response);
            return;
        }
        _logger.LogInformation("SRM: {Msg}", msg);
        if (!ok) return;

        var record = await _db.SelectOneSendRecordAsync();
        if (record == null)
        {
            _logger.LogInformation("No pending records → None");
            _state = SysStatus.None;
            return;
        }

        _preBarcode = record.Barcode;
        var acked = await _commands.SendDisposeAsync(record.Barcode!, ct);

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

        var disposed = await _db.CheckDisposedAsync(_preBarcode, _config.SrmNodeId);
        if (disposed)
        {
            await _db.UpdateStatusAsync(_preBarcode, "disposed");
            _state = SysStatus.Dispose;
            _logger.LogInformation("{Barcode} confirmed disposed → Dispose", _preBarcode);
        }
        else if (_config.MaxWaitDiscardCount > 0)
        {
            var cnt = await _db.GetCheckCountAsync(_preBarcode);
            if (cnt < _config.MaxWaitDiscardCount)
            {
                await _db.UpdateStatusAsync(_preBarcode, "checkcount");
                _logger.LogInformation("Waiting ({Check}/{Max})", cnt, _config.MaxWaitDiscardCount);
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
