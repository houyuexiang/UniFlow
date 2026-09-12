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

    public async Task<bool> SendDisposeAsync(string barcode, CancellationToken ct = default)
    {
        var cmd = $"COMMENT S002^{barcode}\\TRASH^S";
        _logger.LogInformation("Dispose: {Cmd}", cmd);
        var ack = await _socket.SendAsync(cmd, ct);
        return ack?.StartsWith("ACK") == true;
    }

    public async Task<bool> SendDeliverAsync(string barcode, CancellationToken ct = default)
    {
        var cmd = $"COMMENT S002^{barcode}\\DELIVER^S";
        _logger.LogInformation("Deliver: {Cmd}", cmd);
        var ack = await _socket.SendAsync(cmd, ct);
        return ack?.StartsWith("ACK") == true;
    }

    public async Task<bool> SendStatPriorityAsync(string barcode, CancellationToken ct = default)
    {
        var cmd = $"COMMENT S010^{barcode}^S";
        _logger.LogInformation("Stat Priority: {Cmd}", cmd);
        var ack = await _socket.SendAsync(cmd, ct);
        return ack?.StartsWith("ACK") == true;
    }

    public async Task<bool> SendCancelTestAsync(string barcode, string test, CancellationToken ct = default)
    {
        var cmd = $"ORDER {barcode}||||||||||||C|||||{test}";
        _logger.LogInformation("Cancel Test: {Cmd}", cmd);
        var ack = await _socket.SendAsync(cmd, ct);
        return ack?.StartsWith("ACK") == true;
    }

    public async Task<string?> SendStatusRequestAsync(CancellationToken ct = default)
    {
        return await _socket.SendAsync("STATUS-REQUEST 1", ct);
    }
}
