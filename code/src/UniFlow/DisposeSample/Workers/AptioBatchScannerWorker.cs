using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class AptioBatchScannerWorker : BackgroundService
{
    private readonly ILogger<AptioBatchScannerWorker> _logger;
    private readonly IDisposeDatabaseService _db;
    private readonly AptioTaskRouter _router;
    private readonly AptioConfig _aptio;
    private readonly HealthStore _health;
    private readonly IOptionsMonitor<FeatureConfig> _features;
    private bool _pauseReported;
    private int _lastScanCount = -1;

    public AptioBatchScannerWorker(
        ILogger<AptioBatchScannerWorker> logger,
        IDisposeDatabaseService db,
        AptioTaskRouter router,
        HealthStore health,
        IOptionsMonitor<FeatureConfig> features,
        IOptions<AptioConfig> aptioConfig)
    {
        _logger = logger;
        _db = db;
        _router = router;
        _health = health;
        _aptio = aptioConfig.Value;
        _features = features;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("AptioBatchScanner started, interval={Interval}s", _aptio.BatchScan.LoopIntervalSeconds);
        try
        {
            await Task.Delay(3000, ct);
        }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var f = _features.CurrentValue.Aptio;
            if (!(f.Delivery || f.Priority || f.TestNameDispose))
            {
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("AptioBatchScanner", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;
            try
            {
                try { await ScanOnceAsync(ct); try { await _health.RecordHealthAsync("AptioBatchScanner", "healthy"); } catch { } }
                catch (Exception ex) { _logger.LogWarning("AptioBatchScanner error: {Msg}", ex.Message); try { await _health.RecordHealthAsync("AptioBatchScanner", "degraded", ex.Message); await _health.RecordErrorAsync("AptioBatchScanner", "ERROR", ex.Message); } catch { } }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "AptioBatchScanner worker error"); }

            try { await Task.Delay(_aptio.BatchScan.LoopIntervalSeconds * 1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        // 初筛：用各注册者测试名的并集做 SQL 粗筛，减少拉取行数
        var candidates = _router.GetCandidateTestNames();
        if (candidates.Count == 0)
        {
            _logger.LogDebug("No registered test-name tasks, skip scan");
            return;
        }

        var samples = await _db.GetAllScanableSamplesAsync(_aptio.BatchScan.MaxOnetimeScanCount, candidates);
        // 循环降噪：候选数量变化时才记录
        if (samples.Count > 0 && samples.Count != _lastScanCount)
            _logger.LogInformation("Scan found {Count} candidate samples", samples.Count);
        _lastScanCount = samples.Count;

        // 总是分发：空结果也会触发 Router 清理已处理任务的去重记录；
        // 单轮推送上限由 Router 内部按 MaxOnetimeScanCount 控制
        await _router.DispatchAsync(samples, _aptio.BatchScan.MaxOnetimeScanCount, ct);
    }
}