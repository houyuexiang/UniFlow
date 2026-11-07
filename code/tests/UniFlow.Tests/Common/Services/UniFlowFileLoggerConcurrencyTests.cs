using Microsoft.Extensions.Logging;
using Shouldly;
using UniFlow.Common.Services.Logging;

namespace UniFlow.Tests.Common.Services;

/// <summary>
/// M-03：<c>UniFlowFileLoggerProvider</c> 的并发写入用例。
/// <para>
/// 历史缺陷：所有模块日志共享 provider 的**一把全局锁</c>，且每行 <c>Flush()</c>
/// → 高日志量时任意模块的写入都会阻塞其它模块（串行化 + 落盘放大）。
/// 现在：<c>LogFileState</c> 自带 <c>Lock</c>，模块日志 / 错误日志各锁各的状态。
/// </para>
/// <para>验收：多线程写不同模块 → <b>不丢行</b>（每行都落盘）且<b>不串文件</b>（各模块只进自己的文件）。</para>
/// </summary>
public class UniFlowFileLoggerConcurrencyTests : IDisposable
{
    private readonly string _logDir;
    private readonly UniFlowFileLoggerProvider _provider;

    public UniFlowFileLoggerConcurrencyTests()
    {
        // 绝对路径 → Path.Combine(baseDir, 绝对路径) 返回绝对路径（测试隔离，不污染 bin/logs）
        _logDir = Path.Combine(Path.GetTempPath(), "uniflow-logtest-" + Guid.NewGuid().ToString("N"));
        _provider = new UniFlowFileLoggerProvider(new UniFlowLoggerConfig
        {
            FileLoggingEnabled = true,
            LogDirectory = _logDir,
            MaxFileSizeMb = 10,
            RetentionDays = 1,
            LogLevel = "Trace"
        });
    }

    /// <summary>取某模块当天目录下的所有日志文件内容（合并多行）。</summary>
    private List<string> ReadModuleLines(string module)
    {
        var dayDir = Path.Combine(_logDir, DateTime.Now.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dayDir)) return new List<string>();
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(dayDir, module + "_*.log"))
        {
            lines.AddRange(File.ReadAllLines(file));
        }
        return lines;
    }

    private List<string> ReadErrorLines()
    {
        var dayDir = Path.Combine(_logDir, DateTime.Now.ToString("yyyy-MM-dd"));
        if (!Directory.Exists(dayDir)) return new List<string>();
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(dayDir, "UniFlow_Error_*.log"))
        {
            lines.AddRange(File.ReadAllLines(file));
        }
        return lines;
    }

    [Fact]
    public void ConcurrentWrites_ToDifferentModules_LoseNoLines_AndDoNotCrossFiles()
    {
        // 4 个模块各写 250 行（每行带唯一标记）
        var modules = new[] { "DisposeSample", "DmsPitStop", "SrmExport", "WorkListCleaner" };
        const int linesPerModule = 250;

        Parallel.ForEach(modules, module =>
        {
            var logger = _provider.CreateLogger("UniFlow." + module + ".Worker");
            for (var i = 0; i < linesPerModule; i++)
            {
                logger.LogInformation("marker {Marker} from {Module} seq {Seq}", module + "-" + i, module, i);
            }
        });

        // 不丢行：每个模块文件里恰好 linesPerModule 行
        foreach (var module in modules)
        {
            var lines = ReadModuleLines(module);
            lines.Count.ShouldBe(linesPerModule, $"模块 {module} 的日志行数不对（丢行？）");
        }

        // 不串文件：每个模块文件里只含本模块的标记
        foreach (var module in modules)
        {
            var lines = ReadModuleLines(module);
            foreach (var line in lines)
            {
                line.ShouldContain("from " + module + " ");
                foreach (var other in modules.Where(m => m != module))
                {
                    line.ShouldNotContain("from " + other + " ");
                }
            }
        }
    }

    [Fact]
    public void ConcurrentWrites_ToSameModule_LoseNoLines()
    {
        // 同一模块多线程写：仍然一行不少（该模块自己的锁保证串行写入，不会写坏文件）
        const int threads = 8;
        const int linesPerThread = 200;

        Parallel.For(0, threads, t =>
        {
            var logger = _provider.CreateLogger("UniFlow.DisposeSample.Worker");
            for (var i = 0; i < linesPerThread; i++)
            {
                logger.LogInformation("same-module marker t{Thread} i{Index}", t, i);
            }
        });

        var lines = ReadModuleLines("DisposeSample");
        lines.Count.ShouldBe(threads * linesPerThread);
    }

    [Fact]
    public void ConcurrentErrorLogs_AreWrittenToErrorFile_AndModuleFile()
    {
        // Error 级日志同时写模块文件与错误文件；两者都不能丢行
        const int count = 150;

        Parallel.For(0, count, i =>
        {
            var logger = _provider.CreateLogger("UniFlow.DmsPitStop.Worker");
            logger.LogError("boom {Index}", i);
        });

        ReadModuleLines("DmsPitStop").Count.ShouldBe(count);
        ReadErrorLines().Count.ShouldBe(count);
    }

    [Fact]
    public void ModuleLocks_Are_PerFile_NotGlobal()
    {
        // 关键：证明"按文件状态独立锁"（M-03），而不是所有模块共用一把全局锁。
        // 手法：反射拿到各模块的 LogFileState → 锁住模块 A 的锁 →
        //       写模块 B 必须**不被阻塞**（旧实现用全局锁 → B 会被 A 阻塞住）。
        var loggerA = _provider.CreateLogger("UniFlow.LockA.Worker");
        var loggerB = _provider.CreateLogger("UniFlow.LockB.Worker");
        loggerA.LogInformation("warmup A");     // 建 A 的状态
        loggerB.LogInformation("warmup B");     // 建 B 的状态

        var moduleFiles = (System.Collections.Concurrent.ConcurrentDictionary<string, LogFileState>)
            typeof(UniFlowFileLoggerProvider)
                .GetField("_moduleFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(_provider)!;

        var stateA = moduleFiles["LockA"];
        var stateB = moduleFiles["LockB"];
        stateA.ShouldNotBeSameAs(stateB);
        // 每个状态有自己的锁对象（不是共享同一把）
        stateA.Lock.ShouldNotBeSameAs(stateB.Lock);

        // 锁住 A 的锁（由独立线程持有）→ 写 B 不应被阻塞，但写 A 必须被阻塞
        using var lockHeld = new ManualResetEventSlim(false);
        using var releaseLock = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            lock (stateA.Lock)
            {
                lockHeld.Set();
                releaseLock.Wait(TimeSpan.FromSeconds(30));
            }
        });
        lockHeld.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("未能取得模块 A 的锁");

        Task? writeA = null;
        try
        {
            // 写 B：不同文件 → 不应被 A 的锁阻塞
            var writeB = Task.Run(() => _provider.WriteModuleLog("LockB", "B line while A locked"));
            writeB.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("写模块 B 不应被模块 A 的锁阻塞（说明是按文件独立锁）");

            // 写 A：同一文件 → 必须被阻塞（证明锁确实生效，不是形同虚设）
            writeA = Task.Run(() => _provider.WriteModuleLog("LockA", "A line while A locked"));
            writeA.Wait(TimeSpan.FromMilliseconds(500)).ShouldBeFalse("写模块 A 必须被 A 自己的锁阻塞（否则锁没生效）");
        }
        finally
        {
            releaseLock.Set();
        }

        holder.Wait(TimeSpan.FromSeconds(10));
        // 释放锁之后 writeA 才真正拿到锁并写盘 —— 必须**等它写完**再读文件。
        // （旧实现只等 holder、不等 writeA → 并发全量跑时可能读到"还没写进去"的中间态 → 偶发红）
        writeA.ShouldNotBeNull();
        writeA!.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue("释放锁后写模块 A 必须完成");
        // 释放后 A 的写入完成（不丢行）
        ReadModuleLines("LockA").Count(e => e.Contains("A line while A locked")).ShouldBe(1);
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_logDir, true); } catch { /* ignore */ }
    }
}
