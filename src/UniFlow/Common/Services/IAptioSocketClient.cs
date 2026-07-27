namespace UniFlow.Common.Services;

public interface IAptioSocketClient
{
    string Host { get; set; }
    int Port { get; set; }
    bool Connected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task<string?> SendAndReceiveAsync(string command, CancellationToken ct = default);
    void Disconnect();
}
