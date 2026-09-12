using System.Threading.Channels;
using Microsoft.Extensions.Options;
using UniFlow.Common.Models;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Models;
using UniFlow.DisposeSample.Services;
using UniFlow.WebAdmin.Services;

namespace UniFlow.DisposeSample.Workers;

public class PriorityWorker : BackgroundService
{
    private readonly ILogger<PriorityWorker> _logger;
    private readonly IAptioSocketClient _socket;
    private readonly AptioCommandService _commands;
    private readonly AptioPriorityConfig _prt;
    private readonly HealthStore _health;
    private readonly Channel<SampleRecord> _queue = Channel.CreateUnbounded<SampleRecord>();
    private static readonly TimeSpan IdleHeartbeat = TimeSpan.FromSeconds(60);
    private readonly IOptionsMonitor<FeatureConfig> _features;
    private bool _pauseReported;

    public PriorityWorker(
        ILogger<PriorityWorker> logger,
        IAptioSocketClient socket,
        AptioCommandService commands,
        HealthStore health,
        AptioTaskRouter router,
        IOptionsMonitor<FeatureConfig> features,
        IOptions<AptioConfig> aptioConfig)
    {
        _logger = logger;
        _socket = socket;
        _commands = commands;
        _health = health;
        _prt = aptioConfig.Value.Priority;
        _features = features;

        router.Register("Priority", _prt.TestName, n => n == _prt.TestName, _queue, () => features.CurrentValue.Aptio.Priority);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Priority consumer started");
        try { await Task.Delay(3000, ct); }
        catch (OperationCanceledException) { return; }

        try { await _health.RecordHealthAsync("Priority", "healthy"); } catch { }

        while (!ct.IsCancellationRequested)
        {
            if (!_features.CurrentValue.Aptio.Priority)
            {
                if (!_pauseReported)
                {
                    try { await _health.RecordHealthAsync("Priority", "stopped"); } catch { }
                    _pauseReported = true;
                }
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            _pauseReported = false;

            SampleRecord r;
            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(IdleHeartbeat);
                r = await _queue.Reader.ReadAsync(idleCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) break;
                // 空闲心跳：消费者空闲时也上报健康，避免状态陈旧
                try { await _health.RecordHealthAsync("Priority", "healthy"); } catch { }
                continue;
            }

            try
            {
                await ProcessAsync(r, ct);
                try { await _health.RecordHealthAsync("Priority", "healthy"); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Priority error: {Msg}", ex.Message);
                try { await _health.RecordHealthAsync("Priority", "degraded", ex.Message); await _health.RecordErrorAsync("Priority", "ERROR", ex.Message); } catch { }
            }
        }
    }

    private async Task ProcessAsync(SampleRecord r, CancellationToken ct)
    {
        // 1. 发送 S010 标记 STAT 优先
        var ok = await _commands.SendStatPriorityAsync(r.Barcode!, ct);
        if (ok)
        {
            _logger.LogInformation("Priority set for {Barcode}", r.Barcode);
        }
        // 2. 延时后取消该测试的 GUI 消息（对应原始 DoCancelTestGUIMessage）
        await Task.Delay(500, ct);
        if (!string.IsNullOrEmpty(_prt.TestName))
        {
            await _commands.SendCancelTestAsync(r.Barcode!, _prt.TestName, ct);
        }
    }
}