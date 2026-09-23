using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using WindowPosition.Core;

namespace WindowPosition.Smoke;

internal static class WheelUiChecks
{
    internal static async Task RunAsync(nint appHandle, string executable, Action<string, bool, string> assert)
    {
        var settingsPath = Path.Combine(Path.GetDirectoryName(executable)!, "settings.cfg");
        var initialBytes = File.ReadAllBytes(settingsPath);
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        assert("wheel-settings-readable", store.LoadWarning is null, store.LoadWarning ?? $"{settings.Rules.Count} existing rules");
        var initial = await Task.Run(() => Snapshot(appHandle));
        assert("wheel-list-scrollable", initial.Scrollable && initial.ViewSize > 0 && initial.ViewSize < 100,
            $"Items={settings.Rules.Count}; VerticalViewSize={initial.ViewSize:R}; VerticalScrollPercent={initial.Percent:R}");

        // WPF's item-scrolling provider reports viewport / extent as a percentage.
        // Therefore one logical item advances 100 / (extent - viewport) percent.
        var viewport = settings.Rules.Count * initial.ViewSize / 100;
        var scrollableItems = settings.Rules.Count - viewport;
        assert("wheel-enough-items-for-independent-check", scrollableItems >= 2,
            $"Items={settings.Rules.Count}; viewport={viewport:R}; scrollableItems={scrollableItems:R}");
        var expectedPercent = 100 / scrollableItems;
        var originalCursor = Cursor.Position;
        var originalForeground = DesktopInterop.GetForegroundWindow();
        Exception? failure = null;
        try
        {
            await Task.Run(() => SetPercent(appHandle, 0));
            await WaitAsync(async () => Math.Abs((await Task.Run(() => Snapshot(appHandle))).Percent) < 0.001);
            DesktopInterop.SetForegroundWindow(appHandle);
            await Task.Delay(150);
            var top = await Task.Run(() => Snapshot(appHandle));
            var point = new Point((int)(top.Bounds.Left + top.Bounds.Width / 2), (int)(top.Bounds.Top + top.Bounds.Height / 2));
            assert("wheel-input-hits-application", DesktopInterop.RootWindowAtPoint(point) == appHandle,
                $"List center={point}; application HWND=0x{appHandle:X}");

            // SendInput runs off the driver's dispatcher so the target's low-level
            // hooks cannot wait on the thread that is synchronously delivering input.
            await Task.Run(() => DesktopInterop.MouseWheel(point, -120));
            await WaitAsync(async () => (await Task.Run(() => Snapshot(appHandle))).Percent > 0.001);
            await Task.Delay(150);
            var down = await Task.Run(() => Snapshot(appHandle));
            var movedItems = down.Percent / expectedPercent;
            assert("wheel-down-moves-one-item", Math.Abs(movedItems - 1) < 0.01,
                $"Delta=-120; expectedPercent={expectedPercent:R}; actualPercent={down.Percent:R}; movedItems={movedItems:R}");

            await Task.Run(() => DesktopInterop.MouseWheel(point, 120));
            await WaitAsync(async () => Math.Abs((await Task.Run(() => Snapshot(appHandle))).Percent) < 0.001);
            var up = await Task.Run(() => Snapshot(appHandle));
            assert("wheel-up-returns-one-item", Math.Abs(up.Percent) < 0.001,
                $"Delta=120; VerticalScrollPercent={up.Percent:R}");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try
            {
                await Task.Run(() => SetPercent(appHandle, initial.Percent));
                await WaitAsync(async () => Math.Abs((await Task.Run(() => Snapshot(appHandle))).Percent - initial.Percent) < 0.001);
                var restored = await Task.Run(() => Snapshot(appHandle));
                assert("wheel-original-scroll-restored", Math.Abs(restored.Percent - initial.Percent) < 0.001,
                    $"Original={initial.Percent:R}; restored={restored.Percent:R}");
                assert("wheel-selection-unchanged", initial.Selection.SequenceEqual(restored.Selection),
                    $"Original=[{string.Join(", ", initial.Selection)}]; current=[{string.Join(", ", restored.Selection)}]");
                assert("wheel-settings-unchanged", File.ReadAllBytes(settingsPath).SequenceEqual(initialBytes),
                    $"settings.cfg remains byte-for-byte identical ({initialBytes.Length} bytes)");
            }
            catch (Exception cleanupError)
            {
                failure = failure is null ? cleanupError : new AggregateException(failure, cleanupError);
            }
            DesktopInterop.SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != 0)
                DesktopInterop.SetForegroundWindow(originalForeground);
        }
        if (failure is not null)
            throw new InvalidOperationException("Actual application one-item mouse-wheel check failed", failure);
    }

    private static AutomationElement FindList(nint appHandle) =>
        AutomationElement.FromHandle(appHandle).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "RuleList"))
        ?? throw new InvalidOperationException("RuleList was not found");

    private static ListSnapshot Snapshot(nint appHandle)
    {
        var list = FindList(appHandle);
        var scroll = ((ScrollPattern)list.GetCurrentPattern(ScrollPattern.Pattern)).Current;
        var selected = ((SelectionPattern)list.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection();
        // Runtime IDs can change as virtualization recreates containers. Names
        // identify the selected rule without forcing it into view or selecting it.
        return new ListSnapshot(scroll.VerticallyScrollable, scroll.VerticalViewSize, scroll.VerticalScrollPercent,
            list.Current.BoundingRectangle, selected.Select(item => item.Current.Name).ToArray());
    }

    private static void SetPercent(nint appHandle, double percent) =>
        ((ScrollPattern)FindList(appHandle).GetCurrentPattern(ScrollPattern.Pattern))
            .SetScrollPercent(ScrollPattern.NoScroll, percent);

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("RuleList did not reach the expected scroll position within 5 seconds");
            await Task.Delay(50);
        }
    }

    private sealed record ListSnapshot(bool Scrollable, double ViewSize, double Percent,
        System.Windows.Rect Bounds, string[] Selection);
}
