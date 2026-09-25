using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using WindowPosition.App;
using WindowPosition.App.Services;
using WindowPosition.Core;

namespace WindowPosition.UiSoak;

internal static class Program
{
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--tray-startup")
            return TrayStartupChecks.Run(args[1], Path.GetFullPath(args[2]), Path.GetFullPath(args[3]));
        if (args.Length != 2) return 2; // output JSON path, then absolute Smoke.exe path
        // Compile the actual App/MainWindow sources; use an isolated executable
        // directory for settings and skip App.OnStartup's production singleton.
        var output = Path.GetFullPath(args[0]);
        var targetExe = Path.GetFullPath(args[1]);
        var app = new TestApplication();
        app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var success = false;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var samples = new List<object>();
            Process? target = null;
            Exception? failure = null;
            try
            {
                var title = "WindowPosition UI soak " + Guid.NewGuid().ToString("N");
                var start = new ProcessStartInfo(targetExe) { UseShellExecute = false };
                start.ArgumentList.Add("--target"); start.ArgumentList.Add(title); start.ArgumentList.Add("--background-target");
                target = Process.Start(start)!;
                var native = new NativeWindowSystem();
                WindowSnapshot? snapshot = null;
                for (var attempt = 0; attempt < 100 && snapshot is null; attempt++)
                { snapshot = native.EnumerateWindows().FirstOrDefault(w => w.Title == title); await Task.Delay(50); }
                if (snapshot is null) throw new InvalidOperationException("Temporary target missing");
                var window = new MainWindow { Title = "Window Position — isolated UI soak" };
                app.MainWindow = window;
                window.Show();
                await Task.Delay(200);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var timer = (DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(window)!;
                timer.Stop();
                var items = (IList)typeof(MainWindow).GetField("_items", flags)!.GetValue(window)!;
                items.Clear();
                var rule = new WindowRule { Title = title, Mode = FollowMode.Continuous,
                    X = snapshot.Bounds.X, Y = snapshot.Bounds.Y, Width = snapshot.Bounds.Width, Height = snapshot.Bounds.Height };
                var item = typeof(MainWindow).GetMethod("CreateItem", flags)!.Invoke(window, [rule]);
                items.Add(item);
                var tick = typeof(MainWindow).GetMethod("Tick", flags)!;
                using var self = Process.GetCurrentProcess();
                var clock = Stopwatch.StartNew();
                long baselineMemory = 0;
                uint baselineGdi = 0, baselineUser = 0;
                int baselineHandles = 0;
                for (var index = 0; index <= 50000; index++)
                {
                    // Alternate accepted moves and impossible-size requests. The
                    // latter exercise continuous retries and repeated status UI changes.
                    if (index % 16 == 0)
                        ((RuleItem)item!).Replace(rule with { Width = index % 32 == 0 ? rule.Width + 30 : 1 });
                    tick.Invoke(window, null);
                    if (index % 8 == 0) await Dispatcher.Yield(DispatcherPriority.Background);
                    if (index % 5000 == 0)
                    {
                        var memory = GC.GetTotalMemory(true); self.Refresh();
                        var gdi = GetGuiResources(self.Handle, 0); var user = GetGuiResources(self.Handle, 1);
                        if (index == 5000) { baselineMemory = memory; baselineGdi = gdi; baselineUser = user; baselineHandles = self.HandleCount; }
                        samples.Add(new { index, seconds = clock.Elapsed.TotalSeconds, retainedBytes = memory, privateBytes = self.PrivateMemorySize64, handles = self.HandleCount, gdi, user });
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        File.WriteAllText(output + ".samples.json", JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                        if (index == 50000 && (memory - baselineMemory > 16 * 1024 * 1024 || gdi > baselineGdi + 16 || user > baselineUser + 16 || self.HandleCount > baselineHandles + 50))
                            throw new InvalidOperationException("UI resources increased beyond the warm-up tolerance");
                    }
                }
                if (!window.IsVisible || !window.Dispatcher.CheckAccess()) throw new InvalidOperationException("UI not available after soak");
                typeof(MainWindow).GetMethod("ExitApplication", flags)!.Invoke(window, null);
                success = true;
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (target is not null)
                {
                    if (!target.HasExited) { target.CloseMainWindow(); if (!target.WaitForExit(2000)) target.Kill(); }
                    target.Dispose();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { Success = success, Samples = samples, Failure = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(success ? 0 : 1);
            }
        }));
        app.Run();
        return success ? 0 : 1;
    }

    internal sealed class TestApplication : App.App
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            // Application queues OnStartup in its constructor, even if a test
            // pumps Dispatcher directly. Never enter the real singleton here.
        }
    }
}
