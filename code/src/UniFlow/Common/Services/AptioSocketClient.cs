using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UniFlow.Common.Services;

public class AptioSocketClient : IAptioSocketClient, IDisposable
{
    private readonly ILogger<AptioSocketClient> _logger;
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2055;
    public bool Connected => _tcpClient?.Connected ?? false;

    public AptioSocketClient(ILogger<AptioSocketClient> logger) => _logger = logger;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Connected) return;
        await _lock.WaitAsync(ct);
        try
        {
            DisconnectInternal();
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(Host, Port, ct);
            _stream = _tcpClient.GetStream();
            _stream.ReadTimeout = 10000;
            _stream.WriteTimeout = 5000;
            _reader = new StreamReader(_stream, Encoding.ASCII);
            _writer = new StreamWriter(_stream, Encoding.ASCII) { NewLine = "\r\n" };
            _logger.LogInformation("Connected to Aptio {Host}:{Port}", Host, Port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Connect failed: {Message}", ex.Message);
            DisconnectInternal();
            throw;
        }
        finally { _lock.Release(); }
    }

    public async Task<string?> SendAndReceiveAsync(string command, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            EnsureConnected();
            await _writer!.WriteAsync(command.AsMemory(), ct);
            await _writer!.WriteAsync("\r\n".AsMemory(), ct);
            await _writer!.FlushAsync(ct);
            _logger.LogDebug(">> {Cmd}", Sanitize(command));
            var response = await _reader!.ReadLineAsync(ct);
            if (response != null)
                _logger.LogDebug("<< {Resp}", Sanitize(response));
            return response;
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Socket I/O error: {Message}", ex.Message);
            DisconnectInternal();
            return null;
        }
        finally { _lock.Release(); }
    }

    private void EnsureConnected()
    {
        if (!Connected || _stream == null || _reader == null || _writer == null)
            throw new InvalidOperationException("Not connected");
    }

    private void DisconnectInternal()
    {
        _writer?.Dispose(); _reader?.Dispose(); _stream?.Dispose(); _tcpClient?.Dispose();
        _writer = null; _reader = null; _stream = null; _tcpClient = null;
    }

    private static string Sanitize(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    public void Disconnect()
    {
        if (!_lock.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Disconnect: could not acquire lock within 5s, force disconnecting");
            DisconnectInternal();
            return;
        }
        try { DisconnectInternal(); } finally { _lock.Release(); }
    }

    public void Dispose() { DisconnectInternal(); _lock.Dispose(); }
}
