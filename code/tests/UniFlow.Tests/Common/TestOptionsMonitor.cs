using Microsoft.Extensions.Options;

namespace UniFlow.Tests.Common;

/// <summary>
/// 可变 <see cref="IOptionsMonitor{TOptions}"/> 替身（测试用）。
/// <para>
/// 生产环境的 monitor 由 DI 提供（<c>ConfigureAptioOptions</c> / <c>services.Configure&lt;T&gt;(section)</c>，
/// 均带变更令牌 → appsettings.json 改动即刷新）。
/// 测试里用它模拟"配置文件被改"：改 <see cref="CurrentValue"/> 再触发一次刷新即可。
/// </para>
/// </summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
