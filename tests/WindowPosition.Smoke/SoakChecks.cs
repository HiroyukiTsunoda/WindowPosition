using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WindowPosition.App.Services;
using WindowPosition.Core;

namespace WindowPosition.Smoke;

internal static class SoakChecks
{
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);

    internal static async Task RunAsync(int ticks, string samplesPath, Action<string, bool, string> assert, Action<nint, string> checkVisible)
    {
        if (ticks < 10000 || ticks > 1000000) throw new ArgumentOutOfRangeException(nameof(ticks));
        Directory.CreateDirectory(Path.GetDirectoryName(samplesPath)!);
        var title = "WindowPosition soak " + Guid.NewGuid().ToString("N");
        using var self = Process.GetCurrentProcess();
        Process? target = null;
        var native = new NativeWindowSystem();
        var measured = new CountedWindows(native);
        var engine = new TrackingEngine(measured);
        var area = Screen.PrimaryScreen!.WorkingArea;
        var saved = new WindowBounds(area.X + 80, area.Y + 80, 520, 340);
        var rule = new WindowRule { Title = title, Mode = FollowMode.Continuous, X = saved.X, Y = saved.Y, Width = saved.Width, Height = saved.Height };
        var clock = Stopwatch.StartNew();
        var samples = new List<Sample>();
        try
        {
            target = IntegrationChecks.StartTarget(title, background: true);
            var handle = await IntegrationChecks.WaitForTargetAsync(target, title);
            checkVisible(handle, "soak-target");
            for (var tick = 0; tick <= ticks; tick++)
            {
                if (tick > 0 && tick % 5000 == 0)
                {
                    await IntegrationChecks.CloseTargetAsync(target);
                    target = null;
                    engine.Tick([rule]);
                    target = IntegrationChecks.StartTarget(title, background: true);
                    handle = await IntegrationChecks.WaitForTargetAsync(target, title);
                }
                if (tick % 8 == 0)
                    DesktopInterop.Move(handle, new Rectangle(saved.X + 30, saved.Y + 20, saved.Width + 40, saved.Height + 30));
                if (tick % 256 == 64) DesktopInterop.ShowWindow(handle, 6);
                if (tick % 256 == 72) DesktopInterop.ShowWindow(handle, 9);
                engine.Tick([rule]);
                // Pump the dispatcher and the target UI without substituting fake native calls.
                if (tick % 16 == 0) await Task.Delay(1);
                if (tick % 2500 == 0)
                {
                    var retained = GC.GetTotalMemory(forceFullCollection: true);
                    self.Refresh();
                    samples.Add(new(tick, clock.Elapsed.TotalSeconds, retained, self.PrivateMemorySize64, self.HandleCount,
                        GetGuiResources(self.Handle, 0), GetGuiResources(self.Handle, 1), measured.Moves));
                    File.WriteAllLines(samplesPath, new[] { "ticks,seconds,retainedBytes,privateBytes,handles,gdi,user,moveRequests" }
                        .Concat(samples.Select(s => FormattableString.Invariant($"{s.Ticks},{s.Seconds:F3},{s.RetainedBytes},{s.PrivateBytes},{s.Handles},{s.Gdi},{s.User},{s.Moves}"))));
                }
            }
            DesktopInterop.ShowWindow(handle, 9);
            var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            do { engine.Tick([rule]); await Task.Delay(50); }
            while (native.GetWindow(handle)?.Bounds != saved && Stopwatch.GetTimestamp() < deadline);
            assert("soak-final-bounds", native.GetWindow(handle)?.Bounds == saved, native.GetWindow(handle)?.Bounds.ToString() ?? "missing");
            checkVisible(handle, "soak-target-after-tests");
            var baseline = samples.First(s => s.Ticks >= 2500);
            var last = samples.Last();
            assert("soak-completed-ticks", measured.Enumerations >= ticks, $"ticks={measured.Enumerations}; elapsedSeconds={clock.Elapsed.TotalSeconds:F1}; moves={measured.Moves}");
            assert("soak-retained-memory-bounded", last.RetainedBytes - baseline.RetainedBytes < 8 * 1024 * 1024, $"baseline={baseline.RetainedBytes}; final={last.RetainedBytes}");
            assert("soak-native-handles-bounded", last.Handles - baseline.Handles < 32, $"baseline={baseline.Handles}; final={last.Handles}");
            assert("soak-gdi-user-resources-bounded", last.Gdi <= baseline.Gdi + 8 && last.User <= baseline.User + 8, $"GDI={baseline.Gdi}->{last.Gdi}; USER={baseline.User}->{last.User}");
        }
        finally { if (target is not null) await IntegrationChecks.CloseTargetAsync(target); }
    }

    private sealed record Sample(int Ticks, double Seconds, long RetainedBytes, long PrivateBytes, int Handles, uint Gdi, uint User, long Moves);
    private sealed class CountedWindows(NativeWindowSystem native) : IWindowSystem
    {
        internal long Enumerations, Moves;
        public IReadOnlyList<WindowSnapshot> EnumerateWindows() { Enumerations++; return native.EnumerateWindows(); }
        public WindowBounds NormalizeBounds(WindowBounds bounds) => native.NormalizeBounds(bounds);
        public bool TrySetBounds(nint handle, WindowBounds bounds, out string? error) { Moves++; return native.TrySetBounds(handle, bounds, out error); }
    }
}
