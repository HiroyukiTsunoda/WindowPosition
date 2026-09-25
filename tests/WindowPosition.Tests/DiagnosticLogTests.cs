using WindowPosition.Core;

internal static class DiagnosticLogTests
{
    internal static void Sessions() => InDirectory(path =>
    {
        var first = new DiagnosticLog(path);
        first.BeginSession("test first");
        first.Write("WINDOW_HIDE", "still resident");
        first.Complete("TrayMenuExit", 0);
        first.Complete("RuntimeProcessExit", 0);
        first.Write("LATE_EVENT");
        var second = new DiagnosticLog(path);
        second.BeginSession("test second");
        var lines = File.ReadAllLines(path);
        Assert(lines.Count(line => line.Contains("\tEXIT\t")) == 1, "Exit must be written only once");
        Assert(!lines.Any(line => line.Contains("PREVIOUS_SESSION_INCOMPLETE") || line.Contains("LATE_EVENT")), "Clean exit must not be reported as incomplete");
        // A different resident starts with no Complete() from the previous lifetime.
        new DiagnosticLog(path).BeginSession("test third");
        lines = File.ReadAllLines(path);
        Assert(lines.Count(line => line.Contains("\tPREVIOUS_SESSION_INCOMPLETE\t")) == 1, "Unclosed lifetime must be detected");
        Assert(lines.All(line => DateTimeOffset.TryParse(line.Split('\t')[0], out _)), "Each record needs an absolute timestamp");
        Assert(lines.All(line => line.Split('\t')[1] == Environment.ProcessId.ToString()), "Each record needs a process ID");
    });

    internal static void Exceptions() => InDirectory(path =>
    {
        var log = new DiagnosticLog(path);
        log.BeginSession("test");
        Exception captured;
        try { throw new InvalidOperationException("outer\nsecond line", new IOException("inner detail")); }
        catch (Exception error) { captured = error; }
        log.Fatal("WPF Dispatcher", captured);
        log.Complete("ApplicationShutdown", 1);
        new DiagnosticLog(path).BeginSession("after fatal error");
        var lines = File.ReadAllLines(path);
        var fatal = lines.Single(line => line.Contains("\tUNHANDLED_EXCEPTION\t"));
        Assert(fatal.Contains("InvalidOperationException") && fatal.Contains("IOException") && fatal.Contains("inner detail") && fatal.Contains("DiagnosticLogTests"), "Exception type, nested cause and stack trace must survive");
        Assert(fatal.Contains("outer\\nsecond line"), "Newlines must stay within one record");
        Assert(!lines.Any(line => line.Contains("\tEXIT\t")), "A fatal exception must never be labelled as a clean exit");
        Assert(lines.Any(line => line.Contains("\tPREVIOUS_SESSION_INCOMPLETE\t")), "Restart after fatal exit must warn");
    });

    internal static void Rotation() => InDirectory(path =>
    {
        const int limit = 1024;
        var log = new DiagnosticLog(path, limit);
        log.BeginSession("rotation");
        for (var index = 0; index < 30; index++) log.Write("EVENT", new string('界', 10000) + index);
        log.Fatal("large exception", new Exception(new string('\n', 10000)));
        log.Complete("test", 1);
        Assert(File.Exists(path + ".1"), "Rotation must keep a previous generation");
        Assert(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 2, "Only two generations may exist");
        Assert(new FileInfo(path).Length <= limit && new FileInfo(path + ".1").Length <= limit, "Both generations must remain within the byte limit");
        Assert(File.ReadAllText(path + ".1").Contains("[truncated]"), "Oversized records must be explicitly truncated");
        new DiagnosticLog(path, limit).BeginSession("restart after rotation");
        Assert((File.ReadAllText(path) + File.ReadAllText(path + ".1")).Contains("PREVIOUS_SESSION_INCOMPLETE"), "Rotation must retain incomplete-lifetime detection");
    });

    internal static void WriteFailures() => InDirectory(path =>
    {
        Directory.CreateDirectory(path);
        var log = new DiagnosticLog(path);
        log.BeginSession("unwritable");
        log.Write("TEST", error: new IOException("original error"));
        log.Fatal("test", new Exception("original fatal error"));
        log.Complete("test", 1);
        Assert(!string.IsNullOrWhiteSpace(log.LastError), "Write failure must remain inspectable without throwing");
    });

    internal static void ConcurrentWrites() => InDirectory(path =>
    {
        var log = new DiagnosticLog(path);
        log.BeginSession("concurrent");
        Parallel.For(0, 80, index => log.Write("EVENT", $"number={index}"));
        log.Complete("test", 0);
        var lines = File.ReadAllLines(path);
        Assert(lines.Length == 82 && lines.All(line => line.Split('\t').Length == 5), "Concurrent writes must not interleave or lose records");
        Assert(lines.Last().Contains("\tEXIT\t"), "Exit must be the last durable record");
    });

    private static void InDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowPosition.LogTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(Path.Combine(directory, "WindowPosition.log")); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Assert(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
