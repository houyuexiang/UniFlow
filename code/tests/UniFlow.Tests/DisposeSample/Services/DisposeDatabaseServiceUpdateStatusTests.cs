using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using UniFlow.Common.Services;
using UniFlow.DisposeSample.Services;

namespace UniFlow.Tests.DisposeSample.Services;

/// <summary>
/// <see cref="IDisposeDatabaseService.UpdateStatusAsync"/> 的失败契约：
/// DB 写不进去时必须返回 <c>false</c>（并记录日志），**不得抛异常、不得静默当成功**，
/// 否则状态机会推进到 Ack 而 DB 仍是 send=0 → 下一轮重复下发。
/// </summary>
public class DisposeDatabaseServiceUpdateStatusTests
{
    /// <summary>指向一个必然连不通的端口（1），无需真实 MySQL 即可触发连接失败。</summary>
    private static DisposeDatabaseService NewUnreachableService()
    {
        var factory = new MySqlConnectionFactory("127.0.0.1", 1, "flexlab", "root", "pwd");
        return new DisposeDatabaseService(NullLogger<DisposeDatabaseService>.Instance, factory);
    }

    [Fact]
    public async Task UpdateStatusAsync_WhenDbUnreachable_ReturnsFalse_AndDoesNotThrow()
    {
        var svc = NewUnreachableService();

        var ok = await svc.UpdateStatusAsync("S001", "send");

        ok.ShouldBeFalse("DB 写失败必须返回 false，不能让调用方以为写成功了");
    }

    [Fact]
    public async Task UpdateStatusAsync_UnknownField_Throws()
    {
        // 未知字段是编程错误（SQL 片段不存在），必须抛出来，不吞
        var svc = NewUnreachableService();

        await Should.ThrowAsync<ArgumentException>(() => svc.UpdateStatusAsync("S001", "bogus_field"));
    }
}
