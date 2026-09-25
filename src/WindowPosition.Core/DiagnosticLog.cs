using System.Text;

namespace WindowPosition.Core;

/// <summary>Single resident process owns this bounded, immediately flushed journal.</summary>
public sealed class DiagnosticLog(string path, int maximumBytes = 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);
    private readonly int _maximumBytes = maximumBytes >= 1024 ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
    private bool _started;
    private bool _completed;
    private bool _fatal;
    public string? LastError { get; private set; }

    public void BeginSession(string details)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            try
            {
                // Only inspect a bounded tail, even if a user supplies an oversized log.
                var previous = LastRecord(_path);
                if (previous.Length == 0) previous = LastRecord(_path + ".1");
                var fields = previous.Split('\t');
                if (previous.Length > 0 && (fields.Length < 5 || fields[3] != "EXIT"))
                    WriteCore("WARN", "PREVIOUS_SESSION_INCOMPLETE", "No completed exit record; cause unknown. Last record: " + previous[..Math.Min(previous.Length, 512)]);
            }
            catch (Exception error) { LastError = error.Message; }
            WriteCore("INFO", "START", details);
        }
    }

    public void Write(string eventName, string details = "", Exception? error = null)
    {
        lock (_gate)
        {
            if (!_started || _completed) return;
            WriteCore(error is null ? "INFO" : "ERROR", eventName, details + (error is null ? "" : " " + error));
        }
    }

    public void Fatal(string source, Exception error)
    {
        lock (_gate)
        {
            if (!_started || _completed) return;
            _fatal = true;
            WriteCore("FATAL", "UNHANDLED_EXCEPTION", source + " " + error);
        }
    }

    public void Complete(string reason, int exitCode)
    {
        lock (_gate)
        {
            if (!_started || _completed) return;
            WriteCore(_fatal ? "ERROR" : "INFO", _fatal ? "EXIT_AFTER_ERROR" : "EXIT", $"reason={reason}; exitCode={exitCode}");
            _completed = true;
        }
    }

    private void WriteCore(string level, string eventName, string details)
    {
        try
        {
            // Keep one physical line per record; bound even very large exception messages.
            var budget = Math.Min(16000, (_maximumBytes - 256) / 4);
            var truncated = details.Length > budget;
            details = details[..Math.Min(details.Length, budget)].Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            if (truncated) details += " [truncated]";
            var record = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:O}\t{Environment.ProcessId}\t{level}\t{eventName}\t{details}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length + record.Length > _maximumBytes)
                File.Move(_path, _path + ".1", overwrite: true);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(record);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception error)
        {
            // Diagnostics must never terminate the app or mask the original exception.
            LastError = error.Message;
        }
    }

    private static string LastRecord(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().TrimEnd('\r', '\n').Split('\n').LastOrDefault() ?? "";
    }
}
