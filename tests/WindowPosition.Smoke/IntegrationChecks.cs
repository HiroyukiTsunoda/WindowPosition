using System.Diagnostics;
using System.Windows.Threading;
using WindowPosition.App.Services;
using WindowPosition.Core;

namespace WindowPosition.Smoke;

internal static class IntegrationChecks
{
    internal static async Task RunAsync(Action<string, bool, string> assert, Action<nint, string> checkVisible)
    {
        var title = $"WindowPosition smoke {Guid.NewGuid():N}";
        var originalCursor = Cursor.Position;
        var originalForeground = DesktopInterop.GetForegroundWindow();
        Process? target = null;
        try
        {
            target = StartTarget(title);
            var handle = await WaitForTargetAsync(target, title);
            checkVisible(handle, "target");

            var native = new NativeWindowSystem();
            var observed = native.EnumerateWindows().SingleOrDefault(window => window.Handle == handle);
            assert("native-enumeration-title", observed?.Title == title, observed?.Title ?? "Target missing");
            assert("native-capture-position-size", observed?.Bounds == ToBounds(DesktopInterop.Bounds(handle)), observed?.Bounds.ToString() ?? "Target missing");

            var area = Screen.PrimaryScreen!.WorkingArea;
            var saved = new WindowBounds(area.X + 120, area.Y + 100, 600, 390);
            var moved = new WindowBounds(area.X + 220, area.Y + 170, 650, 420);
            assert("native-set-bounds-accepted", native.TrySetBounds(handle, saved, out var nativeError), nativeError ?? saved.ToString());
            await WaitAsync(() => ToBounds(DesktopInterop.Bounds(handle)) == saved);
            assert("native-set-bounds-actual", ToBounds(DesktopInterop.Bounds(handle)) == saved, DesktopInterop.Bounds(handle).ToString());

            var rule = new WindowRule
            {
                Title = title,
                X = saved.X,
                Y = saved.Y,
                Width = saved.Width,
                Height = saved.Height,
                Mode = FollowMode.OnFirstAppearance,
            };
            var engine = new TrackingEngine(native);
            Move(handle, moved);
            engine.Tick([rule]);
            await WaitAsync(() => ToBounds(DesktopInterop.Bounds(handle)) == saved);
            assert("first-appearance-restores", ToBounds(DesktopInterop.Bounds(handle)) == saved, saved.ToString());
            engine.Tick([rule]);
            Move(handle, moved);
            for (var tick = 0; tick < 3; tick++)
            {
                engine.Tick([rule]);
                await Task.Delay(100);
            }
            assert("first-appearance-allows-later-user-move", ToBounds(DesktopInterop.Bounds(handle)) == moved, DesktopInterop.Bounds(handle).ToString());

            await CloseTargetAsync(target);
            target = null;
            engine.Tick([rule]);
            target = StartTarget(title);
            handle = await WaitForTargetAsync(target, title);
            engine.Tick([rule]);
            await WaitAsync(() => ToBounds(DesktopInterop.Bounds(handle)) == saved);
            assert("first-appearance-restores-next-process", ToBounds(DesktopInterop.Bounds(handle)) == saved, $"PID={target.Id}; {saved}");
            engine.Tick([rule]);

            rule = rule with { Mode = FollowMode.Continuous };
            engine.Tick([rule]);
            Move(handle, moved);
            engine.Tick([rule]);
            await WaitAsync(() => ToBounds(DesktopInterop.Bounds(handle)) == saved);
            assert("continuous-restores-after-user-move", ToBounds(DesktopInterop.Bounds(handle)) == saved, saved.ToString());

            var disabled = rule with { Enabled = false };
            Move(handle, moved);
            engine.Tick([disabled]);
            await Task.Delay(200);
            assert("disabled-rule-leaves-window-unchanged", ToBounds(DesktopInterop.Bounds(handle)) == moved, DesktopInterop.Bounds(handle).ToString());

            DesktopInterop.ShowWindow(handle, 6);
            await WaitAsync(() => DesktopInterop.IsIconic(handle));
            engine.Tick([rule]);
            await Task.Delay(200);
            assert("minimized-window-stays-minimized", DesktopInterop.IsIconic(handle), "Continuous mode does not unminimize the target");
            DesktopInterop.ShowWindow(handle, 9);
            await WaitAsync(() => !DesktopInterop.IsIconic(handle));
            Move(handle, moved);
            DesktopInterop.SetForegroundWindow(handle);
            await Task.Delay(200);

            using var capture = new WindowCaptureService(native, Dispatcher.CurrentDispatcher);
            var captured = new TaskCompletionSource<WindowSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.Captured += (_, snapshot) => captured.TrySetResult(snapshot);
            capture.Failed += (_, error) => captured.TrySetException(new InvalidOperationException(error));
            capture.Start();
            assert("capture-mode-starts", capture.IsCapturing, "Global mouse hook installed");
            var rectangle = DesktopInterop.Bounds(handle);
            var center = new Point(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
            assert("capture-child-hit-target", native.GetWindowAtPoint(center.X, center.Y)?.Handle == handle, $"Child control at {center} resolves to the top-level target");
            // Keep the hook-owning UI thread pumping while the synthetic input
            // waits for its low-level hook callbacks, just as physical input does.
            await Task.Run(() => DesktopInterop.LeftClick(center));
            WindowSnapshot snapshot;
            try { snapshot = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException exception)
            {
                var diagnostics = string.Join("; ", new[] { "_waitingForRelease", "_completionQueued", "_selectedWindow", "_hook" }
                    .Select(field => $"{field}={typeof(WindowCaptureService).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(capture)}"));
                throw new TimeoutException($"Capture timeout: {diagnostics}; targetTitle={native.GetWindow(handle)?.Title}; thread={Environment.CurrentManagedThreadId}; sync={SynchronizationContext.Current?.GetType().Name}", exception);
            }
            assert("capture-click-title-position-size", snapshot.Handle == handle && snapshot.Title == title && snapshot.Bounds == moved,
                $"Title={snapshot.Title}; Bounds={snapshot.Bounds}");
            assert("capture-hook-released", !capture.IsCapturing, "Capture ended after the selection click");
            await Task.Delay(150);
            assert("capture-selection-click-suppressed", native.GetWindow(handle)?.Title == title, "Selection did not click through to the target control");

            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.Cancelled += (_, _) => cancelled.TrySetResult();
            capture.Start();
            await Task.Run(DesktopInterop.Escape);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            assert("capture-escape-short-tap-cancels", !capture.IsCapturing, capture.LastCancellationReason ?? "No cancellation reason");
            checkVisible(handle, "target-after-tests");
        }
        finally
        {
            if (target is not null)
                await CloseTargetAsync(target);
            DesktopInterop.SetCursorPos(originalCursor.X, originalCursor.Y);
            if (originalForeground != 0)
                DesktopInterop.SetForegroundWindow(originalForeground);
        }
    }

    internal static Process StartTarget(string title)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(title);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start temporary test target");
    }

    internal static async Task<nint> WaitForTargetAsync(Process process, string title)
    {
        nint handle = 0;
        await WaitAsync(() =>
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Target exited: {process.ExitCode}");
            handle = DesktopInterop.FindWindow(null, title);
            return handle != 0 && DesktopInterop.IsWindowVisible(handle) && DesktopInterop.Responsive(handle);
        });
        return handle;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var expires = Stopwatch.GetTimestamp() + (long)(5 * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > expires)
                throw new TimeoutException("Temporary target did not reach the expected state within 5 seconds");
            await Task.Delay(50);
        }
    }

    private static void Move(nint handle, WindowBounds bounds) =>
        DesktopInterop.Move(handle, new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height));

    private static WindowBounds ToBounds(Rectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    internal static async Task CloseTargetAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException)
                {
                    // This PID was created by this test; never terminate any user process.
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally { process.Dispose(); }
    }
}
