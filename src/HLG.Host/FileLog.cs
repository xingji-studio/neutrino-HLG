using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

sealed class FileLog : ILoggerProvider
{
    readonly object gate = new();
    bool disposed;
    public string Path { get; }
    public FileLog(string directory) {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss.fff}_{Environment.ProcessId}.log");
        using var file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
    }
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Write(LogLevel level, string category, string message, Exception? exception = null) {
        var text = message + (exception is null ? "" : Environment.NewLine + exception);
        text = Regex.Replace(text, "(?i)(authorization[\\\"']?\\s*[:=]\\s*[\\\"']?)(?:bearer|basic)\\s+[^\\s,;\\\"']+", "$1[已隐藏]");
        text = Regex.Replace(text, "(?i)((?:authorization|api[_-]?key|token|password|cookie)[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s,;\\\"'}]+", "$1[已隐藏]");
        text = Regex.Replace(text, @"(https?://[^\s?]+)\?[^\s]*", "$1?[已隐藏]");
        lock (gate) { if (disposed) return; try { using var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite); using var writer = new StreamWriter(file, new UTF8Encoding(false)); writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {category}: {text}"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }
    public void Dispose() { lock (gate) disposed = true; }
    sealed class Logger(FileLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { if (IsEnabled(level)) owner.Write(level, category, formatter(state, exception), exception); }
    }
}
