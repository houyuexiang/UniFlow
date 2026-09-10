using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class TestNameDisposeWorker : BackgroundService
{
    private readonly ILogger<TestNameDisposeWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioCommandService _commands;
    private readonly AptioConfig _aptio;
    private readonly AptioAutoDisposeConfig _dc;
    private readonly HealthStore _health;

    public TestNameDisposeWorker(
        ILogger<TestNameDisposeWorker> logger,
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

        _socket.Host = _aptio.Ip;
        _socket.Port = _aptio.Port;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("TestNameDispose started, interval={Interval}s", _dc.LoopIntervalSeconds);
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await ProcessTestNameDisposeAsync(ct); try { await _health.RecordHealthAsync("TestNameDispose", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("TestNameDispose error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("TestNameDispose", "degraded", ex.Message); await _health.RecordErrorAsync("TestNameDispose", "ERROR", ex.Message); } catch { } }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "TestNameDispose worker error"); }

            await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct);
        }
    }

    private async Task ProcessTestNameDisposeAsync(CancellationToken ct)
    {
        var records = await _db.GetTestNameDisposeRecordsAsync(_dc.DisposeTestName, _dc.MaxOnetimeSelectDiscardCount);
        foreach (var r in records)
        {
            if (ct.IsCancellationRequested) break;
            if (!_socket.Connected) break;
            var ok = await _commands.SendDisposeAsync(r.Barcode!, ct);
            if (ok)
            {
                await _db.UpdateStatusAsync(r.Barcode!, "send");
                _logger.LogInformation("Test-name disposed {Barcode}", r.Barcode);
            }
            await Task.Delay(500, ct);
        }
    }
}