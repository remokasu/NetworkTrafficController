using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetworkTrafficController.Core.Logging;

/// <summary>
/// 1 ファイルに追記する最小限の <see cref="ILoggerProvider"/>。依存を増やさないために自前で持つ。
/// 書き込みはロックで直列化し、行ごとにフラッシュする（クラッシュ時に直前のログを残すため）。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>既定のログファイル上限（64 MiB）。超えたら書き込みを止め、その旨を 1 行残す。</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly LogLevel _minimumLevel;
    private readonly long _maxBytes;
    private bool _capped;

    /// <summary>ログファイルのパス。</summary>
    public string Path { get; }

    /// <summary>ログファイルを開く（追記）。フォルダが無ければ作る。サイズが <paramref name="maxBytes"/> に達したら以降は書かない。</summary>
    public FileLoggerProvider(string path, LogLevel minimumLevel = LogLevel.Information, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        _minimumLevel = minimumLevel;
        _maxBytes = maxBytes;
        // 管理者権限で書くので、ジャンクション経由で保護されたフォルダに書かされないようにする（security review MEDIUM-1）
        Native.InstallDirectoryGuard.EnsureNotReparsePoint(path);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = new StringBuilder(256)
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Level(level)).Append("] ")
            .Append(category).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_lock)
        {
            if (_capped)
            {
                return;
            }

            if (_writer.BaseStream.Length >= _maxBytes)
            {
                // ディスクを使い切らないよう、上限に達したら以降は書かない（security review MEDIUM）
                _capped = true;
                _writer.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [WRN] FileLoggerProvider: log size limit {_maxBytes} bytes reached; further log lines are discarded");
                return;
            }

            _writer.WriteLine(line.ToString());
        }
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

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
