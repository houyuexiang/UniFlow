using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace UniFlow.Common.Services.Logging;

internal static class LogHelper
{
    public static string LogLevelName(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "LOG"
    };

    public static string ModuleName(string category)
    {
        var parts = category.Split('.');
        foreach (var p in parts)
        {
            if (p is "UniFlow" or "Services" or "Workers" or "Models") continue;
            if (p.EndsWith("Worker") || p.EndsWith("Service"))
                return parts[^1].Length > 2 ? parts[^1] : p;
            return p;
        }
        return parts.Length > 0 ? parts[^1] : category;
    }
}

public class UniFlowFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly UniFlowFileLoggerProvider _provider;
    private readonly string _moduleName;

    public UniFlowFileLogger(string categoryName, UniFlowFileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
        _moduleName = LogHelper.ModuleName(categoryName);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var msg = formatter(state, exception);
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [").Append(LogHelper.LogLevelName(logLevel)).Append("] ");
        sb.Append('[').Append(_moduleName).Append("] ");
        sb.Append(msg);
        if (exception != null)
            sb.Append(" | ").Append(exception);

        var line = sb.ToString();
        _provider.WriteModuleLog(_moduleName, line);
        if (logLevel >= LogLevel.Error)
            _provider.WriteErrorLog(line);
    }
}

public class LogFileState
{
    public string? CurrentDate;
    public int FileIndex;
    public StreamWriter? Writer;
}

public class UniFlowFileLoggerProvider : ILoggerProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, UniFlowFileLogger> _loggers = new();
    private readonly ConcurrentDictionary<string, LogFileState> _moduleFiles = new();
    private readonly LogFileState _errorFile = new();
    private readonly string _baseDir;
    private readonly int _maxFileSizeBytes;
    private readonly int _retentionDays;
    private readonly Timer _cleanupTimer;
    private readonly object _lock = new();

    public LogLevel MinLevel { get; }

    public UniFlowFileLoggerProvider(UniFlowLoggerConfig config)
    {
        _baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, config.LogDirectory);
        Directory.CreateDirectory(_baseDir);
        _maxFileSizeBytes = config.MaxFileSizeMb * 1024 * 1024;
        _retentionDays = config.RetentionDays;
        MinLevel = config.LogLevel.ToUpper() switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "INFORMATION" => LogLevel.Information,
            "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Information
        };
        _cleanupTimer = new Timer(_ => CleanupOld(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new UniFlowFileLogger(name, this));

    public void WriteModuleLog(string module, string line)
    {
        var state = _moduleFiles.GetOrAdd(module, _ => new LogFileState());
        lock (_lock)
        {
            Rotate(state, $"{module}_{{0}}");
            state.Writer?.WriteLine(line);
            state.Writer?.Flush();
        }
    }

    public void WriteErrorLog(string line)
    {
        lock (_lock)
        {
            Rotate(_errorFile, "UniFlow_Error_{0}");
            _errorFile.Writer?.WriteLine(line);
            _errorFile.Writer?.Flush();
        }
    }

    private void Rotate(LogFileState state, string nameTemplate)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        if (state.CurrentDate != today)
        {
            CloseWriter(state);
            state.CurrentDate = today;
            state.FileIndex = 0;
            OpenFile(state, BuildPath(nameTemplate, today));
            return;
        }

        if (state.Writer != null && state.Writer.BaseStream.Length > _maxFileSizeBytes)
        {
            CloseWriter(state);
            state.FileIndex++;
            OpenFile(state, BuildPath(nameTemplate, today));
        }
    }

    private string BuildPath(string template, string date)
    {
        var name = $"{template}_{date}.log";
        return Path.Combine(_baseDir, name);
    }

    private void OpenFile(LogFileState state, string path)
    {
        state.Writer = new StreamWriter(path, true, Encoding.UTF8) { AutoFlush = true };
    }

    private static void CloseWriter(LogFileState state)
    {
        state.Writer?.Dispose();
        state.Writer = null;
    }

    private void CleanupOld()
    {
        try
        {
            if (!Directory.Exists(_baseDir)) return;
            var cutoff = DateTime.Now.AddDays(-_retentionDays);
            foreach (var f in Directory.GetFiles(_baseDir, "*.log"))
            {
                var fi = new FileInfo(f);
                if (fi.LastWriteTime < cutoff)
                {
                    try { fi.Delete(); } catch { }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var state in _moduleFiles.Values) CloseWriter(state);
        CloseWriter(_errorFile);
        _loggers.Clear();
    }
}

public static class UniFlowLoggingExtensions
{
    public static ILoggingBuilder AddUniFlowFileLogger(this ILoggingBuilder builder,
        Action<UniFlowLoggerConfig>? configure = null)
    {
        var config = new UniFlowLoggerConfig();
        configure?.Invoke(config);
        builder.AddProvider(new UniFlowFileLoggerProvider(config));
        return builder;
    }
}
