using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using UniFlow.Common.Models;
using UniFlow.Common.Services;

namespace UniFlow.Tests.Common.Services;

/// <summary>
/// P1-3：AptioSocketClient 命令队列与连接健壮性
/// <list type="bullet">
/// <item>队列有界（默认 1000）：满则等待，由调用方 ct / 入队超时（默认 30s）控制，不丢命令；</item>
/// <item>排队中已超时的请求**绝不发送**（防陈旧重放）；</item>
/// <item>排队年龄上限（默认 120s）：超过即丢弃不发送；</item>
/// <item>连接总超时 120s + 失败冷却 30s：冷却期内新命令快速失败，不阻塞消费者；</item>
/// <item>Connected 语义：<b>仅在连接建立且探测成功后</b>为 true；断开后为 false（不会被在途探测改回 true）。</item>
/// </list>
/// </summary>
public class AptioSocketClientQueueTests
{
    private const string StatusLine = @"STATUS\18^SRM^1^ON^0000^G^^\";

    private static AptioSocketClient CreateClient(int port, int queueCapacity = AptioSocketClient.CommandQueueCapacity, ILogger<AptioSocketClient>? logger = null)
    {
        var client = new AptioSocketClient(
            logger ?? NullLogger<AptioSocketClient>.Instance,
            Options.Create(new AptioConfig { Ip = "127.0.0.1", Port = port }),
            queueCapacity);
        client.StatusResponseTimeout = TimeSpan.FromSeconds(1);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(1);
        return client;
    }

    /// <summary>取一个当前空闲的本地端口（先监听再停掉）。</summary>
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>轮询等待 Connected 变成期望值。</summary>
    private static async Task<bool> WaitForConnectedAsync(AptioSocketClient client, bool expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (client.Connected == expected) return true;
            await Task.Delay(25);
        }
        return false;
    }

    /// <summary>断言 Connected 在一段时间内保持稳定（不被在途探测改回）。</summary>
    private static async Task AssertStableAsync(AptioSocketClient client, bool expected, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            client.Connected.ShouldBe(expected, "Connected 状态在稳定窗口内不应变化");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Queue_IsBounded_SendAsync_Waits_WhenFull_AndRespectsCallerCt()
    {
        // 队列容量 4；假服务器 accept 但**不回包** → 连接探测挂住（ProbeResponseTimeout=30s）
        // → 消费者被卡住 → 队列很快填满
        await using var server = new FakeAptioServer((index, _) => null);
        var logger = new CapturingLogger();
        using var client = CreateClient(server.Port, queueCapacity: 4, logger);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(30);   // 探测挂住 → 消费者卡住
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(60);
        client.EnqueueTimeout = TimeSpan.FromMilliseconds(600);

        await client.StartAsync(CancellationToken.None);

        // 连续投递 6 条（不等待结果：消费者正卡在探测上）
        // 容量 4 + 消费者占用 1 → 第 6 条必然因"队列满"而等待
        var pending = new List<Task<string?>>();
        for (int i = 0; i < 6; i++)
            pending.Add(client.SendAsync($"CMD{i}", CancellationToken.None));

        await Task.Delay(TimeSpan.FromSeconds(2));

        // 关键断言：**队列满**的丢弃路径被触发（无界队列时不会出现该日志 → 用例变红）
        logger.Entries.Any(e => e.Level == LogLevel.Warning
                                && e.Message.Contains("Command queue is full")).ShouldBeTrue(
            "有界队列满时应记录 'Command queue is full'（无界队列不会有）");

        // 队列满时，等待时长由调用方 ct 控制 → 超时抛 OperationCanceledException（不丢命令、不无限阻塞）
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var sw = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(
            () => client.SendAsync("CMD_OVERFLOW", cts.Token));
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(3000, "队列满时应由调用方 ct 控制等待时长");
    }

    [Fact]
    public async Task QueueFull_EnqueueTimesOut_AfterEnqueueTimeout()
    {
        // 容量 1：消费者占 1 条（卡在探测上）+ 队列 1 条 → 第 3 条必然入队等待
        // → EnqueueTimeout(100ms) 后返回 null（不阻塞 1h）
        await using var server = new FakeAptioServer((index, _) => null);
        var logger = new CapturingLogger();
        using var client = CreateClient(server.Port, queueCapacity: 1, logger);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(30);   // 探测挂住 → 消费者卡住
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(60);
        client.EnqueueTimeout = TimeSpan.FromMilliseconds(100);
        client.CommandTimeout = TimeSpan.FromSeconds(30);   // 结果等待足够长（与入队超时无关）

        await client.StartAsync(CancellationToken.None);

        var pending = new List<Task<string?>>();
        pending.Add(client.SendAsync("BLOCKER", CancellationToken.None));   // 消费者取走并卡住
        pending.Add(client.SendAsync("Q1", CancellationToken.None));        // 占满队列

        var sw = Stopwatch.StartNew();
        var result = await client.SendAsync("Q2", CancellationToken.None);  // 入队等待 → 超时
        sw.Stop();

        result.ShouldBeNull("入队超时 → 未投递 → 返回 null（调用方自行重试）");
        sw.ElapsedMilliseconds.ShouldBeLessThan(2000, "入队等待应由 EnqueueTimeout 控制，不能卡 1h");
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(50, "应确实等待到入队超时");
        logger.Entries.Any(e => e.Level == LogLevel.Warning
                                && e.Message.Contains("Command queue is full")).ShouldBeTrue();
    }

    [Fact]
    public async Task ExpiredRequest_IsNotSent_WhenConnectionRecovers()
    {
        // 场景：① 先投一条"阻塞命令"把消费者卡在连接上（端口无人监听）；
        //       ② 再投一条会超时的命令 —— 它**留在队列里**并被标记为过期；
        //       ③ 端口起服务 → 消费者连上后取到那条过期命令 → 必须丢弃、不得补发。
        var port = FreePort();
        using var client = CreateClient(port);
        client.CommandTimeout = TimeSpan.FromMilliseconds(300);   // 快速过期
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(30);

        await client.StartAsync(CancellationToken.None);

        // ① 阻塞命令：消费者取走它并卡在连接重试上
        var blocker = client.SendAsync("COMMENT S002^BLOCKER^TRASH^S", CancellationToken.None);

        // ② 过期命令：留在队列中 → 300ms 后超时被标记为过期
        var stale = await client.SendAsync("COMMENT S002^STALE^TRASH^S", CancellationToken.None);
        stale.ShouldBeNull();

        // ③ 端口起服务：消费者重连成功后处理队列
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null);
        server.MoveTo(port);

        await server.FirstReplySent.WaitAsync(TimeSpan.FromSeconds(20));
        // 等阻塞命令真正被发出（说明消费者已连上并开始处理队列），再断言过期命令没被补发
        await server.WaitForReceived("COMMENT S002^BLOCKER^TRASH^S", TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(1));   // 给消费者一点时间取队列

        server.Received.ShouldNotContain("COMMENT S002^STALE^TRASH^S");
        // 阻塞命令允许被补发（它已进入 SendCoreAsync，无法撤回 —— 已知边界）
        server.Received.ShouldContain("COMMENT S002^BLOCKER^TRASH^S");
        await Task.WhenAny(blocker, Task.Delay(1000));
    }

    [Fact]
    public async Task QueuedRequest_OlderThanMaxAge_IsNotSent()
    {
        // 排队年龄上限：MaxQueuedAge=100ms；命令在队列里待够久 → 直接丢弃、不发送
        // （CommandTimeout 足够长 → 不是"等待结果超时"路径，而是"年龄上限"路径）
        var port = FreePort();
        using var client = CreateClient(port);
        client.MaxQueuedAge = TimeSpan.FromMilliseconds(100);
        client.CommandTimeout = TimeSpan.FromSeconds(30);
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(30);

        await client.StartAsync(CancellationToken.None);

        // 阻塞命令占住消费者
        var blocker = client.SendAsync("COMMENT S002^BLOCKER^TRASH^S", CancellationToken.None);
        // 目标命令进入队列并等待（超过 MaxQueuedAge）
        var aged = client.SendAsync("COMMENT S002^AGED^TRASH^S", CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));    // 让它在队列里变老

        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null);
        server.MoveTo(port);

        await server.WaitForReceived("COMMENT S002^BLOCKER^TRASH^S", TimeSpan.FromSeconds(25));
        await Task.Delay(TimeSpan.FromSeconds(1));            // 消费者接着取那条"老"命令

        server.Received.ShouldNotContain("COMMENT S002^AGED^TRASH^S");
        await Task.WhenAny(aged, Task.Delay(1000));
    }

    [Fact]
    public async Task ConnectFailure_Cooldown_MakesNextCommand_FailFast()
    {
        // 关键设计：连接总超时设成 **30s**（很长）→ 只有"冷却期立即失败"才能让命令在 2s 内返回；
        // 若冷却被去掉，命令会一直重试连接直到 30s 总超时 → 用例变红。
        var port = FreePort();      // 无人监听 → 连接失败
        using var client = CreateClient(port);
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(30);
        client.ConnectFailureCooldown = TimeSpan.FromSeconds(30);
        client.CommandTimeout = TimeSpan.FromSeconds(60);

        await client.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));   // 让后台预连接失败一次（冷却起点被记录）

        var sw = Stopwatch.StartNew();
        await Should.ThrowAsync<Exception>(() => client.SendAsync("CMD", CancellationToken.None));
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "冷却期内应立即失败，不得等待连接总超时");
    }

    [Fact]
    public async Task Connected_IsTrue_AfterProbe_AndFalse_AfterDisconnect()
    {
        // 修掉 P1-1 抖动：**等待探测落地**再断言；断开后必须保持 false（不被在途探测改回 true）
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null);
        using var client = CreateClient(server.Port);

        client.Connected.ShouldBeFalse();

        await client.StartAsync(CancellationToken.None);

        // 等待"探测成功 → Connected=true"真正落地
        (await WaitForConnectedAsync(client, true, TimeSpan.FromSeconds(20))).ShouldBeTrue("探测成功后 Connected 应为 true");
        // 稳定窗口：不能自己翻转
        await AssertStableAsync(client, true, TimeSpan.FromMilliseconds(300));

        client.Disconnect();
        client.Connected.ShouldBeFalse();
        // 断开后必须**保持** false（旧的实现里在途探测会把它改回 true → 抖动根因）
        await AssertStableAsync(client, false, TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task Connected_IsTrue_OnlyAfterProbe_AndFalseAfterDisconnect()
    {
        // 服务端 accept 但**不回包** → 探测挂住（探测窗口 8s）→ 探测期间 Connected 必须保持 false；
        // 服务端开始回 STATUS → 探测通过 → Connected=true；Disconnect 后 false 且保持 false。
        var reply = false;
        await using var server = new FakeAptioServer((index, _) => reply ? StatusLine : null);
        using var client = CreateClient(server.Port);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(8);   // 探测窗口足够长，便于观察"探测期间"

        await client.StartAsync(CancellationToken.None);

        // 探测挂住期间：Connected 不得为 true（旧语义"TCP 连上即置位"会在这里露馅）
        for (int i = 0; i < 8; i++)
        {
            client.Connected.ShouldBeFalse("探测未通过时不得声称已连接");
            await Task.Delay(100);
        }

        // 服务端开始回包 → 客户端重试探测成功 → Connected=true
        reply = true;
        (await WaitForConnectedAsync(client, true, TimeSpan.FromSeconds(30))).ShouldBeTrue("探测成功后 Connected 应为 true");
        await AssertStableAsync(client, true, TimeSpan.FromMilliseconds(300));

        client.Disconnect();
        client.Connected.ShouldBeFalse();
        await AssertStableAsync(client, false, TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public async Task SlowStatusReply_RepeatedConnectAsync_ConnectsOnce_AndBecomesConnected()
    {
        // 复核 P1：STATUS 回包 3.5s（慢链路）时，worker 每 300ms 调一次 ConnectAsync
        // → 旧实现每次都拆掉正在探测的连接 → 永远建不起来（everConnected=false）
        // → 现在 ConnectAsync 与内部发送同一条单飞+探测流程：只连一次、只探测一次
        await using var server = new FakeAptioServer((index, _) => StatusLine, replyDelay: TimeSpan.FromMilliseconds(3500));
        using var client = CreateClient(server.Port);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(8);   // 慢回包(3.5s)仍能通过探测

        await client.StartAsync(CancellationToken.None);

        // 模拟 worker 轮询：每 300ms 调一次 ConnectAsync
        var sw = Stopwatch.StartNew();
        var calls = new List<Task>();
        while (sw.Elapsed < TimeSpan.FromSeconds(12))
        {
            calls.Add(client.ConnectAsync(CancellationToken.None));
            await Task.Delay(300);
        }
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(40));

        // 断言：最终连上，且**没有反复拆链**（accept/探测次数合理，不是 17 次）
        client.Connected.ShouldBeTrue("慢回包下也必须最终连上");
        server.AcceptCount.ShouldBe(1, "worker 反复 ConnectAsync 不应反复建链/拆链");
        server.ProbeCount.ShouldBe(1, "探测不应重复（否则服务端收到多条 STATUS-REQUEST）");
        Console.WriteLine($"[slow-link] elapsed={sw.ElapsedMilliseconds}ms accept={server.AcceptCount} probe={server.ProbeCount} connected={client.Connected}");
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_ReturnsImmediately_WithoutReprobe()
    {
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null);
        using var client = CreateClient(server.Port);
        await client.StartAsync(CancellationToken.None);

        (await WaitForConnectedAsync(client, true, TimeSpan.FromSeconds(20))).ShouldBeTrue();
        var probesBefore = server.ProbeCount;

        var sw = Stopwatch.StartNew();
        await client.ConnectAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(1000, "已连接时 ConnectAsync 应立即返回");
        server.ProbeCount.ShouldBe(probesBefore, "已连接时不得重复探测");
        client.Connected.ShouldBeTrue();
    }

    [Fact]
    public async Task ConnectAsync_DuringProbe_Waits_AndDoesNotBreakConnection()
    {
        // 探测进行中（Connected=false）时再次调用 ConnectAsync：
        // 应等待同一次连接+探测（不拆链、不并发探测），最终连上
        await using var server = new FakeAptioServer((index, _) => StatusLine, replyDelay: TimeSpan.FromMilliseconds(2000));
        using var client = CreateClient(server.Port);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(8);

        await client.StartAsync(CancellationToken.None);
        await Task.Delay(300);       // 让第一次探测在途

        client.Connected.ShouldBeFalse("探测未完成时不得声称已连接");
        var second = client.ConnectAsync(CancellationToken.None);   // 并发的第二次调用

        await Task.WhenAll(new[] { second }).WaitAsync(TimeSpan.FromSeconds(30));

        client.Connected.ShouldBeTrue();
        server.AcceptCount.ShouldBe(1, "并发 ConnectAsync 不得另开连接（不拆在用连接）");
        server.ProbeCount.ShouldBe(1, "不得并发探测");
    }

    [Fact]
    public async Task BudgetExpiry_SetsCooldown_NextCallFailsFastWithCooldown()
    {
        // 黑洞对端：TCP 能连上但**永不回 STATUS** → 探测一直挂到预算到期
        await using var server = new FakeAptioServer((index, _) => null);
        using var client = CreateClient(server.Port);
        client.ConnectTotalTimeout = TimeSpan.FromMilliseconds(300);   // 预算很短
        client.ConnectFailureCooldown = TimeSpan.FromSeconds(30);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(30);        // 探测必然超时（靠预算收口）

        // 不启动后台预连接任务，避免它抢先写入冷却（这里只测"预算到期 → 冷却"的链路）
        // ① 第一次：预算到期 → 必须抛 TimeoutException（不是裸 TaskCanceledException）
        var sw1 = Stopwatch.StartNew();
        var ex1 = await Should.ThrowAsync<TimeoutException>(() => client.ConnectAsync(CancellationToken.None));
        sw1.Stop();
        ex1.Message.ShouldContain("total timeout");

        // ② 第二次：冷却期内 → 立即失败（<100ms）且消息含 cooldown
        var sw2 = Stopwatch.StartNew();
        var ex2 = await Should.ThrowAsync<TimeoutException>(() => client.ConnectAsync(CancellationToken.None));
        sw2.Stop();
        ex2.Message.ShouldContain("cooldown");
        sw2.ElapsedMilliseconds.ShouldBeLessThan(100, "冷却期内应立即失败，不得再等满总超时");
        Console.WriteLine($"[cooldown] first={sw1.ElapsedMilliseconds}ms second={sw2.ElapsedMilliseconds}ms msg2={ex2.Message}");
    }

    [Fact]
    public async Task BudgetExpiry_ThenSendAsync_FailsFastWithCooldown()
    {
        // 同上，但走命令路径（SendAsync）：第一条因预算到期失败，第二条走冷却快速失败
        await using var server = new FakeAptioServer((index, _) => null);
        using var client = CreateClient(server.Port);
        client.ConnectTotalTimeout = TimeSpan.FromMilliseconds(400);
        client.ConnectFailureCooldown = TimeSpan.FromSeconds(30);
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(30);

        await client.StartAsync(CancellationToken.None);

        var sw1 = Stopwatch.StartNew();
        await Should.ThrowAsync<TimeoutException>(() => client.SendAsync("CMD1", CancellationToken.None));
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        var ex2 = await Should.ThrowAsync<TimeoutException>(() => client.SendAsync("CMD2", CancellationToken.None));
        sw2.Stop();
        ex2.Message.ShouldContain("cooldown");
        sw2.ElapsedMilliseconds.ShouldBeLessThan(500, "冷却期内第二条命令应快速失败");
    }

    [Fact]
    public async Task PreConnect_RetriesAfterBudgetExpiry_AndConnectsWhenServerAppears()
    {
        // 启动时端口不可用 → 后台预连接任务在预算到期后**不能退出**；
        // 之后端口起服务 → 60s 内自动连上（Connected=true）
        var port = FreePort();
        using var client = CreateClient(port);
        client.ConnectTotalTimeout = TimeSpan.FromSeconds(1);        // 预算很短 → 频繁到期
        client.ConnectFailureCooldown = TimeSpan.FromSeconds(2);     // 冷却很短 → 重试快
        client.ProbeResponseTimeout = TimeSpan.FromSeconds(5);

        await client.StartAsync(CancellationToken.None);
        client.Connected.ShouldBeFalse("端口不可用时不应声称已连接");

        // 让后台任务经历若干次"预算到期 + 冷却"（证明它没退出）
        await Task.Delay(TimeSpan.FromSeconds(8));

        // 端口起服务
        await using var server = new FakeAptioServer((index, _) => index == 0 ? StatusLine : null, port: port);

        var sw = Stopwatch.StartNew();
        (await WaitForConnectedAsync(client, true, TimeSpan.FromSeconds(60))).ShouldBeTrue(
            "后台预连接任务应在预算到期后继续重试，并在端口恢复后自动连上");
        sw.Stop();
        Console.WriteLine($"[preconnect] Connected=true after {sw.ElapsedMilliseconds}ms (server started at t≈8s)");
    }

    /// <summary>捕获日志（用于断言"队列满""冷却"等关键路径确实被触发）。</summary>
    private sealed class CapturingLogger : ILogger<AptioSocketClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>假 Aptio 服务器（可指定端口/回包延迟）：接受连接、逐行读取、按 responder 决定是否回包。</summary>
    private sealed class FakeAptioServer : IAsyncDisposable
    {
        private TcpListener _listener;
        private readonly Func<int, string, string?> _responder;
        private readonly TimeSpan _replyDelay;
        private readonly List<string> _received = new();
        private readonly TaskCompletionSource _firstReplySent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private Task? _acceptLoop;
        private int _acceptCount;

        public int Port { get; private set; }
        public Task FirstReplySent => _firstReplySent.Task;
        public IReadOnlyList<string> Received { get { lock (_received) return _received.ToList(); } }
        /// <summary>接受的连接数（建链次数）。</summary>
        public int AcceptCount => Volatile.Read(ref _acceptCount);
        /// <summary>收到的 STATUS-REQUEST 探测次数。</summary>
        public int ProbeCount
        {
            get { lock (_received) return _received.Count(l => l.StartsWith("STATUS-REQUEST", StringComparison.OrdinalIgnoreCase)); }
        }

        /// <summary>等待某条命令被服务端收到（超时抛异常）。</summary>
        public async Task WaitForReceived(string line, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (Received.Contains(line)) return;
                await Task.Delay(50);
            }
            throw new TimeoutException($"server did not receive '{line}' within {timeout}");
        }

        public FakeAptioServer(Func<int, string, string?> responder, TimeSpan? replyDelay = null, int port = 0)
        {
            _responder = responder;
            _replyDelay = replyDelay ?? TimeSpan.Zero;
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Start();
        }

        /// <summary>换到指定端口（测试里客户端正在重试该端口）。</summary>
        public void MoveTo(int port)
        {
            _listener.Stop();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = port;
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
                    Interlocked.Increment(ref _acceptCount);
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
                        if (_replyDelay > TimeSpan.Zero)
                            await Task.Delay(_replyDelay);      // 模拟慢链路（STATUS 回包延迟）
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
