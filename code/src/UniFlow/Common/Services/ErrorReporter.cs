using System.Threading.Channels;

namespace UniFlow.Common.Services;

public class ErrorReporter : IErrorReporter
{
    private readonly Channel<ErrorEntry> _channel = Channel.CreateUnbounded<ErrorEntry>();

    public ChannelReader<ErrorEntry> Reader => _channel.Reader;

    public void Report(string module, string level, string message)
    {
        _channel.Writer.TryWrite(new ErrorEntry(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), module, level, message));
    }
}