namespace WindowPosition.Core;

public enum FollowMode
{
    OnFirstAppearance,
    Continuous
}

public enum TitleMatchMode
{
    Exact,
    Contains
}

public sealed record WindowBounds(int X, int Y, int Width, int Height);

public sealed record WindowSnapshot(
    nint Handle,
    int ProcessId,
    string Title,
    WindowBounds Bounds,
    bool IsMinimized,
    bool IsMaximized);

public sealed record WindowRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = string.Empty;
    public TitleMatchMode MatchMode { get; init; } = TitleMatchMode.Exact;
    public FollowMode Mode { get; init; } = FollowMode.OnFirstAppearance;
    public bool Enabled { get; init; } = true;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 800;
    public int Height { get; init; } = 600;

    [System.Text.Json.Serialization.JsonIgnore]
    public WindowBounds Bounds => new(X, Y, Width, Height);
}

public sealed record RuleStatus(Guid RuleId, int MatchedCount, string Message, bool HasError);

public sealed record AppSettings
{
    public List<WindowRule> Rules { get; init; } = [];
    public bool StartInTray { get; init; }
}

public interface IWindowSystem
{
    IReadOnlyList<WindowSnapshot> EnumerateWindows();
    bool TrySetBounds(nint handle, WindowBounds bounds, out string? error);
    WindowBounds NormalizeBounds(WindowBounds bounds) => bounds;
}
