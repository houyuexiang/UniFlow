using System.Collections.Concurrent;
using System.Threading.Channels;
using UniFlow.DisposeSample.Models;

namespace UniFlow.DisposeSample.Services;

public class AptioTaskRegistration
{
    public string Name { get; init; } = "";
    public string TestName { get; init; } = "";
    public Func<string, bool> Matcher { get; init; } = _ => false;
    public Channel<SampleRecord> Queue { get; init; } = default!;
    public Func<bool> Enabled { get; init; } = () => true;
}

public class AptioTaskRouter
{
    private readonly ILogger<AptioTaskRouter> _logger;
    private readonly ConcurrentDictionary<string, AptioTaskRegistration> _registrations = new();
    // 去重：记录 (注册者名|barcode)。样本只要仍出现在扫描结果中就视为"已安排"，
    // 不重复推送；样本从扫描结果消失（Aptio 已处理）后清理。
    // PushTtl 为兜底重试期（命令可能因长时间中断而失败）。
    private readonly ConcurrentDictionary<string, DateTime> _pushed = new();
    private static readonly TimeSpan PushTtl = TimeSpan.FromHours(1);

    public AptioTaskRouter(ILogger<AptioTaskRouter> logger)
    {
        _logger = logger;
    }

    public void Register(string name, string testName, Func<string, bool> matcher, Channel<SampleRecord> queue, Func<bool> enabled)
    {
        var reg = new AptioTaskRegistration
        {
            Name = name,
            TestName = testName,
            Matcher = matcher,
            Queue = queue,
            Enabled = enabled
        };
        if (_registrations.TryAdd(name, reg))
            _logger.LogInformation("Task registered: {Name} (testName={TestName})", name, testName);
        else
        {
            _registrations[name] = reg;
            _logger.LogWarning("Task {Name} already registered, updated (testName={TestName})", name, testName);
        }
    }

    public void Unregister(string name)
    {
        if (_registrations.TryRemove(name, out _))
            _logger.LogInformation("Task unregistered: {Name}", name);
    }

    // 各注册者测试名的并集（非空），用于 SQL 初筛
    public IReadOnlyCollection<string> GetCandidateTestNames()
    {
        return _registrations.Values
            .Select(r => r.TestName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();
    }

    // 逐样本逐注册者匹配；已安排（仍出现在扫描结果中）的任务不重复推送。
    // maxPush：单轮最多推送的新任务数（超出下轮继续，不会丢失）。
    public async Task DispatchAsync(IReadOnlyCollection<SampleRecord> samples, int maxPush, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var pushed = 0;
        var skipped = 0;

        foreach (var s in samples)
        {
            var testNames = ExtractTestNames(s.TestName);
            if (testNames.Count == 0) continue;

            foreach (var reg in _registrations.Values)
            {
                if (!reg.Enabled()) continue;
                if (!testNames.Any(reg.Matcher)) continue;

                var key = $"{reg.Name}|{s.Barcode}";
                currentKeys.Add(key);

                // 本轮达到上限则只登记不推送，下轮继续
                if (maxPush > 0 && pushed >= maxPush) continue;
                // 已安排且未超过兜底重试期 → 不重复推送
                if (_pushed.TryGetValue(key, out var last) && now - last < PushTtl) { skipped++; continue; }

                _pushed[key] = now;
                await reg.Queue.Writer.WriteAsync(s, ct);
                pushed++;
            }
        }

        // 清理：样本已从扫描结果消失（Aptio 已处理），移除记录以便未来同号新样本可再推送
        foreach (var key in _pushed.Keys)
        {
            if (!currentKeys.Contains(key))
                _pushed.TryRemove(key, out _);
        }

        if (pushed > 0)
            _logger.LogInformation("Router pushed {Pushed} tasks, skipped {Skipped} scheduled (scanned {Scanned})",
                pushed, skipped, samples.Count);
        else if (skipped > 0)
            _logger.LogDebug("Router: {Skipped} tasks already scheduled (scanned {Scanned})", skipped, samples.Count);
    }

    // 对齐原始逻辑：test 列以分号分隔，第 5 段为测试名；
    // test 列以 "7" 开头视为已处理，跳过
    // TestName 字段为 CONCAT_WS('^', test_1..test_80) 拼接，^ 分隔各列
    public static HashSet<string> ExtractTestNames(string? tests)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(tests)) return result;

        foreach (var col in tests.Split('^'))
        {
            var test = col;
            if (string.IsNullOrEmpty(test) || test.Length <= 1) continue;
            if (test.StartsWith("7")) continue;
            var parts = test.Split(';');
            if (parts.Length > 4)
            {
                var name = parts[4].Trim();
                if (name.Length > 0) result.Add(name);
            }
        }
        return result;
    }
}