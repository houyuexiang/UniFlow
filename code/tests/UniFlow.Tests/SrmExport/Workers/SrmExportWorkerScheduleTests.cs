using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using UniFlow.SrmExport.Models;
using UniFlow.SrmExport.Services;
using UniFlow.SrmExport.Workers;
using UniFlow.WebAdmin.Services;

namespace UniFlow.Tests.SrmExport.Workers;

/// <summary>
/// SrmExport 每日导出的触发时机用例（P3）。
/// <para>
/// 历史缺陷：要求 <c>now.Hour==target.Hour &amp;&amp; now.Minute==target.Minute</c> 才导出，
/// 且"当天已导出"标志只在整点（00:00）复位 → 循环延迟/DB 慢/进程重启错过那一分钟 → 当天再也不导。
/// 现在：时间窗（now &gt;= target）+ 每日一次标记 → 错过整点当天仍补，重启后当天也补。
/// </para>
/// </summary>
public class SrmExportWorkerScheduleTests : IDisposable
{
    private static readonly TimeOnly Target = new(8, 30);

    [Theory]
    [InlineData(8, 29, false)]     // 还没到点
    [InlineData(8, 30, true)]      // 正好到点
    [InlineData(8, 31, true)]      // 错过整点（循环延迟/DB 慢）→ 仍要导
    [InlineData(9, 15, true)]      // 晚很多 → 仍要导
    [InlineData(23, 59, true)]     // 当天最后 → 仍要导
    public void ShouldExport_TimeWindow(int hour, int minute, bool expected)
    {
        var now = new DateTime(2026, 11, 7, hour, minute, 0);
        SrmExportWorker.ShouldExport(now, Target, lastExportDate: null).ShouldBe(expected);
    }

    [Fact]
    public void ShouldExport_AlreadyExportedToday_ReturnsFalse()
    {
        var now = new DateTime(2026, 11, 7, 9, 0, 0);
        SrmExportWorker.ShouldExport(now, Target, new DateTime(2026, 11, 7, 8, 30, 0)).ShouldBeFalse();
    }

    [Fact]
    public void ShouldExport_NextDay_ExportsAgain()
    {
        var now = new DateTime(2026, 11, 8, 9, 0, 0);
        SrmExportWorker.ShouldExport(now, Target, new DateTime(2026, 11, 7, 8, 30, 0)).ShouldBeTrue();
    }

    [Fact]
    public void ShouldExport_BeforeTargetTime_EvenIfNeverExported_ReturnsFalse()
    {
        var now = new DateTime(2026, 11, 7, 3, 0, 0);
        SrmExportWorker.ShouldExport(now, Target, lastExportDate: null).ShouldBeFalse();
    }

    // ---------- Worker 级：每天只导一次 / DB 不可用时补导 ----------

    private readonly IExportDatabaseService _db = Substitute.For<IExportDatabaseService>();
    private readonly IExportFileService _export = Substitute.For<IExportFileService>();
    private readonly HealthStore _health;
    private readonly string _healthDbPath;
    private readonly SrmExportWorker _worker;

    public SrmExportWorkerScheduleTests()
    {
        _healthDbPath = Path.Combine(Path.GetTempPath(), $"uniflow-test-{Guid.NewGuid():N}.db");
        _health = new HealthStore(_healthDbPath, NullLogger<HealthStore>.Instance);
        _db.PingAsync().Returns(Task.FromResult(true));
        _db.GetDisposedSamplesAsync(Arg.Any<string>())
            .Returns(Task.FromResult(new List<DisposedSample>()));
        _export.ExportAsync(Arg.Any<List<DisposedSample>>()).Returns(Task.FromResult("ok"));
        _worker = new SrmExportWorker(NullLogger<SrmExportWorker>.Instance, _db, _export, _health);
    }

    [Fact]
    public async Task TryExport_AfterTargetTime_ExportsOnce_PerDay()
    {
        var day = new DateTime(2026, 11, 7, 9, 0, 0);

        (await _worker.TryExportAsync(day, Target)).ShouldBeTrue();
        // 同一天再调 → 不再导出
        (await _worker.TryExportAsync(day.AddMinutes(1), Target)).ShouldBeFalse();
        (await _worker.TryExportAsync(day.AddHours(5), Target)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryExport_MissedExactMinute_StillExports()
    {
        // 目标 08:30，但这一轮循环发生在 08:31（延迟/DB 慢）→ 仍必须导出
        var now = new DateTime(2026, 11, 7, 8, 31, 0);
        (await _worker.TryExportAsync(now, Target)).ShouldBeTrue();
    }

    [Fact]
    public async Task TryExport_BeforeTargetTime_DoesNotExport()
    {
        var now = new DateTime(2026, 11, 7, 8, 29, 0);
        (await _worker.TryExportAsync(now, Target)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryExport_DbUnavailable_DoesNotMarkDay_AndRetriesLater()
    {
        _db.PingAsync().Returns(Task.FromResult(false));
        var now = new DateTime(2026, 11, 7, 9, 0, 0);

        // DB 不可用 → 本轮不导出
        (await _worker.TryExportAsync(now, Target)).ShouldBeFalse();

        // DB 恢复 → 当天仍会补导（未被标记成"已导出"）
        _db.PingAsync().Returns(Task.FromResult(true));
        (await _worker.TryExportAsync(now.AddMinutes(1), Target)).ShouldBeTrue();
    }

    public void Dispose()
    {
        _health.Dispose();
        try { File.Delete(_healthDbPath); } catch { /* ignore */ }
    }
}
