namespace UniFlow.Common.Services;

public interface IAptioSocketClient
{
    string Host { get; set; }
    int Port { get; set; }
    bool Connected { get; }
    Task<string?> SendAsync(string command, CancellationToken ct = default);
    Task ConnectAsync(CancellationToken ct = default);
    void Disconnect();
}