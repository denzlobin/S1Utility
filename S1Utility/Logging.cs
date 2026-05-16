using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace S1Utility;

// Minimal hand-rolled logging. ILog stays in S1Utility (no Avalonia dependency) so
// any non-UI helper can write to it; S1Utility.Core does not depend on this and keeps
// throwing — the catching caller logs.
public interface ILog
{
    void Info(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
}

// No-op default. Active until Log.Logger is replaced at app startup, and used by
// tests / headless tools that never wire a real sink.
public sealed class NullLog : ILog
{
    public void Info(string message) { }
    public void Warn(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
}

// Global access point. Set once at app startup; safe to read concurrently.
public static class Log
{
    public static ILog Logger { get; set; } = new NullLog();
}

// Per-session log file under <directory>, pruned to the N newest sessions and
// hard-capped at maxBytes so a runaway error loop can't fill the disk. Writes are
// serialized — safe to call from the MIDI receive thread and the UI thread.
public sealed class RollingFileLogger : ILog, IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private readonly long _maxBytes;
    private bool _truncated;

    public string Path { get; }

    public RollingFileLogger(string directory, int keepSessions = 5, long maxBytes = 1_048_576)
    {
        Directory.CreateDirectory(directory);
        Prune(directory, Math.Max(0, keepSessions - 1));

        Path      = System.IO.Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _maxBytes = maxBytes;
        _writer   = new StreamWriter(Path, append: false) { AutoFlush = true };

        var v = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?";
        Info($"S1Utility {v} on {Environment.OSVersion}");
    }

    private static void Prune(string dir, int keepNewest)
    {
        try
        {
            var stale = new DirectoryInfo(dir).GetFiles("session-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keepNewest);
            foreach (var f in stale)
            {
                try { f.Delete(); } catch { /* a delete failure here is not loggable */ }
            }
        }
        catch { /* dir enumeration failure: leave existing files alone */ }
    }

    public void Info(string m)                                => Write("INFO ", m, null);
    public void Warn(string m, Exception? e = null)           => Write("WARN ", m, e);
    public void Error(string m, Exception? e = null)          => Write("ERROR", m, e);

    private void Write(string level, string message, Exception? ex)
    {
        lock (_lock)
        {
            if (_truncated) return;
            _writer.Write(DateTime.Now.ToString("HH:mm:ss.fff "));
            _writer.Write(level);
            _writer.Write(' ');
            _writer.WriteLine(message);
            if (ex != null) _writer.WriteLine(ex);

            if (_writer.BaseStream.Length > _maxBytes)
            {
                _writer.WriteLine($"[log truncated at {_maxBytes / 1024} KB]");
                _truncated = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}
