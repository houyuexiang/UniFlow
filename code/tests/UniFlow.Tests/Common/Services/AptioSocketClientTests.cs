using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.Common.Services;

namespace UniFlow.Tests.Common.Services;

/// <summary>
/// W3：半开连接 —— STATUS-REQUEST 连续拿不到有效 STATUS 回复（null 或非 STATUS 开头）
/// 达 2 次即强制断开（Connected == false），收到有效 STATUS 回复则计数归零。
/// 用本地 TcpListener 假服务器验证：第 1 次请求回 STATUS（供连接探测用），其后按用例决定回包/不回包。
/// </summary>
public class AptioSocketClientTests
{
    private const string StatusLine = @"STATUS\18^SRM^1^ON^0000^G^^\";

    /// <summary>
    /// 轮询等待 <c>Connected</c> 变为 true。
    /// <para>
    /// 新语义下 <c>Connected</c> 只在"连接建立且 <b>探测回复被客户端读回</b>"后才置 true，
    /// 而 <c>server.FirstReplySent</c> 只代表**服务端已写出**回复 —— 两者之间存在毫秒级线程调度差。
    /// 立即断言会出现随机假红（实测 10/20 次 false），故改为轮询；**超时不放宽断言**（仍然失败）。
    /// </para>
    /// </summary>
    private static async Task WaitForConnectedAsync(AptioSocketClient client, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (client.Connected) return;
            await Task.Delay(10);
        }
        client.Connected.ShouldBeTrue("Connected 应最终为 true（连接建立且探测回复被读回后才置位）");
    }

    [Fact]
    public async Task StatusRequest_TwoConsecutiveSilentReplies_DisconnectsHalfOpenConnection()
    {
        // 假服务器：第 1 次（连接探测）回 STATUS，之后收包但不回包
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null);
        using var client = CreateClient(server.Port);

        await client.StartAsync(CancellationToken.None);
        await server.FirstReplySent.WaitAsync(TimeSpan.FromSeconds(10));

        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询

        // 第 1 次 STATUS-REQUEST：无回复 → 计数 1（未达阈值，不断开）
        var r1 = await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None);
        r1.ShouldBeNull();
        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询

        // 第 2 次 STATUS-REQUEST：连续 2 次未命中 → 强制断开
        var r2 = await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None);
        r2.ShouldBeNull();
        client.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task StatusRequest_TwoConsecutiveNonStatusReplies_DisconnectsHalfOpenConnection()
    {
        // 假服务器：探测回 STATUS，之后回非 STATUS 内容
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : "GARBAGE");
        using var client = CreateClient(server.Port);

        await client.StartAsync(CancellationToken.None);
        await server.FirstReplySent.WaitAsync(TimeSpan.FromSeconds(10));

        var r1 = await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None);
        r1.ShouldBe("GARBAGE");
        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询

        var r2 = await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None);
        r2.ShouldBe("GARBAGE");
        client.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task StatusRequest_ValidStatusReply_ResetsMissCounter()
    {
        // 假服务器：探测 → STATUS；随后 GARBAGE / STATUS / GARBAGE / GARBAGE
        // 期望：GARBAGE(1) → STATUS(归零) → GARBAGE(1，不断开) → GARBAGE(2，断开)
        await using var server = new FakeAptioServer((index, _) => index switch
        {
            0 => StatusLine,
            2 => StatusLine,
            _ => "GARBAGE"
        });
        using var client = CreateClient(server.Port);

        await client.StartAsync(CancellationToken.None);
        await server.FirstReplySent.WaitAsync(TimeSpan.FromSeconds(10));

        (await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None)).ShouldBe("GARBAGE");
        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询

        (await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None)).ShouldBe(StatusLine);
        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询

        // 若计数未归零，这一次就是连续第 2 次未命中 → 应当被断开；归零则不断开
        (await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None)).ShouldBe("GARBAGE");
        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询

        (await client.SendAsync("STATUS-REQUEST 1", CancellationToken.None)).ShouldBe("GARBAGE");
        client.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task NonStatusCommand_ReturnsDeliveredMarker()
    {
        // 假服务器：回 STATUS 但业务命令不回包（W1：COMMENT/ORDER 无 ACK，写成功=已投递）
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null);
        using var client = CreateClient(server.Port);

        await client.StartAsync(CancellationToken.None);
        await server.FirstReplySent.WaitAsync(TimeSpan.FromSeconds(10));

        var result = await client.SendAsync("COMMENT S002^S001\\TRASH^S", CancellationToken.None);
        result.ShouldBe(AptioSocketClient.DeliveredMarker);
        await WaitForConnectedAsync(client);   // 探测回复被读回后才置位，需轮询
    }

    private static AptioSocketClient CreateClient(int port)
    {
        var client = new AptioSocketClient(
            NullLogger<AptioSocketClient>.Instance,
            Options.Create(new AptioConfig { Ip = "127.0.0.1", Port = port }));
        // 测试用：把 STATUS 等待窗口压到下限（属性会钳制到 [1s,60s]），避免每次未命中都等 10s
        client.StatusResponseTimeout = TimeSpan.FromSeconds(1);
        return client;
    }

    [Fact]
    public void StatusResponseTimeout_ClampedToSafeRange()
    {
        using var client = CreateClient(1);

        client.StatusResponseTimeout = TimeSpan.Zero;
        client.StatusResponseTimeout.ShouldBe(TimeSpan.FromSeconds(1));

        client.StatusResponseTimeout = TimeSpan.FromSeconds(-5);
        client.StatusResponseTimeout.ShouldBe(TimeSpan.FromSeconds(1));

        client.StatusResponseTimeout = TimeSpan.FromHours(1);
        client.StatusResponseTimeout.ShouldBe(TimeSpan.FromSeconds(60));

        client.StatusResponseTimeout = TimeSpan.FromSeconds(10);
        client.StatusResponseTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 假 Aptio 服务器：接受连接、逐行读取请求，按 responder(index, line) 决定是否回包。
    /// responder 返回 null = 收包但不回包。
    /// </summary>
    private sealed class FakeAptioServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<int, string, string?> _responder;
        private readonly List<string> _received = new();
        private readonly TaskCompletionSource _firstReplySent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Task FirstReplySent => _firstReplySent.Task;
        public IReadOnlyList<string> Received
        {
            get { lock (_received) return _received.ToList(); }
        }

        public FakeAptioServer(Func<int, string, string?> responder)
        {
            _responder = responder;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Start();
        }

        private void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => PumpAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task PumpAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    var index = 0;
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        string? reply;
                        lock (_received) _received.Add(line);
                        reply = _responder(index++, line);
                        if (reply == null) continue;
                        var bytes = Encoding.ASCII.GetBytes(reply + "\r\n");
                        await stream.WriteAsync(bytes);
                        await stream.FlushAsync();
                        _firstReplySent.TrySetResult();
                    }
                }
            }
            catch (Exception)
            {
                // 客户端断开：忽略
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            if (_acceptLoop != null)
            {
                try { await _acceptLoop; } catch { }
            }
            _cts.Dispose();
        }
    }
}
