using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WindowPosition.App;
using WindowPosition.App.Services;
using WindowPosition.Core;

namespace WindowPosition.UiSoak;

internal static class TrayStartupChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(string phase, string output, string targetExe)
    {
        var checks = new List<string>();
        var app = new Program.TestApplication();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var success = false;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            Exception? failure = null;
            Process? target = null;
            MainWindow? window = null;
            try
            {
                var store = new SettingsStore(Path.Combine(AppContext.BaseDirectory, "settings.cfg"));
                if (phase == "hide")
                    store.Save(new() { Rules = [new WindowRule { Title = "WindowPosition tray test " + Guid.NewGuid().ToString("N"), X = 120, Y = 100, Width = 600, Height = 390 }] });
                var saved = store.Load();
                if (saved.Rules.Count != 1) throw new InvalidOperationException("Expected isolated test configuration");
                var rule = saved.Rules.Single();
                if (phase == "hidden")
                {
                    var start = new ProcessStartInfo(targetExe) { UseShellExecute = false };
                    start.ArgumentList.Add("--target"); start.ArgumentList.Add(rule.Title); start.ArgumentList.Add("--background-target");
                    target = Process.Start(start)!;
                }
                var visibleTransitions = 0;
                window = new MainWindow { Title = "Window Position — tray startup test" };
                window.IsVisibleChanged += (_, _) => { if (window.IsVisible) visibleTransitions++; };
                app.MainWindow = window;
                window.RestoreStartupVisibility();
                await Task.Delay(150);
                Assert(phase == "hidden" ? !window.IsVisible && visibleTransitions == 0 : window.IsVisible, "saved-startup-visibility");
                var tray = (System.Windows.Forms.NotifyIcon)typeof(MainWindow).GetField("_tray", Private)!.GetValue(window)!;
                var timer = (DispatcherTimer)typeof(MainWindow).GetField("_timer", Private)!.GetValue(window)!;
                Assert(tray.Visible && timer.IsEnabled, "tray-and-tracking-active");
                if (phase == "hide")
                {
                    AssertVisible(window);
                    window.Close(); // Real title-bar close path, which must stay resident.
                    Assert(!window.IsVisible && store.Load().StartInTray, "close-persists-tray-state");
                }
                else if (phase == "hidden")
                {
                    var native = new NativeWindowSystem();
                    var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
                    WindowSnapshot? snapshot;
                    do
                    {
                        await Task.Delay(100);
                        snapshot = native.EnumerateWindows().FirstOrDefault(w => w.Title == rule.Title);
                    } while (snapshot?.Bounds != rule.Bounds && Stopwatch.GetTimestamp() < deadline);
                    Assert(snapshot?.Bounds == rule.Bounds, "hidden-startup-restores-target");
                    Assert(!window.IsVisible && visibleTransitions == 0, "no-startup-window-flash");
                    window.ShowFromTray();
                    AssertVisible(window);
                    Assert(!store.Load().StartInTray, "show-persists-window-state");
                }
                else if (phase == "shown")
                {
                    AssertVisible(window);
                    // Capture's temporary Hide() must not become a tray-start preference.
                    typeof(MainWindow).GetMethod("BeginCapture", Private)!.Invoke(window, null);
                    Assert(!store.Load().StartInTray, "capture-hide-does-not-change-preference");
                    var capture = (WindowCaptureService)typeof(MainWindow).GetField("_capture", Private)!.GetValue(window)!;
                    capture.Cancel();
                    Assert(window.IsVisible && !store.Load().StartInTray, "capture-cancel-restores-settings-window");
                }
                else throw new ArgumentException("Unknown phase");
                Assert(store.Load().Rules.Single() == rule, "window-rule-preserved");
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
                if (window is not null) typeof(MainWindow).GetMethod("ExitApplication", Private)!.Invoke(window, null);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(new { Success = success, Phase = phase, Checks = checks, Failure = failure?.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(success ? 0 : 1);
            }

            void Assert(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                checks.Add(name);
            }
            void AssertVisible(MainWindow candidate)
            {
                var handle = new WindowInteropHelper(candidate).Handle;
                Assert(candidate.IsVisible && NativeMethods.IsWindowVisible(handle), "settings-window-visible");
                if (!NativeMethods.GetWindowRect(handle, out var bounds)) throw new InvalidOperationException("No window bounds");
                var rectangle = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                Assert(System.Windows.Forms.Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(rectangle)), "settings-window-on-screen");
            }
        }));
        app.Run();
        return success ? 0 : 1;
    }
}
