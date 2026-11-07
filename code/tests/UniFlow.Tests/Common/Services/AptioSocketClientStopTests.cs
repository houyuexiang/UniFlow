using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.Common.Services;

namespace UniFlow.Tests.Common.Services;

/// <summary>
/// AptioSocketClient 停止时的队列结算用例（P3）。
/// <para>
/// 历史缺陷：<c>StopAsync</c> 只关队列 + 断连，**不结算**队列里未完成的请求 →
/// 调用方一直等到 CommandTimeout（默认 1h）才返回。
/// 现在：StopAsync 会给所有 pending 请求一个结果（null = 未投递）→ 调用方立刻返回。
/// </para>
/// <para>
/// 用例用"只 accept 不回包"的假服务器让消费者**确定性地**卡在探测上
/// （否则连接被拒 → 冷却期快速失败 → 请求会被消费者立刻以 TimeoutException 结算，用例变flake）。
/// </para>
/// </summary>
public class AptioSocketClientStopTests
{
    private static AptioSocketClient CreateClient(int port)
    {
        var client = new AptioSocketClient(
            NullLogger<AptioSocketClient>.Instance,
            Options.Create(new AptioConfig { Ip = "127.0.0.1", Port = port }));
        client.StatusResponseTimeout = TimeSpan.FromSeconds(1);
        // 探测窗口拉长：消费者确定性地卡在探测上（不会被冷却期快速失败打断）
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <summary>只 accept、从不回包的假服务器（让探测一直挂着）。</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clients = new();

        public int Port { get; }

        public SilentServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    lock (_clients) _clients.Add(client);   // 保持连接不被关闭（否则探测会立刻失败）
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            lock (_clients)
                foreach (var c in _clients)
                    try { c.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }

    [Fact]
    public async Task Stop_SettlesQueuedRequest_WithoutCallerCancellationToken()
    {
        using var server = new SilentServer();
        using var client = CreateClient(server.Port);
        client.CommandTimeout = TimeSpan.FromSeconds(30);      // 足够长：若不结算就得等 30s
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(30);

        await client.StartAsync(CancellationToken.None);

        // ① 阻塞命令：消费者取走它并卡在探测上
        var blocker = client.SendAsync("CMD-BLOCKER", CancellationToken.None);
        await Task.Delay(300);

        // ② 排队命令：留在队列里（消费者还占着）
        var queued = client.SendAsync("CMD-QUEUED", CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await client.StopAsync(CancellationToken.None);
        var result = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        result.ShouldBeNull("停止时未投递的命令应返回 null（而不是挂到 CommandTimeout）");
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "StopAsync 后调用方应立即返回");
        await ObserveAsync(blocker);
    }

    [Fact]
    public async Task Stop_SettlesInFlightRequest_WithoutCallerCancellationToken()
    {
        using var server = new SilentServer();
        using var client = CreateClient(server.Port);
        client.CommandTimeout = TimeSpan.FromSeconds(30);
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(30);

        await client.StartAsync(CancellationToken.None);

        // 消费者取走这条命令并卡在探测上（在途请求）
        var inflight = client.SendAsync("CMD-INFLIGHT", CancellationToken.None);
        await Task.Delay(300);

        var sw = Stopwatch.StartNew();
        await client.StopAsync(CancellationToken.None);
        var result = await inflight.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        result.ShouldBeNull();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_WithoutStart_SettlesRequest_Immediately()
    {
        using var server = new SilentServer();
        using var client = CreateClient(server.Port);
        client.CommandTimeout = TimeSpan.FromSeconds(30);

        // 消费者从未启动 → 请求一直留在队列里
        var pending = client.SendAsync("CMD-NEVER-CONSUMED", CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await client.StopAsync(CancellationToken.None);
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        result.ShouldBeNull();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SendAfterStop_DoesNotThrow_Unavailable()
    {
        using var server = new SilentServer();
        using var client = CreateClient(server.Port);
        client.EnqueueTimeout = TimeSpan.FromMilliseconds(500);

        await client.StopAsync(CancellationToken.None);

        // 停止后再投递：不得抛异常（未投递 → 返回 null）
        var result = await client.SendAsync("CMD-AFTER-STOP", CancellationToken.None);
        result.ShouldBeNull();
    }

    /// <summary>观察（并吞掉）后台任务的异常，避免未观测异常影响测试结果。</summary>
    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* 停止后被取消/失败都属于预期 */ }
    }
}
