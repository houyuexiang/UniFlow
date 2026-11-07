using UniFlow.Common.Services;

namespace UniFlow.DisposeSample.Services;

public class AptioCommandService
{
    private readonly IAptioSocketClient _socket;
    private readonly ILogger<AptioCommandService> _logger;

    public AptioCommandService(IAptioSocketClient socket, ILogger<AptioCommandService> logger)
    {
        _socket = socket;
        _logger = logger;
    }

    // 成功判定口径（手册 §8.59 / §9.3）：
    //   §8.59（DCAU-H00.816.12.01.02.pdf 第 498 页，/tmp/dcau.txt:24869-24881）：ACK/NAK 是
    //         Dream→GUI 的通知，只对应 ADVANCED-CONFIG-CHANGE、CLEAR-TUBES、COMMENT I021~I024
    //         这 6 条 GUI 命令；与 Host LIS 发出的 COMMENT S002 / ORDER 无关。
    //   §9.3（/tmp/dcau.txt:26761-26780）：COMMENT S002 没有任何回复/ACK 定义。
    // 因此 Host LIS 的 COMMENT S002 / ORDER 没有 ACK；正确语义是「写成功 = 已投递」，
    // 即 socket 层返回 AptioSocketClient.DeliveredMarker 即视为成功（返回值非 null）。
    // 返回 null 表示未投递（1h 超时 / 未连接 / 写失败）。

    public async Task<bool> SendDisposeAsync(string barcode, CancellationToken ct = default)
    {
        var cmd = $"COMMENT S002^{barcode}\\TRASH^S";
        _logger.LogInformation("Dispose: {Cmd}", cmd);
        var delivered = await _socket.SendAsync(cmd, ct);
        return delivered != null;
    }

    public async Task<bool> SendDeliverAsync(string barcode, CancellationToken ct = default)
    {
        var cmd = $"COMMENT S002^{barcode}\\DELIVER^S";
        _logger.LogInformation("Deliver: {Cmd}", cmd);
        var delivered = await _socket.SendAsync(cmd, ct);
        return delivered != null;
    }

    public async Task<bool> SendStatPriorityAsync(string barcode, CancellationToken ct = default)
    {
        // 手册 §9.5：COMMENT S010^<Sample-ID>\<New-Priority>^S，New-Priority 为 A(ASAP)/S(STAT)
        var cmd = $"COMMENT S010^{barcode}\\S^S";
        _logger.LogInformation("Stat Priority: {Cmd}", cmd);
        var delivered = await _socket.SendAsync(cmd, ct);
        return delivered != null;
    }

    public async Task<bool> SendCancelTestAsync(string barcode, string test, CancellationToken ct = default)
    {
        var cmd = $"ORDER {barcode}||||||||||||C|||||{test}";
        _logger.LogInformation("Cancel Test: {Cmd}", cmd);
        var delivered = await _socket.SendAsync(cmd, ct);
        return delivered != null;
    }

    public async Task<string?> SendStatusRequestAsync(CancellationToken ct = default)
    {
        return await _socket.SendAsync("STATUS-REQUEST 1", ct);
    }
}
