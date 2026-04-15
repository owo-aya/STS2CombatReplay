using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace STS2CombatRecorder;

internal static class DebugFileLogger
{
    private static readonly TimeSpan RecorderTimeOffset = TimeSpan.FromHours(8);
    private static readonly object Sync = new();
    private static readonly string ModDirectory =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ??
        AppContext.BaseDirectory;
    private static readonly string DebugLogPath = Path.Combine(ModDirectory, "recorder_debug.log");
    private static readonly string LastErrorPath = Path.Combine(ModDirectory, "last_error.txt");
    private static long _totalBytesWritten;

#if DEBUG
    public static bool IsDebugBuild => true;
#else
    public static bool IsDebugBuild => false;
#endif

    public static long TotalBytesWritten
    {
        get
        {
            lock (Sync)
            {
                return _totalBytesWritten;
            }
        }
    }

    public static void StartSession(string location, string message)
    {
        TryAppendLine(string.Empty);
        TryAppendLine("============================================================");
        TryAppendLine($"{Timestamp()} [{location}] Session start");
        TryAppendLine($"{Timestamp()} [{location}] {message}");
    }

    public static void Log(string location, string message)
    {
        TryAppendLine($"{Timestamp()} [{location}] {message}");
    }

    public static void Error(string location, Exception ex)
    {
        TryAppendLine($"{Timestamp()} [{location}] ERROR: {ex.Message}");
        TryWriteLastError(location, ex);
    }

    public static void Error(string location, string message, Exception ex)
    {
        TryAppendLine($"{Timestamp()} [{location}] ERROR: {message}: {ex.Message}");
        TryWriteLastError(location, ex, message);
    }

    private static void TryAppendLine(string line)
    {
        try
        {
            var content = line + Environment.NewLine;
            var bytesWritten = Encoding.UTF8.GetByteCount(content);
            lock (Sync)
            {
                File.AppendAllText(DebugLogPath, content, Encoding.UTF8);
                _totalBytesWritten += bytesWritten;
            }
        }
        catch
        {
            // Debug logging must never break recorder flow.
        }
    }

    private static void TryWriteLastError(string location, Exception ex, string? message = null)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine($"time: {Timestamp()}");
            builder.AppendLine($"location: {location}");
            if (!string.IsNullOrWhiteSpace(message))
            {
                builder.AppendLine($"message_context: {message}");
            }
            builder.AppendLine($"exception_message: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                builder.AppendLine("stack_trace:");
                builder.AppendLine(ex.StackTrace);
            }

            lock (Sync)
            {
                var content = builder.ToString();
                File.WriteAllText(LastErrorPath, content, Encoding.UTF8);
                _totalBytesWritten += Encoding.UTF8.GetByteCount(content);
            }
        }
        catch
        {
            // Debug logging must never break recorder flow.
        }
    }

    private static string Timestamp()
    {
        return DateTimeOffset.UtcNow
            .ToOffset(RecorderTimeOffset)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
    }
}
