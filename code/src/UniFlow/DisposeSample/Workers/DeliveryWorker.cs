using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class DeliveryWorker : BackgroundService
{
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioCommandService _commands;
    private readonly AptioConfig _aptio;
    private readonly AptioAutoDeliverConfig? _dlv;
    private readonly AptioAutoDisposeConfig _dc;
    private readonly HealthStore _health;

    public DeliveryWorker(
        ILogger<DeliveryWorker> logger,
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
        _dlv = autoProcessConfig.Value.Deliver;

        _socket.Host = _aptio.Ip;
        _socket.Port = _aptio.Port;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Delivery started, interval={Interval}s", _dc.LoopIntervalSeconds);
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await ProcessDeliverAsync(ct); try { await _health.RecordHealthAsync("Delivery", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("Delivery error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("Delivery", "degraded", ex.Message); await _health.RecordErrorAsync("Delivery", "ERROR", ex.Message); } catch { } }

                if (_dlv != null && !string.IsNullOrEmpty(_dlv.DeliveryListFilePath))
                {
                    try { await ProcessDeliveryListFileAsync(ct); try { await _health.RecordHealthAsync("DeliveryFile", "healthy"); } catch { } }
                    catch (Exception ex) { _logger.LogWarning("DeliveryFile error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DeliveryFile", "degraded", ex.Message); await _health.RecordErrorAsync("DeliveryFile", "ERROR", ex.Message); } catch { } }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Delivery worker error"); }

            await Task.Delay(_dc.LoopIntervalSeconds * 1000, ct);
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
}