using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class PriorityWorker : BackgroundService
{
    private readonly ILogger<PriorityWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioCommandService _commands;
    private readonly AptioConfig _aptio;
    private readonly AptioAutoPriorityConfig? _prt;
    private readonly AptioAutoDisposeConfig _dc;
    private readonly HealthStore _health;

    public PriorityWorker(
        ILogger<PriorityWorker> logger,
        IAptioSocketClient socket,
        IDisposeDatabaseService db,
        AptioCommandService commands,
        HealthStore health,
        IOptions<AptioConfig> aptioConfig,
        IOptions<AptioAutoProcessConfig> autoProcessConfig)
    {
        _logger = logger;
        _socket = socket;
        _db = db;
        _commands = commands;
        _health = health;
        _aptio = aptioConfig.Value;
        _dc = autoProcessConfig.Value.Dispose ?? new();
        _prt = autoProcessConfig.Value.Priority;

        _socket.Host = _aptio.Ip;
        _socket.Port = _aptio.Port;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Priority started, interval={Interval}s", _dc.LoopIntervalSeconds);
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await ProcessPriorityAsync(ct); try { await _health.RecordHealthAsync("Priority", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("Priority error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("Priority", "degraded", ex.Message); await _health.RecordErrorAsync("Priority", "ERROR", ex.Message); } catch { } }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Priority worker error"); }

            await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct);
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
}