namespace UniFlow.Common.Models;

public enum RestartLevel
{
    Hot = 0,              // 保存即生效（热加载）
    InnerRestart = 1,     // 重建业务宿主生效（页面「重启服务」按钮）
    ProcessRestart = 2    // 需操作系统级重启服务进程
}

// 标注配置属性的重启级别；PUT /api/config 响应据此返回生效提示
[AttributeUsage(AttributeTargets.Property)]
public class RestartAttribute : Attribute
{
    public RestartLevel Level { get; }
    public RestartAttribute(RestartLevel level = RestartLevel.Hot) { Level = level; }
}
