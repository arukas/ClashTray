using ClashTray.Core;
using Microsoft.Extensions.Logging;

namespace ClashTray.Service;

/// <summary>
/// A bounded rolling file logger provider for the privileged service. Entries are
/// written as single lines to <c>service-yyyyMMdd-HHmmss-N.log</c> files; when the
/// current file reaches the size cap a new file is started and the oldest files
/// beyond the retention count are deleted, so total log volume stays bounded.
/// All messages pass through <see cref="ErrorSanitizer"/> before they are persisted.
/// Logging failures are dropped: logging must never crash the service.
/// </summary>
internal sealed class RollingFileLoggerProvider : ILoggerProvider
{
    internal const long DefaultMaxFileBytes = 1024 * 1024;
    internal const int DefaultMaxRetainedFiles = 4;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly long _maxFileBytes;
    private readonly int _maxRetainedFiles;
    private readonly LogLevel _minLevel;
    private StreamWriter? _writer;
    private string? _currentFilePath;
    private long _currentFileBytes;
    private int _fileSequence;
    private bool _disposed;

    public RollingFileLoggerProvider(
        string directory,
        string filePrefix = "service-",
        long maxFileBytes = DefaultMaxFileBytes,
        int maxRetainedFiles = DefaultMaxRetainedFiles,
        LogLevel minLevel = LogLevel.Information)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRetainedFiles, 1);
        _directory = directory;
        _filePrefix = filePrefix;
        _maxFileBytes = maxFileBytes;
        _maxRetainedFiles = maxRetainedFiles;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void WriteEntry(string category, LogLevel level, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter();
                if (_writer is null)
                {
                    return;
                }

                string sanitized = ErrorSanitizer.Sanitize(message).ReplaceLineEndings(" ");
                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {category}: {sanitized}";
                if (exception is not null)
                {
                    line += $" | {exception.GetType().Name}: {ErrorSanitizer.Sanitize(exception).ReplaceLineEndings(" ")}";
                }

                _writer.WriteLine(line);
                _currentFileBytes += System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (IOException)
            {
                // A logging failure must never take down the privileged service.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null && _currentFileBytes < _maxFileBytes)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(_directory);
        // Every rotation gets a fresh process-unique name; never append to a
        // file this provider instance did not create.
        string path = Path.Combine(_directory, $"{_filePrefix}{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{_fileSequence}.log");
        _fileSequence++;
        FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _currentFilePath = path;
        _currentFileBytes = 0;
        EnforceRetention();
    }

    private void EnforceRetention()
    {
        // Order by write time: a timestamp-only name sort cannot order the
        // same-second collision suffixes (-1, -2) against the base name.
        string[] files = Directory
            .GetFiles(_directory, $"{_filePrefix}*.log")
            .OrderByDescending(file => File.GetLastWriteTimeUtc(file))
            .ThenByDescending(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (int index = _maxRetainedFiles; index < files.Length; index++)
        {
            try
            {
                File.Delete(files[index]);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.WriteEntry(category, logLevel, formatter(state, exception), exception);
        }
    }
}
