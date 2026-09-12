using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class DeliveryFileWorker : BackgroundService
{
    private readonly ILogger<DeliveryFileWorker> _logger;
    private readonly AptioCommandService _commands;
    private readonly AptioConfig _aptio;
    private readonly AptioDeliveryFileConfig _dlf;
    private readonly HealthStore _health;
    private readonly IOptionsMonitor<FeatureConfig> _features;
    private bool _pauseReported;

    public DeliveryFileWorker(
        ILogger<DeliveryFileWorker> logger,
        AptioCommandService commands,
        HealthStore health,
        IOptionsMonitor<FeatureConfig> features,
        IOptions<AptioConfig> aptioConfig)
    {
        _logger = logger;
        _commands = commands;
        _health = health;
        _aptio = aptioConfig.Value;
        _dlf = _aptio.DeliveryFile;
        _features = features;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("DeliveryFile started, interval={Interval}s", _dlf.LoopIntervalSeconds);
        try
        {
            await Task.Delay(3000, ct);
        }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            if (!_features.CurrentValue.Aptio.DeliveryFile)
            {
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("DeliveryFile", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                try { await ProcessDeliveryListFileAsync(ct); try { await _health.RecordHealthAsync("DeliveryFile", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("DeliveryFile error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("DeliveryFile", "degraded", ex.Message); await _health.RecordErrorAsync("DeliveryFile", "ERROR", ex.Message); } catch { } }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "DeliveryFile worker error"); }

            try { await Task.Delay(_dlf.LoopIntervalSeconds * 1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessDeliveryListFileAsync(CancellationToken ct)
    {
        var dir = new DirectoryInfo(_dlf!.DeliveryListFilePath);
        if (!dir.Exists) { dir.Create(); dir.CreateSubdirectory("Success"); dir.CreateSubdirectory("Failed"); return; }

        var success = new DirectoryInfo(Path.Combine(_dlf.DeliveryListFilePath, "Success"));
        var fail = new DirectoryInfo(Path.Combine(_dlf.DeliveryListFilePath, "Failed"));
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