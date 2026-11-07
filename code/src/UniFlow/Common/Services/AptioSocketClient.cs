using System.Collections.Concurrent;
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

        /// <summary>1 = 已超时/已取消（**排队中也不得再发送**，避免陈旧重放）。</summary>
        public int Expired;

        /// <summary>入队时刻（观测用；与文件内其它时间戳一致用本地时间 DateTime.Now）。</summary>
        public DateTime EnqueuedUtc { get; } = DateTime.Now;

        public SocketRequest(string command, CancellationToken ct)
        {
            Command = command;
            Ct = ct;
        }
    }

    private readonly ILogger<AptioSocketClient> _logger;
    private readonly Channel<SocketRequest> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    // 已投递但尚未拿到结果的请求（入队即登记）。
    // 停止时必须逐个结算，否则队列里没人再消费的请求会一直挂到 CommandTimeout（默认 1h）。
    private readonly ConcurrentDictionary<SocketRequest, byte> _pending = new();
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    // 连接+探测的单飞锁（避免后台预连接 / worker / 消费线程各开一条连接）
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private int _connectAttempts;
    private Task? _loopTask;

    // ===== 队列 / 超时 / 连接策略（可注入，便于测试）=====

    /// <summary>命令队列容量上限（满则等待，不丢命令）。</summary>
    public const int CommandQueueCapacity = 1000;

    /// <summary>
    /// 命令排队 + 等待响应的总超时，默认 1h。
    /// <para>超时后请求会被标记为"过期"：消费者取到后**直接丢弃、不发送**（防陈旧重放）。</para>
    /// </summary>
    private TimeSpan _commandTimeout = TimeSpan.FromHours(1);
    public TimeSpan CommandTimeout
    {
        get => _commandTimeout;
        set => _commandTimeout = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value;
    }

    /// <summary>单次连接尝试（含梯度重试）的总超时，默认 120s —— 消费者最多被一条命令卡住这么久。</summary>
    private TimeSpan _connectTotalTimeout = TimeSpan.FromSeconds(120);
    public TimeSpan ConnectTotalTimeout
    {
        get => _connectTotalTimeout;
        set => _connectTotalTimeout = value <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : value;
    }

    /// <summary>连接失败后的冷却期，默认 30s：期间新的连接请求**立即失败**（不阻塞消费者）。</summary>
    private TimeSpan _connectFailureCooldown = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectFailureCooldown
    {
        get => _connectFailureCooldown;
        set => _connectFailureCooldown = value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    /// <summary>
    /// **入队等待**超时（队列满时等不到消费者），默认 30s。
    /// <para>与 <see cref="CommandTimeout"/>（等待结果，1h）分开：入队卡住会让 worker 静默停摆、
    /// health 不更新，所以必须短；超时后命令**未入队**，调用方按"未投递"自行重试。</para>
    /// </summary>
    private TimeSpan _enqueueTimeout = TimeSpan.FromSeconds(30);
    public TimeSpan EnqueueTimeout
    {
        get => _enqueueTimeout;
        set => _enqueueTimeout = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value;
    }

    /// <summary>
    /// 命令在队列中的**最大年龄**，默认 120s。
    /// <para>超过该年龄的请求即使还没被消费者取走，也直接丢弃不发送 ——
    /// 这样"陈旧重放"窗口从 1h 收敛到 120s（已进入写 socket 阶段的请求仍无法撤回）。</para>
    /// </summary>
    private TimeSpan _maxQueuedAge = TimeSpan.FromSeconds(120);
    public TimeSpan MaxQueuedAge
    {
        get => _maxQueuedAge;
        set => _maxQueuedAge = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value;
    }

    // 上次连接失败时刻（冷却期起点）；连接成功后清零
    private DateTime _lastConnectFailureUtc = DateTime.MinValue;
    // 冷却告警节流（避免每条命令刷屏）
    private DateTime _lastCooldownLogUtc = DateTime.MinValue;
    // 显式连接状态（不依赖 TcpClient.Connected —— 它只反映上次 I/O）
    private volatile bool _connected;
    // 连接代际：每次"外部断开/内部失败断开" +1。
    // 探测期间若代际变化，说明连接已被拆掉 → 探测结果作废，不得置 _connected=true。
    private int _connectionGeneration;

    /// <summary>
    /// 「已投递」标记：非 STATUS-REQUEST 命令写+Flush 成功后返回。
    /// <para>
    /// 依据厂商手册 DCAU-H00.816.12.01.02.pdf 第 498 页 §8.59（/tmp/dcau.txt:24869-24881）：
    /// ACK/NAK 是 <b>Dream→GUI 的通知</b>，只对应 ADVANCED-CONFIG-CHANGE、CLEAR-TUBES、
    /// COMMENT I021~I024 这 6 条 GUI 命令；§9.3（/tmp/dcau.txt:26761-26780）COMMENT S002 无任何回复定义。
    /// 因此 Host LIS 发出的 COMMENT S002 / ORDER <b>没有 ACK</b>，正确语义是「写成功 = 已投递」。
    /// </para>
    /// </summary>
    public const string DeliveredMarker = "OK:SENT";

    // 连续拿到无效 STATUS 回复（null 或非 STATUS 开头）达到该次数 → 判定半开连接，强制断开重建
    private const int StatusMissThreshold = 2;

    // STATUS-REQUEST 连续未命中计数（收到有效 STATUS 回复即归零）
    private int _statusMisses;

    // 发送失败自动重发次数（命令在重连后原样重发，如 ORDER…C 取消类幂等命令安全）
    private const int SendRetryCount = 3;
    // 空闲超过该时长即视为可能"半开"连接，发送前主动重建（避免写已断连接撞 WSAECONNABORTED）
    private static readonly TimeSpan IdleReconnectThreshold = TimeSpan.FromSeconds(30);
    // 注：自 W1 起非 STATUS 命令写成功即返回 DeliveredMarker（fire-and-forget），不再有 1s 等待窗口
    // STATUS-REQUEST 是唯一期待回复的命令（Aptio 回 STATUS 状态行），给它更长的等待窗口
    // （实例属性：测试可缩短，避免集成测试等待 10s；写入时钳制到 [1s,60s]，见下方 setter）
    private TimeSpan _statusResponseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// STATUS-REQUEST 期待回复的等待窗口，默认 10s。
    /// 写入时钳制到 [1s, 60s]：防止误设 0/负值导致每次 STATUS 立刻未命中 → 连续 2 次即强制断开（重连风暴）。
    /// </summary>
    public TimeSpan StatusResponseTimeout
    {
        get => _statusResponseTimeout;
        set => _statusResponseTimeout = value < MinStatusResponseTimeout
            ? MinStatusResponseTimeout
            : value > MaxStatusResponseTimeout ? MaxStatusResponseTimeout : value;
    }

    private static readonly TimeSpan MinStatusResponseTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxStatusResponseTimeout = TimeSpan.FromSeconds(60);
    // 重连成功后 STATUS-REQUEST 探测等待窗口（验证通讯是否真正恢复，Aptio 无 ACK）
    private TimeSpan _probeResponseTimeout = TimeSpan.FromSeconds(5);
    public TimeSpan ProbeResponseTimeout
    {
        get => _probeResponseTimeout;
        set => _probeResponseTimeout = value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : value;
    }
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    // 重试梯度：3s×10 → 30s×10 → 60s
    private const int QuickAttempts = 10;
    private const int MediumAttempts = 20;

    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2055;

    /// <summary>
    /// 连接状态：**仅在连接建立且 STATUS 探测成功后**为 true；
    /// 断开 / 连接失败 / 写失败后为 false。
    /// <para>同时要求 writer 仍在（避免 "_connected=true 但 _writer==null" 的不一致组合）。</para>
    /// 半开检测仍靠 idle&gt;30s 与 STATUS 连续 2 次未命中。
    /// </summary>
    public bool Connected => _connected && Volatile.Read(ref _writer) != null;

    public AptioSocketClient(ILogger<AptioSocketClient> logger, IOptions<AptioConfig> aptioConfig)
        : this(logger, aptioConfig, CommandQueueCapacity)
    {
    }

    /// <summary>测试可注入更小的队列容量（生产用默认 1000）。</summary>
    public AptioSocketClient(ILogger<AptioSocketClient> logger, IOptions<AptioConfig> aptioConfig, int queueCapacity)
    {
        _logger = logger;
        Host = aptioConfig.Value.Ip;
        Port = aptioConfig.Value.Port;
        // 有界队列：满则等待（不丢命令），等待时长由调用方 ct / CommandTimeout 控制
        _queue = Channel.CreateBounded<SocketRequest>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    // ===== IHostedService：启动消费循环 + 预连接 =====

    public Task StartAsync(CancellationToken ct)
    {
        _loopTask = Task.Run(() => ProcessLoopAsync(_shutdown.Token), _shutdown.Token);
        // 预连接：**持续重试**（不因一次预算到期就永久退出）
        //   预算到期 → EnsureConnectedAsync 抛 TimeoutException 且已进入冷却；
        //   这里等满冷却再试下一轮 → 端口/对端恢复后自动连上（命令路径之外还有"后台自愈"）。
        _ = Task.Run(async () =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await EnsureConnectedAsync(_shutdown.Token);
                    return;      // 连上了 → 结束预连接任务
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (OperationCanceledException)
                {
                    // 预算到期（未转成 TimeoutException 的兜底）→ 继续下一轮
                    _logger.LogDebug("Aptio pre-connect attempt canceled — will retry");
                }
                catch (TimeoutException ex)
                {
                    // 冷却 / 连接总超时 → 等冷却后继续（不退出）
                    _logger.LogDebug("Aptio pre-connect not ready ({Msg}) — retrying after cooldown", ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Aptio pre-connect attempt failed: {Msg}", ex.Message);
                }
                // 冷却后重试（冷却期内 EnsureConnectedAsync 会立即失败，故这里等满冷却）
                try { await Task.Delay(ConnectFailureCooldown, _shutdown.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, _shutdown.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _queue.Writer.TryComplete();
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        DisconnectInternal();
        SettlePendingRequests("client stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 结算所有未完成的请求：给每个 pending 请求的 TCS 一个结果（null = 未投递），
    /// 调用方立刻返回，不必干等到 CommandTimeout（默认 1h）。
    /// </summary>
    private void SettlePendingRequests(string reason)
    {
        var count = 0;
        foreach (var req in _pending.Keys)
        {
            if (_pending.TryRemove(req, out _))
            {
                // TrySetResult(null)：语义 = "未投递"，调用方按未投递处理（与超时路径一致）
                if (req.Tcs.TrySetResult(null)) count++;
            }
        }
        if (count > 0)
            _logger.LogWarning("Aptio {Reason}: settled {Count} pending command(s) (not delivered)", reason, count);
    }

    // ===== 对外命令接口 =====

    public async Task<string?> SendAsync(string command, CancellationToken ct = default)
    {
        var req = new SocketRequest(command, ct);
        // 登记为 pending（入队前登记）：StopAsync 时必须能把"还没入队/还没被消费"的请求一并结算
        _pending[req] = 0;
        try
        {
            // ① 入队：有界队列满时等待，等待受 caller ct + EnqueueTimeout 约束（不丢命令）
            //    注意：入队等待用**独立的短超时**（默认 30s），不用 1h 的 CommandTimeout ——
            //    否则断链时 worker 会静默卡住 1h、health 也不更新。
            using (var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                writeCts.CancelAfter(EnqueueTimeout);
                try
                {
                    await _queue.Writer.WriteAsync(req, writeCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 入队超时（队列满且迟迟等不到消费者）→ 标记过期，绝不入队 → 调用方按"未投递"重试
                    MarkExpired(req);
                    _logger.LogWarning("Command queue is full (capacity {Cap}), enqueue timed out after {Timeout} — command dropped: {Cmd}",
                        CommandQueueCapacity, EnqueueTimeout, Sanitize(command));
                    return null;
                }
                catch (OperationCanceledException)
                {
                    MarkExpired(req);
                    _logger.LogDebug("Command canceled before enqueue: {Cmd}", Sanitize(command));
                    throw;
                }
                catch (ChannelClosedException)
                {
                    // 客户端已停止（队列已关闭）→ 未投递
                    MarkExpired(req);
                    _logger.LogWarning("Command not enqueued — client is stopping: {Cmd}", Sanitize(command));
                    return null;
                }
            }

            // ② 等结果
            try
            {
                return await req.Tcs.Task.WaitAsync(CommandTimeout, ct);
            }
            catch (TimeoutException) when (!req.Tcs.Task.IsCompleted)
            {
                // 真正的"等待超时"（消费者还没给结果）→ 标记过期 → 消费者取到后直接丢弃，不再发送（防陈旧重放）
                MarkExpired(req);
                _logger.LogWarning("Command timed out after {Timeout}, dropped (will NOT be sent): {Cmd}",
                    CommandTimeout, Sanitize(command));
                return null;
            }
            catch (TimeoutException)
            {
                // 消费者返回的 TimeoutException（连接冷却 / 连接总超时 / 命令预算到期）→ 原样抛出，
                // 让调用方知道"没发出去"的原因（工作线程会记录 Error 并在下一轮重试）
                throw;
            }
            catch (OperationCanceledException)
            {
                MarkExpired(req);
                _logger.LogDebug("Command canceled: {Cmd}", Sanitize(command));
                throw;
            }
        }
        finally
        {
            // 请求已结算（正常完成 / 超时 / 取消 / 停止）→ 不再是 pending
            _pending.TryRemove(req, out _);
        }
    }

    // 标记请求为"已过期"（消费者取到后直接丢弃，不发送）
    private static void MarkExpired(SocketRequest req) => Interlocked.Exchange(ref req.Expired, 1);

    /// <summary>
    /// 建立连接（供 worker / 业务服务显式调用）。
    /// <para>
    /// **与内部发送走同一套单飞 + 探测流程**（<see cref="EnsureConnectedAsync"/>）：
    /// 只建 TCP 不探测的旧实现会拆掉后台/消费者正在探测的连接，
    /// 在"STATUS 回包慢于 worker 轮询周期"时会导致连接永远建不起来（探测被反复打断）。
    /// 现在：已连接且探测通过 → 立即返回；否则等待/参与同一次连接+探测，不重复探测、不拆在用连接。
    /// </para>
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default) => EnsureConnectedAsync(ct);

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

                // 已超时/已取消的请求：**绝不发送**（否则会把很久以前排队的命令陈旧重放）
                if (Volatile.Read(ref req.Expired) != 0)
                {
                    req.Tcs.TrySetResult(null);
                    _logger.LogDebug("Expired command dropped (not sent): {Cmd} (queued {Age}s)",
                        Sanitize(req.Command), (int)(DateTime.Now - req.EnqueuedUtc).TotalSeconds);
                    continue;
                }

                // 队列年龄上限：排队太久（连接不通/消费者卡住）的命令直接丢弃，不发送
                // （把"陈旧重放"窗口从 1h 收敛到 MaxQueuedAge）
                var queuedAge = DateTime.Now - req.EnqueuedUtc;
                if (queuedAge > MaxQueuedAge)
                {
                    MarkExpired(req);
                    req.Tcs.TrySetResult(null);
                    _logger.LogWarning("Command too old in queue ({Age}s > {Max}s), dropped (not sent): {Cmd}",
                        (int)queuedAge.TotalSeconds, (int)MaxQueuedAge.TotalSeconds, Sanitize(req.Command));
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
        // 退出时把队列里剩下的请求全部结算（否则调用方会一直等到 CommandTimeout，默认 1h）
        while (_queue.Reader.TryRead(out var left))
        {
            _pending.TryRemove(left, out _);
            left.Tcs.TrySetResult(null);
        }
        _logger.LogInformation("AptioSocketClient command loop stopped");
    }

    // 确保连接，梯度重试直至成功或取消
    // 注意：Aptio 协议不回 ACK，TCP 连上 ≠ 通讯可用。
    // 每次新连接建立后发 STATUS-REQUEST 1 探测，收到 STATUS 回复才判定连接有效；
    // 探测失败则断开并按梯度重连（避免在"半死"连接上盲目发送业务命令）。
    //
    // P1-3 三重保护：
    //   ① 冷却期：上次连接失败后 30s 内的新请求**立即抛 TimeoutException**（不阻塞消费者）；
    //   ② 总超时：整次连接尝试（含梯度重试）最多 ConnectTotalTimeout(120s)；
    //   ③ 失败记录：连接失败即进入冷却，避免"每条命令都去撞一次坏连接"。
    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        // 已连接且探测通过 → 无需再连
        if (Connected) return;

        // ① 冷却期检查（快速失败，不阻塞调用方）
        ThrowIfInCooldown();

        // ② 总超时预算
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ConnectTotalTimeout);
        var token = budget.Token;

        // ③ 尝试循环：每次"连接 + 探测"是一个**原子尝试**（单飞），尝试之间释放锁。
        //    单飞的原因：后台预连接任务与消费线程/worker 若各自连接，会开出第二条连接 ——
        //    后台那条的探测回复被丢弃、消费线程那条又再发一次探测，
        //    造成服务端收到额外 STATUS-REQUEST（协议流量污染 + 半开误判）。
        //    尝试之间释放锁：让等待者能抢到锁、看到冷却并快速失败（而不是被长退避拖住）。
        //
        // 预算到期收口（P1-1/P2-1）：裸 OCE 不能逸出 ——
        //   · 必须记入冷却（否则黑洞对端下冷却永不进入，调用方反复等满总超时）；
        //   · 必须转成 TimeoutException（调用方日志/语义一致，见文档 §10.3）。
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Connected) { _lastConnectFailureUtc = DateTime.MinValue; return; }

                var gen = Volatile.Read(ref _connectionGeneration);
                var delay = TimeSpan.Zero;
                string? failReason = null;

                await _ensureLock.WaitAsync(token);
                try
                {
                    // 等锁期间可能已由别人连好
                    if (Connected) return;

                    await ConnectCoreAsync(token);   // 只建 TCP（已在单飞锁内，无需 _connectLock）
                    if (_connectAttempts > 0)
                        _logger.LogInformation("Aptio reconnected after {Attempts} attempts", _connectAttempts);
                    _connectAttempts = 0;

                    // 连接验证：STATUS-REQUEST 探测（Aptio 唯一会回复的命令）
                    if (!await ProbeConnectionAsync(token))
                    {
                        _connectAttempts++;
                        _lastConnectFailureUtc = DateTime.UtcNow;   // 探测失败也算失败 → 进冷却
                        delay = GetRetryDelay(_connectAttempts);
                        failReason = $"STATUS probe failed (attempt {_connectAttempts})";
                        _logger.LogWarning("Aptio connection established but STATUS probe failed (attempt {Attempts}), retry in {Delay}s",
                            _connectAttempts, (int)delay.TotalSeconds);
                        DisconnectInternal();
                    }
                    else if (gen != Volatile.Read(ref _connectionGeneration) || Volatile.Read(ref _writer) == null)
                    {
                        // 连接在探测期间被拆掉/替换 → 探测结果作废。
                        // 说明：这是**双保险**——`_writer` 判空有独立用例覆盖；
                        // 连接代际比较目前**没有**独立的负向用例（单独去掉它所有用例仍绿），
                        // 它只是针对"探测期间连接被换掉"这一窄窗口的额外保险，保留但不宣称有闸门。
                        _logger.LogDebug("Aptio connection was replaced during probe — discarding probe result and retrying");
                        DisconnectInternal();
                        _connectAttempts++;
                        delay = GetRetryDelay(_connectAttempts);
                        failReason = "connection replaced during probe";
                    }
                    else
                    {
                        // 连通且探测成功 → 清零失败时刻（冷却解除）
                        _lastConnectFailureUtc = DateTime.MinValue;
                        _connected = true;
                        return;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _connectAttempts++;
                    _lastConnectFailureUtc = DateTime.UtcNow;       // 失败 → 进冷却
                    delay = GetRetryDelay(_connectAttempts);
                    failReason = ex.Message;
                    if (_connectAttempts <= 3 || _connectAttempts % 10 == 1)
                        _logger.LogWarning("Aptio connect failed (attempt {Attempts}): {Msg}, retry in {Delay}s",
                            _connectAttempts, ex.Message, (int)delay.TotalSeconds);
                    DisconnectInternal();
                }
                finally { _ensureLock.Release(); }

                // 失败原因（此前只赋值不读取的死变量）：并入退避日志，便于现场定位
                if (!string.IsNullOrEmpty(failReason))
                    _logger.LogDebug("Aptio connect attempt failed: {Reason}, retry in {Delay}s", failReason, (int)delay.TotalSeconds);

                // 退避在**锁外**：等待者可在此期间抢到锁并快速失败
                if (delay > TimeSpan.Zero)
                {
                    try { await Task.Delay(delay, token); }
                    catch (OperationCanceledException) { throw; }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // 关机取消：原样抛出
        }
        catch (OperationCanceledException)
        {
            // 预算到期（连接总超时）→ 记入冷却 + 转 TimeoutException
            _lastConnectFailureUtc = DateTime.UtcNow;
            _logger.LogWarning("Aptio connect exceeded total timeout {Timeout}s — giving up this round",
                (int)ConnectTotalTimeout.TotalSeconds);
            throw new TimeoutException($"Aptio connect exceeded total timeout ({ConnectTotalTimeout})");
        }
    }

    // 冷却期检查：距上次连接失败 < 冷却期 → 立即失败（不阻塞调用方）
    private void ThrowIfInCooldown()
    {
        if (_lastConnectFailureUtc == DateTime.MinValue) return;
        var since = DateTime.UtcNow - _lastConnectFailureUtc;
        if (since < ConnectFailureCooldown)
        {
            var left = ConnectFailureCooldown - since;
            LogCooldown(left);
            throw new TimeoutException("Aptio connect cooldown");
        }
    }

    // 冷却告警降噪：每个冷却窗口只打一条
    private void LogCooldown(TimeSpan left)
    {
        var now = DateTime.UtcNow;
        if (_lastCooldownLogUtc != DateTime.MinValue && now - _lastCooldownLogUtc < ConnectFailureCooldown)
            return;
        _lastCooldownLogUtc = now;
        _logger.LogWarning("Aptio connect cooldown active ({Left}s left) — failing fast instead of blocking",
            (int)left.TotalSeconds);
    }

    // 发送 STATUS-REQUEST 1 并等待 Aptio 的状态回复；收到非空回复即认为通讯正常。
    private async Task<bool> ProbeConnectionAsync(CancellationToken ct)
    {
        if (_writer == null || _reader == null)
            return false;
        try
        {
            await _writer!.WriteAsync("STATUS-REQUEST 1\r\n".AsMemory(), ct);
            await _writer!.FlushAsync(ct);
            _lastActivityUtc = DateTime.UtcNow;
            _logger.LogDebug(">> STATUS-REQUEST 1 (probe)");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeResponseTimeout);
            var response = await _reader!.ReadLineAsync(timeout.Token);
            _lastActivityUtc = DateTime.UtcNow;
            if (response != null)
            {
                _logger.LogDebug("<< {Resp} (probe)", Sanitize(response));
                return response.StartsWith("STATUS", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("STATUS probe timed out");
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug("STATUS probe failed: {Msg}", ex.Message);
            return false;
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
        // 单条命令的总预算：连接（含重连 + 梯度重试）不得超过 ConnectTotalTimeout，
        // 否则一条命令就能把单消费者卡住 2 分钟以上（队列堆积）。
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ConnectTotalTimeout);
        var token = budget.Token;

        Exception? lastError = null;
        try
        {
            for (var attempt = 0; attempt <= SendRetryCount; attempt++)
            {
                token.ThrowIfCancellationRequested();

                // 发送前确保已连接（失败/超时会以 TimeoutException 抛出 → 交给 ProcessLoopAsync）
                await EnsureConnectedAsync(token);

                if (!_connected || _writer == null || _reader == null)
                    throw new InvalidOperationException("Not connected");

                // 方案2：空闲过久 → 主动重建连接，避免对端已关闭但本地仍以为在线（半开连接）
                var idle = DateTime.UtcNow - _lastActivityUtc;
                if (idle > IdleReconnectThreshold)
                {
                    _logger.LogDebug("Aptio connection idle for {Idle}s, refreshing before send", (int)idle.TotalSeconds);
                    DisconnectInternal();
                    await EnsureConnectedAsync(token);
                    if (!_connected || _writer == null || _reader == null)
                        throw new InvalidOperationException("Not connected after idle refresh");
                }

                // 发送（Aptio 协议为 fire-and-forget：成功=写入对端，不等待 ACK）
                try
                {
                    await _writer!.WriteAsync(command.AsMemory(), token);
                    await _writer!.WriteAsync("\r\n".AsMemory(), token);
                    await _writer!.FlushAsync(token);
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
                    try { await Task.Delay(TimeSpan.FromMilliseconds(500), token); }
                    catch (OperationCanceledException) { throw; }
                    continue;
                }

                // 命令已成功写入。Aptio 协议为 fire-and-forget：COMMENT/ORDER 等命令写入成功即已投递，
                // Dream 不回复（手册 §8.59：ACK/NAK 仅为 Dream→GUI 通知，只覆盖 6 条 GUI 命令，与 Host LIS 的
                // COMMENT S002 / ORDER 无关；§9.3：COMMENT S002 无回复定义），
                // 故返回明确的「已投递」标记 DeliveredMarker（而非 null）。
                // 仅 STATUS-REQUEST 期待 Aptio 状态回复，需要读取。
                if (!command.StartsWith("STATUS-REQUEST", StringComparison.OrdinalIgnoreCase))
                    return DeliveredMarker;
                return await HandleStatusResponseAsync(token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            // 本条命令的总预算到期 → 转成 TimeoutException（调用方按"超时"处理，队列继续推进）
            throw new TimeoutException($"Aptio command exceeded the {ConnectTotalTimeout} budget");
        }
        throw new InvalidOperationException($"Aptio send failed after {SendRetryCount + 1} attempts: {lastError?.Message}", lastError);
    }

    // STATUS-REQUEST 回复处理：连续拿不到有效 STATUS 回复时判定「半开连接」，强制断开并重建。
    // 判据：回复为 null（超时/读失败）或不以 STATUS 开头 → 连续计数 +1；
    //       连续 >= StatusMissThreshold 次 → DisconnectInternal() + 计数归零 + LogWarning；
    //       收到有效 STATUS 回复 → 计数归零。
    private async Task<string?> HandleStatusResponseAsync(CancellationToken ct)
    {
        var response = await TryReadResponseAsync(ct);

        if (response != null && response.StartsWith("STATUS", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _statusMisses, 0);
            return response;
        }

        var misses = Interlocked.Increment(ref _statusMisses);
        if (misses >= StatusMissThreshold)
        {
            _logger.LogWarning(
                "Aptio STATUS-REQUEST missed {Misses} times in a row (last: {Resp}), forcing disconnect (half-open connection suspected)",
                misses, Sanitize(response ?? "<null>"));
            Interlocked.Exchange(ref _statusMisses, 0);
            DisconnectInternal();
        }
        else
        {
            _logger.LogWarning("Aptio STATUS-REQUEST missed {Misses}/{Max} (last: {Resp})",
                misses, StatusMissThreshold, Sanitize(response ?? "<null>"));
        }
        return response;
    }

    // fire-and-forget 命令（COMMENT/ORDER 等）写入成功即返回 DeliveredMarker，不再等待任何回复；
    // 唯一会调用本方法的是 STATUS-REQUEST（期待 Aptio 的 STATUS 状态回复）。
    private async Task<string?> TryReadResponseAsync(CancellationToken ct)
    {
        if (_reader == null) return null;
        try
        {
            var wait = StatusResponseTimeout;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            var response = await _reader.ReadLineAsync(timeout.Token);
            _lastActivityUtc = DateTime.UtcNow;
            if (response != null)
                _logger.LogDebug("<< {Resp}", Sanitize(response));
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 无响应 = 正常（Aptio fire-and-forget）；STATUS 路径由调用方按「未命中」处理
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
        DisposeSocket();          // 只释放旧 socket，不动代际（代际只表示"被谁拆掉了"）
        _tcpClient = new TcpClient();
        // ct 必须贯穿连接全过程（总预算由调用方 CancelAfter 注入）
        await _tcpClient.ConnectAsync(Host, Port, ct);
        _stream = _tcpClient.GetStream();
        _stream.ReadTimeout = 10000;
        _stream.WriteTimeout = 5000;
        _reader = new StreamReader(_stream, Encoding.ASCII);
        _writer = new StreamWriter(_stream, Encoding.ASCII) { NewLine = "\r\n" };
        // 注意：**不**在此置 _connected=true —— 只有探测成功后才算"通讯可用"
        _logger.LogInformation("Connected to Aptio {Host}:{Port}", Host, Port);
    }

    // 仅释放 socket（不推进代际）：连接重建时用它，避免把"自己重连"当成"被外部拆掉"
    private void DisposeSocket()
    {
        _connected = false;
        _writer?.Dispose(); _reader?.Dispose(); _stream?.Dispose(); _tcpClient?.Dispose();
        _writer = null; _reader = null; _stream = null; _tcpClient = null;
    }

    // 外部断开 / 内部失败断开：推进连接代际（让在途探测结果作废）
    private void DisconnectInternal()
    {
        Interlocked.Increment(ref _connectionGeneration);
        DisposeSocket();
    }

    private static string Sanitize(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        DisconnectInternal();
        SettlePendingRequests("client disposed");
        _ensureLock.Dispose();
        _shutdown.Dispose();
    }
}