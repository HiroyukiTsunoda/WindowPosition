using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using WindowPosition.Core;

namespace WindowPosition.Smoke;

internal static class AppUiChecks
{
    internal static async Task RunAsync(nint appHandle, string executable, Action<string, bool, string> assert)
    {
        var settingsPath = Path.Combine(Path.GetDirectoryName(executable)!, "settings.cfg");
        var initialBytes = File.ReadAllBytes(settingsPath);
        var initialSettings = ReadSettings(settingsPath);
        var title = $"WindowPosition UI smoke {Guid.NewGuid():N}";
        assert("ui-test-unique-rule-title", initialSettings.Rules.All(rule => rule.Title != title), title);
        var originalCursor = Cursor.Position;
        var target = IntegrationChecks.StartTarget(title);
        Exception? failure = null;
        var captured = false;
        try
        {
            var targetHandle = await IntegrationChecks.WaitForTargetAsync(target, title);
            var rectangle = DesktopInterop.Bounds(targetHandle);
            var expected = new WindowBounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            DesktopInterop.SetForegroundWindow(targetHandle);
            await Task.Run(() => Invoke(FindById(appHandle, "CaptureButton")));
            await WaitAsync(() => !DesktopInterop.IsWindowVisible(appHandle));
            await Task.Delay(150);
            assert("ui-capture-mode-active-before-input", !DesktopInterop.IsWindowVisible(appHandle) && DesktopInterop.Responsive(appHandle),
                "Capture settings window remains hidden and its UI thread has completed the capture-start handler");
            var point = new Point(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
            var hit = DesktopInterop.RootWindowAtPoint(point);
            if (hit != targetHandle)
            {
                await Task.Run(DesktopInterop.Escape);
                await WaitAsync(() => DesktopInterop.IsWindowVisible(appHandle));
            }
            assert("ui-capture-click-hits-owned-target", hit == targetHandle,
                $"At {point}: root HWND=0x{hit:X}; owned target HWND=0x{targetHandle:X}; bounds={rectangle}");
            await Task.Run(() => DesktopInterop.LeftClick(point));
            await WaitAsync(() => DesktopInterop.IsWindowVisible(appHandle));
            try
            {
                await WaitAsync(async () => await Task.Run(() => ReadValue(appHandle, "TitleInput")) == title);
            }
            catch (TimeoutException exception)
            {
                var actualTitle = await Task.Run(() => ReadValue(appHandle, "TitleInput"));
                throw new TimeoutException($"Captured editor title did not match the test target. Expected={title}; actual={actualTitle}; target HWND=0x{targetHandle:X}", exception);
            }
            captured = true;

            var saved = ReadSettings(settingsPath);
            var added = saved.Rules.SingleOrDefault(rule => rule.Title == title);
            assert("ui-capture-persists-rule", added is not null && saved.Rules.Count == initialSettings.Rules.Count + 1,
                $"settings.cfg: {initialBytes.Length} -> {new FileInfo(settingsPath).Length} bytes; title={title}");
            assert("ui-capture-persists-bounds", added?.Bounds == expected && added.Enabled && added.Mode == FollowMode.OnFirstAppearance,
                added is null ? "Captured rule missing" : $"Bounds={added.Bounds}; Enabled={added.Enabled}; Mode={added.Mode}");

            var values = await Task.Run(() => new[] { "XInput", "YInput", "WidthInput", "HeightInput" }
                .Select(id => ReadValue(appHandle, id)).ToArray());
            assert("ui-capture-populates-editor", values.SequenceEqual(new[] { expected.X.ToString(), expected.Y.ToString(), expected.Width.ToString(), expected.Height.ToString() }),
                $"X={values[0]}; Y={values[1]}; Width={values[2]}; Height={values[3]}");
            assert("ui-capture-lists-exact-title", await Task.Run(() => FindRuleItem(appHandle, title) is not null), title);

            var area = DesktopInterop.WorkingArea(targetHandle);
            var liveBounds = new Rectangle(area.X + 100, area.Y + 110,
                Math.Min(area.Width - 140, expected.Width + 93), Math.Min(area.Height - 150, expected.Height + 61));
            DesktopInterop.Move(targetHandle, liveBounds);
            await WaitAsync(() => DesktopInterop.Bounds(targetHandle) == liveBounds);
            assert("ui-edge-uses-resized-live-target", liveBounds.Width != expected.Width && liveBounds.Height != expected.Height,
                $"Saved={expected.Width}x{expected.Height}; live={liveBounds.Size}");

            await CheckEdgeAsync(appHandle, targetHandle, settingsPath, title, "AlignUpButton", "top", liveBounds.Size, area,
                frame => frame.Top == area.Top, assert);
            await CheckEdgeAsync(appHandle, targetHandle, settingsPath, title, "AlignLeftButton", "top-left", liveBounds.Size, area,
                frame => frame.Top == area.Top && frame.Left == area.Left, assert);

            await Task.Run(() => ToggleUniqueRule(appHandle, title));
            await WaitAsync(() => ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Enabled == false);
            assert("ui-rule-off-persists", !ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Enabled,
                "Only the unique test rule was toggled OFF");

            await CheckEdgeAsync(appHandle, targetHandle, settingsPath, title, "AlignDownButton", "bottom-while-off", liveBounds.Size, area,
                frame => frame.Bottom == area.Bottom && frame.Left == area.Left, assert);
            await CheckEdgeAsync(appHandle, targetHandle, settingsPath, title, "AlignRightButton", "bottom-right-while-off", liveBounds.Size, area,
                frame => frame.Bottom == area.Bottom && frame.Right == area.Right, assert);
            assert("ui-edge-preserves-rule-off", !ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Enabled,
                "Explicit arrow movement leaves the selected rule OFF");

            await Task.Run(() => ToggleUniqueRule(appHandle, title));
            await WaitAsync(() => ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Enabled);
            assert("ui-rule-on-persists", ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Enabled,
                "Only the unique test rule was toggled ON");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try
            {
                // Never restore by overwriting a live settings file. Select and
                // delete only this run's cryptographically unique captured title.
                var found = await Task.Run(() => FindRuleItem(appHandle, title) is not null);
                if (found)
                {
                    await Task.Run(() => SelectUniqueRule(appHandle, title));
                    var selectedTitle = await Task.Run(() => ReadValue(appHandle, "TitleInput"));
                    if (selectedTitle != title)
                        throw new InvalidOperationException("Cleanup refused: selected editor does not match the unique test rule");
                    await Task.Run(() => Invoke(FindButton(appHandle, "削除")));
                    await WaitAsync(() => ReadSettings(settingsPath).Rules.All(rule => rule.Title != title));
                }
                else if (captured)
                {
                    throw new InvalidOperationException("Captured test rule cannot be found for scoped UI cleanup");
                }
                assert("ui-test-settings-restored-exactly", File.ReadAllBytes(settingsPath).SequenceEqual(initialBytes),
                    $"settings.cfg returned byte-for-byte to its initial {initialBytes.Length} bytes");
            }
            catch (Exception cleanupError)
            {
                failure = failure is null ? cleanupError : new AggregateException(failure, cleanupError);
            }
            await IntegrationChecks.CloseTargetAsync(target);
            DesktopInterop.SetCursorPos(originalCursor.X, originalCursor.Y);
            DesktopInterop.SetForegroundWindow(appHandle);
        }
        if (failure is not null)
            throw new InvalidOperationException("Actual application capture/settings roundtrip failed", failure);
    }

    private static async Task CheckEdgeAsync(nint appHandle, nint targetHandle, string settingsPath, string title,
        string buttonId, string label, Size liveSize, Rectangle area, Func<Rectangle, bool> aligned,
        Action<string, bool, string> assert)
    {
        await Task.Run(() => Invoke(FindById(appHandle, buttonId)));
        await WaitAsync(() => aligned(DesktopInterop.VisibleFrame(targetHandle)));
        // Cross at least one 500 ms tracking tick so a saved outer coordinate
        // inside the invisible resize border cannot be silently clamped back.
        await Task.Delay(650);
        var actual = DesktopInterop.Bounds(targetHandle);
        var frame = DesktopInterop.VisibleFrame(targetHandle);
        assert($"ui-edge-{label}-visible-border", aligned(frame), $"Visible frame={frame}; work area={area}");
        assert($"ui-edge-{label}-fully-on-same-monitor", DesktopInterop.WorkingArea(targetHandle) == area && area.Contains(frame),
            $"Visible frame={frame}; original work area={area}");
        assert($"ui-edge-{label}-preserves-live-size", actual.Size == liveSize, $"Expected live size={liveSize}; outer bounds={actual}");

        var expected = new WindowBounds(actual.X, actual.Y, actual.Width, actual.Height);
        await WaitAsync(() => ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Bounds == expected);
        assert($"ui-edge-{label}-persists-position-and-live-size", ReadSettings(settingsPath).Rules.Single(rule => rule.Title == title).Bounds == expected,
            $"settings.cfg bounds={expected}");
        var values = await Task.Run(() => new[] { "XInput", "YInput", "WidthInput", "HeightInput" }
            .Select(id => ReadValue(appHandle, id)).ToArray());
        assert($"ui-edge-{label}-updates-editor", values.SequenceEqual(new[] { actual.X.ToString(), actual.Y.ToString(), actual.Width.ToString(), actual.Height.ToString() }),
            $"X={values[0]}; Y={values[1]}; Width={values[2]}; Height={values[3]}");
    }

    private static AppSettings ReadSettings(string path)
    {
        var store = new SettingsStore(path);
        var settings = store.Load();
        if (store.LoadWarning is { } warning)
            throw new InvalidDataException(warning);
        return settings;
    }

    private static AutomationElement FindById(nint appHandle, string automationId) =>
        AutomationElement.FromHandle(appHandle).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"UI element not found: {automationId}");

    private static AutomationElement FindButton(nint appHandle, string name) =>
        AutomationElement.FromHandle(appHandle).FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name)))
        ?? throw new InvalidOperationException($"UI button not found: {name}");

    private static string ReadValue(nint appHandle, string automationId) =>
        ((ValuePattern)FindById(appHandle, automationId).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static AutomationElement? FindRuleItem(nint appHandle, string title)
    {
        var list = FindById(appHandle, "RuleList");
        var items = list.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
        foreach (AutomationElement item in items)
        {
            if (item.Current.Name == title || item.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, title)) is not null)
                return item;
        }
        return null;
    }

    private static AutomationElement SelectUniqueRule(nint appHandle, string title)
    {
        var item = FindRuleItem(appHandle, title)
            ?? throw new InvalidOperationException("Unique test rule is missing from the UI");
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        return item;
    }

    private static void ToggleUniqueRule(nint appHandle, string title)
    {
        var item = SelectUniqueRule(appHandle, title);
        var checkbox = item.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
            ?? throw new InvalidOperationException("Unique test rule checkbox not found");
        ((TogglePattern)checkbox.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
    }

    private static Task WaitAsync(Func<bool> condition) => WaitAsync(() => Task.FromResult(condition()));

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Application UI did not reach the expected state within 5 seconds");
            await Task.Delay(75);
        }
    }
}
