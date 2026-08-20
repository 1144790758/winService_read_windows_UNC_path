using System.Collections.Concurrent;

namespace WindowsServiceHost;

public sealed class FileLoggerOptions
{
    public bool Enabled { get; set; }
    /// <summary>日志文件路径,留空则使用服务安装目录下的 logs\service.log。</summary>
    public string Path { get; set; } = string.Empty;
}

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public FileLoggerProvider(string? filePath, string? basePath = null)
    {
        var baseDir = basePath ?? AppContext.BaseDirectory;
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? System.IO.Path.Combine(baseDir, "logs", "service.log")
            : (System.IO.Path.IsPathRooted(filePath)
                ? filePath
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, filePath)));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_filePath)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    internal void WriteLine(string line)
    {
        lock (_lock)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{logLevel}] [{categoryName}] {formatter(state, exception)}";
            if (exception is not null) message += Environment.NewLine + exception;
            provider.WriteLine(message);
        }
    }
}
