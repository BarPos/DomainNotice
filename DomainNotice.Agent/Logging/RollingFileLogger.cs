using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace DomainNotice.Agent.Logging;

/// <summary>
/// Small rolling file logger: <c>agent.log</c>, rolled to <c>agent.1.log</c> … <c>agent.N.log</c> at a size limit
/// (plan 5.4). Writes happen on a background task so logging never blocks the link; failures are swallowed.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Task _writer;

    public RollingFileLoggerProvider(string directory, string baseName = "agent", long maxBytes = 5 * 1024 * 1024, int keep = 10)
    {
        _directory = directory;
        _baseName = baseName;
        _maxBytes = maxBytes;
        _keep = keep;
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(name, this));

    internal void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private string CurrentPath => Path.Combine(_directory, _baseName + ".log");

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                try
                {
                    writer ??= Open();
                    while (_queue.Reader.TryRead(out var line))
                    {
                        writer.Write(line);
                        if (writer.BaseStream.Length >= _maxBytes)
                        {
                            writer.Dispose();
                            writer = null;
                            Roll();
                            writer = Open();
                        }
                    }
                    writer.Flush();
                }
                catch (Exception)
                {
                    writer?.Dispose();
                    writer = null;
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter Open()
    {
        System.IO.Directory.CreateDirectory(_directory);
        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
    }

    private void Roll()
    {
        var oldest = Path.Combine(_directory, $"{_baseName}.{_keep}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = _keep - 1; i >= 1; i--)
        {
            var from = Path.Combine(_directory, $"{_baseName}.{i}.log");
            if (File.Exists(from)) File.Move(from, Path.Combine(_directory, $"{_baseName}.{i + 1}.log"), overwrite: true);
        }
        File.Move(CurrentPath, Path.Combine(_directory, $"{_baseName}.1.log"), overwrite: true);
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
    }

    private sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var builder = new StringBuilder(256);
            builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            builder.Append(' ').Append(Level(logLevel)).Append(' ').Append(category).Append(": ");
            builder.Append(formatter(state, exception));
            if (exception is not null) builder.AppendLine().Append(exception);
            builder.AppendLine();
            provider.Enqueue(builder.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
