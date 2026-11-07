namespace UniFlow.Common.Services;

/// <summary>单实例保护的结果。</summary>
public enum SingleInstanceStatus
{
    /// <summary>已拿到锁：本进程是唯一实例。</summary>
    Acquired,
    /// <summary>已有实例在运行（拿到的是已存在的互斥体）→ 调用方应退出。</summary>
    AlreadyRunning,
    /// <summary>互斥体不可用（权限/平台限制）→ 调用方可继续启动，只是没有保护。</summary>
    Unavailable,
}

/// <summary>
/// 单实例保护（老 Delphi 版用 Mutex；.NET 版此前缺失 → 两个实例会同时抢 Web 端口、抢 Aptio 连接、重复清理数据）。
/// <para>
/// 用法：<c>var guard = SingleInstanceGuard.TryAcquire(out var status, out var error);</c>
/// —— status=Acquired 表示本进程是唯一实例；AlreadyRunning 表示已有实例在跑（调用方应退出）；
/// Unavailable 表示互斥体不可用（此时**不应**退出，只是不具备保护）。
/// </para>
/// <para>
/// 名称优先用 <c>Global\UniFlow</c>（跨会话，Windows 服务与控制台互斥）；
/// 非管理员账号没有 SeCreateGlobalPrivilege → 退回 <c>Local\UniFlow</c>（会话级）。
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>全局互斥体名（跨会话）。</summary>
    public const string GlobalName = @"Global\UniFlow";

    /// <summary>会话级互斥体名（非管理员时的退路）。</summary>
    public const string LocalName = @"Local\UniFlow";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, string name)
    {
        _mutex = mutex;
        Name = name;
    }

    /// <summary>实际生效的互斥体名（便于日志定位是全局还是会话级）。</summary>
    public string Name { get; }

    /// <summary>
    /// 尝试获取单实例锁。
    /// </summary>
    /// <param name="status">结果分类（Acquired / AlreadyRunning / Unavailable）。</param>
    /// <param name="error">失败原因（供日志）；成功时为 null。</param>
    public static SingleInstanceGuard? TryAcquire(out SingleInstanceStatus status, out string? error)
        => TryAcquire(GlobalName, LocalName, out status, out error);

    /// <summary>可指定名称的版本（测试用：避免与生产实例抢同一个锁）。</summary>
    public static SingleInstanceGuard? TryAcquire(
        string globalName, string localName, out SingleInstanceStatus status, out string? error)
    {
        error = null;
        // ① 全局（跨会话）：服务 + 控制台互斥
        try
        {
            var m = new Mutex(true, globalName, out var createdNew);
            if (!createdNew)
            {
                m.Dispose();
                status = SingleInstanceStatus.AlreadyRunning;
                error = "another instance is already running";
                return null;
            }
            status = SingleInstanceStatus.Acquired;
            return new SingleInstanceGuard(m, globalName);
        }
        catch (UnauthorizedAccessException)
        {
            // ② 无权创建全局互斥体（非管理员 / 受限服务账号）→ 退到会话级
            try
            {
                var m = new Mutex(true, localName, out var createdNew);
                if (!createdNew)
                {
                    m.Dispose();
                    status = SingleInstanceStatus.AlreadyRunning;
                    error = "another instance is already running";
                    return null;
                }
                status = SingleInstanceStatus.Acquired;
                return new SingleInstanceGuard(m, localName);
            }
            catch (Exception ex)
            {
                status = SingleInstanceStatus.Unavailable;
                error = $"session-scoped mutex unavailable: {ex.Message}";
                return null;
            }
        }
        catch (Exception ex)
        {
            status = SingleInstanceStatus.Unavailable;
            error = $"global mutex unavailable: {ex.Message}";
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* 未持有（异常路径）→ 忽略 */ }
        catch (ObjectDisposedException) { }
        _mutex.Dispose();
    }
}
