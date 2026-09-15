using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UniFlow.Common.Models;

namespace UniFlow.Common.Services;

// 队列模式 socket 客户端：
// 所有命令通过 Channel 入队，由单一消费循环统一处理连接、重连和收发。
// 连接失败按梯度重试：3s×10 次 → 30s×10 次 → 之后 60s；连接成功后重置梯度。
public class AptioSocketClient : IAptioSocketClient, IHostedService, IDisposable
{
    private sealed class SocketRequest
    {
        public string Command { get; }
        public CancellationToken Ct { get; }
        public TaskCompletionSource<string?> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SocketRequest(string command, CancellationToken ct)
        {
            Command = command;
            Ct = ct;
        }
    }

    private readonly ILogger<AptioSocketClient> _logger;
    private readonly Channel<SocketRequest> _queue = Channel.CreateUnbounded<SocketRequest>();
    private readonly CancellationTokenSource _shutdown = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private int _connectAttempts;
    private Task? _loopTask;

    // 命令队列保留时长：足够错误恢复
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromHours(1);

    // 发送失败自动重发次数（命令在重连后原样重发，如 ORDER…C 取消类幂等命令安全）
    private const int SendRetryCount = 3;
    // 空闲超过该时长即视为可能"半开"连接，发送前主动重建（避免写已断连接撞 WSAECONNABORTED）
    private static readonly TimeSpan IdleReconnectThreshold = TimeSpan.FromSeconds(30);
    // fire-and-forget：写入后只在此窗口内尽力等一行回复（Aptio 通常不回包），超时即视为已投递
    private static readonly TimeSpan ResponseWaitTimeout = TimeSpan.FromSeconds(1);
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    // 重试梯度：3s×10 → 30s×10 → 60s
    private const int QuickAttempts = 10;
    private const int MediumAttempts = 20;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2055;
    public bool Connected => _tcpClient?.Connected ?? false;

    public AptioSocketClient(ILogger<AptioSocketClient> logger, IOptions<AptioConfig> aptioConfig)
    {
        _logger = logger;
        Host = aptioConfig.Value.Ip;
        Port = aptioConfig.Value.Port;
    }

    // ===== IHostedService：启动消费循环 + 预连接 =====

    public Task StartAsync(CancellationToken ct)
    {
        _loopTask = Task.Run(() => ProcessLoopAsync(_shutdown.Token), _shutdown.Token);
        // 预连接：后台立即尝试，梯度重试直至成功
        _ = Task.Run(async () =>
        {
            try { await EnsureConnectedAsync(_shutdown.Token); }
            catch (OperationCanceledException) { }
            catch { /* 由消费循环持续重试 */ }
        }, _shutdown.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _queue.Writer.TryComplete();
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        DisconnectInternal();
        return Task.CompletedTask;
    }

    // ===== 对外命令接口 =====

    public async Task<string?> SendAsync(string command, CancellationToken ct = default)
    {
        var req = new SocketRequest(command, ct);
        await _queue.Writer.WriteAsync(req, ct);
        try
        {
            return await req.Tcs.Task.WaitAsync(CommandTimeout, ct);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Command timed out after 1h, dropping: {Cmd}", Sanitize(command));
            return null;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Command canceled: {Cmd}", Sanitize(command));
            throw;
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (Connected) return;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (Connected) return;
            await ConnectCoreAsync(ct);
            _connectAttempts = 0;
        }
        finally { _connectLock.Release(); }
    }

    public void Disconnect() => DisconnectInternal();

    // ===== 消费循环 =====

    private async Task ProcessLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("AptioSocketClient command loop started");
        try
        {
            await foreach (var req in _queue.Reader.ReadAllAsync(ct))
            {
                if (req.Ct.IsCancellationRequested)
                {
                    req.Tcs.TrySetCanceled(req.Ct);
                    continue;
                }

                try
                {
                    var response = await SendCoreAsync(req.Command, req.Ct);
                    req.Tcs.TrySetResult(response);
                }
                catch (OperationCanceledException) when (req.Ct.IsCancellationRequested)
                {
                    req.Tcs.TrySetCanceled(req.Ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Command failed: {Msg}", ex.Message);
                    req.Tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogInformation("AptioSocketClient command loop stopped");
    }

    // 确保连接，梯度重试直至成功或取消
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        while (!Connected)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ConnectAsync(ct);
                if (_connectAttempts > 0)
                    _logger.LogInformation("Aptio reconnected after {Attempts} attempts", _connectAttempts);
                _connectAttempts = 0;
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _connectAttempts++;
                var delay = GetRetryDelay(_connectAttempts);
                if (_connectAttempts <= 3 || _connectAttempts % 10 == 1)
                    _logger.LogWarning("Aptio connect failed (attempt {Attempts}): {Msg}, retry in {Delay}s",
                        _connectAttempts, ex.Message, (int)delay.TotalSeconds);
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { throw; }
            }
        }
    }

    private static TimeSpan GetRetryDelay(int attempts)
    {
        if (attempts <= QuickAttempts) return TimeSpan.FromSeconds(3);
        if (attempts <= MediumAttempts) return TimeSpan.FromSeconds(30);
        return TimeSpan.FromSeconds(60);
    }

    // ===== 底层收发 =====

    private async Task<string?> SendCoreAsync(string command, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt <= SendRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // 发送前确保已连接
            await EnsureConnectedAsync(ct);

            if (!Connected || _writer == null || _reader == null)
                throw new InvalidOperationException("Not connected");

            // 方案2：空闲过久 → 主动重建连接，避免对端已关闭但本地仍以为在线（半开连接）
            var idle = DateTime.UtcNow - _lastActivityUtc;
            if (idle > IdleReconnectThreshold)
            {
                _logger.LogInformation("Aptio connection idle for {Idle}s, refreshing before send", (int)idle.TotalSeconds);
                DisconnectInternal();
                await EnsureConnectedAsync(ct);
                if (!Connected || _writer == null || _reader == null)
                    throw new InvalidOperationException("Not connected after idle refresh");
            }

            // 发送（Aptio 协议为 fire-and-forget：成功=写入对端，不等待 ACK）
            try
            {
                await _writer!.WriteAsync(command.AsMemory(), ct);
                await _writer!.WriteAsync("\r\n".AsMemory(), ct);
                await _writer!.FlushAsync(ct);
                _lastActivityUtc = DateTime.UtcNow;
                _logger.LogDebug(">> {Cmd}", Sanitize(command));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                // 方案1：仅发送（写入）失败 → 断开重连 → 原命令重发（最多 SendRetryCount 次）
                lastError = ex;
                _logger.LogWarning("Aptio send failed (attempt {Attempt}/{Max}): {Msg} — reconnecting and retrying",
                    attempt + 1, SendRetryCount + 1, ex.Message);
                DisconnectInternal();
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
                catch (OperationCanceledException) { throw; }
                continue;
            }

            // 命令已成功写入。Aptio 通常不回复（异步推送模型），
            // 尽力在极短窗口内读一行：有则返回（如 STATUS 类），无则视为成功（fire-and-forget）。
            return await TryReadResponseAsync(ct);
        }
        throw new InvalidOperationException($"Aptio send failed after {SendRetryCount + 1} attempts: {lastError?.Message}", lastError);
    }

    // 尽力读一行响应；Aptio 不回包时在超时窗口后返回 null（不算失败，不触发重试）
    private async Task<string?> TryReadResponseAsync(CancellationToken ct)
    {
        if (_reader == null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseWaitTimeout);
            var response = await _reader.ReadLineAsync(timeout.Token);
            _lastActivityUtc = DateTime.UtcNow;
            if (response != null)
                _logger.LogDebug("<< {Resp}", Sanitize(response));
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 无响应 = 正常（Aptio fire-and-forget）
            return null;
        }
        catch (IOException)
        {
            // 连接被对端关闭/读超时：命令已写入，按已投递处理，交给下次发送时重连
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
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

    private void DisconnectInternal()
    {
        _writer?.Dispose(); _reader?.Dispose(); _stream?.Dispose(); _tcpClient?.Dispose();
        _writer = null; _reader = null; _stream = null; _tcpClient = null;
    }

    private static string Sanitize(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        DisconnectInternal();
        _connectLock.Dispose();
        _shutdown.Dispose();
    }
}