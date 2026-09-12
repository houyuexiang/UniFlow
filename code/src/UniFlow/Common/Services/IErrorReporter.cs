namespace UniFlow.Common.Services;

public record ErrorEntry(string Timestamp, string Module, string Level, string Message);

// 错误上报接口：各模块将错误写入队列，由专属 ErrorCollectorWorker 统一写库
public interface IErrorReporter
{
    void Report(string module, string level, string message);
}