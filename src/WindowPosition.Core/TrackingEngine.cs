namespace WindowPosition.Core;

/// <summary>Tracks window lifetimes without taking any dependency on a desktop framework.</summary>
public sealed class TrackingEngine(IWindowSystem windows)
{
    private readonly IWindowSystem _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    private readonly Dictionary<Guid, RuleState> _states = [];

    public IReadOnlyList<RuleStatus> Tick(IReadOnlyList<WindowRule> rules, bool paused = false)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var snapshots = _windows.EnumerateWindows();
        var activeIds = rules.Select(rule => rule.Id).ToHashSet();
        foreach (var id in _states.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            _states.Remove(id);

        // The first enabled matching rule owns a window for this tick, including while minimized.
        var claimed = new HashSet<WindowIdentity>();
        var result = new List<RuleStatus>(rules.Count);
        foreach (var rule in rules)
        {
            var state = GetState(rule);
            var matches = snapshots.Where(window => Matches(rule, window)).ToArray();
            state.ForgetMissing(matches.Select(Identity).ToHashSet());
            result.Add(Process(rule, matches, state, claimed, paused, force: false));
        }
        return result;
    }

    /// <summary>Applies one rule immediately; callers choose the rule explicitly, so other rules do not compete.</summary>
    public RuleStatus ApplyNow(WindowRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var state = GetState(rule);
        var matches = _windows.EnumerateWindows().Where(window => Matches(rule, window)).ToArray();
        state.ForgetMissing(matches.Select(Identity).ToHashSet());
        return Process(rule, matches, state, [], paused: false, force: true);
    }

    public void ResetRule(Guid id) => _states.Remove(id);

    private RuleState GetState(WindowRule rule)
    {
        if (!_states.TryGetValue(rule.Id, out var state) || state.Configuration != rule)
        {
            state = new RuleState(rule);
            _states[rule.Id] = state;
        }
        return state;
    }

    private RuleStatus Process(
        WindowRule rule,
        IReadOnlyList<WindowSnapshot> matches,
        RuleState state,
        HashSet<WindowIdentity> claimed,
        bool paused,
        bool force)
    {
        if (!rule.Enabled)
            return new(rule.Id, 0, "無効", false);
        if (RuleValidation.GetError(rule) is { } validationError)
            return new(rule.Id, matches.Count, validationError, true);
        if (paused)
            return new(rule.Id, matches.Count, "一時停止中", false);
        if (matches.Count == 0)
            return new(rule.Id, 0, "対象ウィンドウの起動を待っています", false);

        var applied = 0;
        var minimized = 0;
        var conflicts = 0;
        var errors = new List<string>();
        var targetBounds = _windows.NormalizeBounds(rule.Bounds);
        foreach (var window in matches)
        {
            var identity = Identity(window);
            if (!claimed.Add(identity))
            {
                conflicts++;
                continue;
            }
            if (window.IsMinimized)
            {
                minimized++;
                continue;
            }
            if (force)
            {
                state.Processed.Remove(identity);
                state.AcceptedAttempts.Remove(identity);
                state.Errors.Remove(identity);
            }
            else if (rule.Mode == FollowMode.OnFirstAppearance && state.Processed.Contains(identity))
            {
                if (state.Errors.TryGetValue(identity, out var previousError))
                    errors.Add(previousError);
                continue;
            }

            if (window.Bounds == targetBounds && !window.IsMaximized)
            {
                state.Processed.Add(identity);
                state.AcceptedAttempts.Remove(identity);
                state.Errors.Remove(identity);
                continue;
            }
            if (rule.Mode == FollowMode.OnFirstAppearance && state.AcceptedAttempts.GetValueOrDefault(identity) >= 3)
            {
                const string unconfirmed = "位置・サイズの反映を確認できませんでした。アプリのサイズ制限等を確認して再適用してください";
                state.Processed.Add(identity);
                state.Errors[identity] = unconfirmed;
                errors.Add(unconfirmed);
                continue;
            }
            if (_windows.TrySetBounds(window.Handle, targetBounds, out var error))
            {
                // Native changes can be asynchronous. Confirm the result in a later snapshot.
                if (rule.Mode == FollowMode.OnFirstAppearance)
                    state.AcceptedAttempts[identity] = state.AcceptedAttempts.GetValueOrDefault(identity) + 1;
                applied++;
            }
            else
            {
                // A failed attempt must be retried even in first-appearance mode.
                errors.Add(string.IsNullOrWhiteSpace(error) ? "ウィンドウの位置・サイズを変更できませんでした" : error);
            }
        }

        var messages = new List<string>();
        if (applied > 0)
            messages.Add($"位置・サイズの適用を要求しました（{applied} 件）");
        if (minimized > 0)
            messages.Add($"最小化中のため待機（{minimized} 件）");
        if (conflicts > 0)
            messages.Add($"先に登録されたルールを優先（{conflicts} 件）");
        if (errors.Count > 0)
            messages.Add($"適用失敗（{errors.Count} 件）: {string.Join(" / ", errors.Distinct())}");
        if (messages.Count == 0)
            messages.Add(rule.Mode == FollowMode.Continuous ? "常時追従中" : "適用済み（次回の起動を待機）");
        return new(rule.Id, matches.Count, string.Join(" / ", messages), errors.Count > 0);
    }

    private static bool Matches(WindowRule rule, WindowSnapshot window) =>
        !string.IsNullOrWhiteSpace(rule.Title) && !string.IsNullOrWhiteSpace(window.Title) &&
        (rule.MatchMode == TitleMatchMode.Contains
            ? window.Title.Contains(rule.Title, StringComparison.OrdinalIgnoreCase)
            : string.Equals(window.Title, rule.Title, StringComparison.OrdinalIgnoreCase));

    private static WindowIdentity Identity(WindowSnapshot window) => new(window.Handle, window.ProcessId);

    private readonly record struct WindowIdentity(nint Handle, int ProcessId);

    private sealed class RuleState(WindowRule configuration)
    {
        public WindowRule Configuration { get; } = configuration;
        public HashSet<WindowIdentity> Processed { get; } = [];
        public Dictionary<WindowIdentity, int> AcceptedAttempts { get; } = [];
        public Dictionary<WindowIdentity, string> Errors { get; } = [];

        public void ForgetMissing(HashSet<WindowIdentity> present)
        {
            Processed.IntersectWith(present);
            foreach (var identity in AcceptedAttempts.Keys.Where(identity => !present.Contains(identity)).ToArray())
                AcceptedAttempts.Remove(identity);
            foreach (var identity in Errors.Keys.Where(identity => !present.Contains(identity)).ToArray())
                Errors.Remove(identity);
        }
    }
}

internal static class RuleValidation
{
    public static string? GetError(WindowRule rule)
    {
        if (rule.Id == Guid.Empty)
            return "項目の ID が不正です";
        if (string.IsNullOrWhiteSpace(rule.Title))
            return "ウィンドウタイトルを入力してください";
        if (rule.Width <= 0 || rule.Height <= 0)
            return "幅と高さには 1 以上の数値を入力してください";
        if (!Enum.IsDefined(rule.Mode) || !Enum.IsDefined(rule.MatchMode))
            return "追従モードまたはタイトルの一致方法が不正です";
        return null;
    }
}
