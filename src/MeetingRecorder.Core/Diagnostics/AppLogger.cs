using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MeetingRecorder.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>Sink for technical logs. Local only - see <see cref="AppLogger"/>.</summary>
public interface ILogger
{
    void Log(LogLevel level, string category, string message, Exception? exception = null);
}

public static class LoggerExtensions
{
    public static void Debug(this ILogger logger, string category, string message)
        => logger.Log(LogLevel.Debug, category, message);

    public static void Info(this ILogger logger, string category, string message)
        => logger.Log(LogLevel.Info, category, message);

    public static void Warn(this ILogger logger, string category, string message)
        => logger.Log(LogLevel.Warning, category, message);

    public static void Error(this ILogger logger, string category, string message, Exception? exception = null)
        => logger.Log(LogLevel.Error, category, message, exception);
}

/// <summary>Discards everything. Default so that library code never needs a null check.</summary>
public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}

/// <summary>
/// Rolling local file logger.
/// </summary>
/// <remarks>
/// <para>
/// This application ships no telemetry of any kind: nothing here is ever
/// transmitted, aggregated or uploaded. The log exists so that a user who hits a
/// problem can hand a file to their IT department.
/// </para>
/// <para>
/// Meeting content is deliberately absent. The pipeline logs the *shape* of what
/// happened - segment counts, durations, inference times, device events - never
/// the recognized text, because duplicating confidential meeting content into a
/// second, longer-lived file is exactly the kind of leak this project has to
/// avoid.
/// </para>
/// </remarks>
public sealed class AppLogger : ILogger, IDisposable
{
    private readonly BlockingCollection<string> _queue = new(8192);
    private readonly Thread _worker;
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly int _keepFiles;
    private string _currentPath;
    private long _currentBytes;
    private bool _disposed;

    public AppLogger(string directory, LogLevel minimumLevel = LogLevel.Info, long maxBytes = 4 * 1024 * 1024, int keepFiles = 5)
    {
        _directory = directory;
        MinimumLevel = minimumLevel;
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
        Directory.CreateDirectory(directory);
        _currentPath = Path.Combine(directory, "meetingrecorder.log");
        _currentBytes = File.Exists(_currentPath) ? new FileInfo(_currentPath).Length : 0;

        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "MeetingRecorder.Log",
            // Below normal: logging must never compete with audio capture.
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    public LogLevel MinimumLevel { get; set; }

    public string CurrentFilePath => _currentPath;

    public void Log(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < MinimumLevel || _disposed)
        {
            return;
        }

        var builder = new StringBuilder(160);
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        builder.Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ");
        builder.Append(category).Append(" - ").Append(message);
        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        // Never block the caller: a full queue means the disk is the problem and
        // dropping a log line is always better than stalling the audio pipeline.
        _queue.TryAdd(builder.ToString());
    }

    private void ProcessQueue()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                RollIfNeeded(line.Length);
                using var writer = new StreamWriter(_currentPath, append: true, Encoding.UTF8);
                writer.WriteLine(line);
                _currentBytes += line.Length + Environment.NewLine.Length;
            }
            catch (IOException)
            {
                // Logging must never take the application down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RollIfNeeded(int incomingLength)
    {
        if (_currentBytes + incomingLength < _maxBytes)
        {
            return;
        }

        try
        {
            for (var i = _keepFiles - 1; i >= 1; i--)
            {
                var older = Path.Combine(_directory, $"meetingrecorder.{i}.log");
                var newer = i == 1 ? _currentPath : Path.Combine(_directory, $"meetingrecorder.{i - 1}.log");
                if (File.Exists(newer))
                {
                    File.Move(newer, older, overwrite: true);
                }
            }

            _currentBytes = 0;
        }
        catch (IOException)
        {
            _currentBytes = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
