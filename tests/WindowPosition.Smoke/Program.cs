using System.IO;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace WindowPosition.Smoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (Option(args, "--target") is { } title)
        {
            Application.Run(new TargetForm(title));
            return 0;
        }

        var output = Path.GetFullPath(Option(args, "--output") ?? "artifacts/desktop-smoke.json");
        var context = new DriverContext(output, Option(args, "--check-app"), Option(args, "--check-wheel"));
        var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            await context.RunAsync();
            application.Shutdown(context.Success ? 0 : 1);
        }));
        return application.Run();
    }

    private static string? Option(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed class TargetForm : Form
    {
        internal TargetForm(string title)
        {
            Text = title;
            // Only this owned temporary target is topmost. A failed foreground
            // request must never make capture register an unrelated real app.
            TopMost = true;
            var area = Screen.PrimaryScreen!.WorkingArea;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(area.X + 80, area.Y + 80, 520, 340);
            var content = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Window Position integration test\nThis temporary window closes automatically.",
                Font = new Font("Segoe UI", 12),
            };
            content.Click += (_, _) => Text = title + " (selection click reached target)";
            Controls.Add(content);
        }
    }

    private sealed class DriverContext
    {
        private readonly string _output;
        private readonly string? _applicationTitle;
        private readonly string? _wheelApplicationTitle;
        private readonly List<Check> _checks = [];
        private string? _screenshotPath;

        internal DriverContext(string output, string? applicationTitle, string? wheelApplicationTitle)
        {
            _output = output;
            _applicationTitle = applicationTitle;
            _wheelApplicationTitle = wheelApplicationTitle;
        }

        internal bool Success { get; private set; }

        internal async Task RunAsync()
        {
            Exception? failure = null;
            var started = DateTimeOffset.Now;
            try
            {
                var desktop = DesktopInterop.DesktopNames();
                Assert("normal-user-desktop", desktop.Station.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) && desktop.Desktop.Equals("Default", StringComparison.OrdinalIgnoreCase), $"{desktop.Station}\\{desktop.Desktop}");
                if (_wheelApplicationTitle is not null)
                {
                    var handle = DesktopInterop.FindWindow(null, _wheelApplicationTitle);
                    Assert("wheel-application-window-found", handle != 0, _wheelApplicationTitle);
                    CheckVisibleWindow(handle, "wheel-application");
                    DesktopInterop.GetWindowThreadProcessId(handle, out var processId);
                    using var application = Process.GetProcessById(checked((int)processId));
                    var executable = application.MainModule?.FileName
                        ?? throw new InvalidOperationException("Could not identify application executable");
                    await WheelUiChecks.RunAsync(handle, executable, Assert);
                    CheckVisibleWindow(handle, "wheel-application-after-check");
                }
                else if (_applicationTitle is not null)
                {
                    await CheckApplicationAsync();
                }
                else
                {
                    await RunIntegrationAsync();
                }
                Success = true;
            }
            catch (Exception exception)
            {
                failure = exception;
                Success = false;
            }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
                File.WriteAllText(_output, JsonSerializer.Serialize(new
                {
                    Success,
                    StartedAt = started,
                    CompletedAt = DateTimeOffset.Now,
                    Checks = _checks,
                    ScreenshotPath = _screenshotPath,
                    Failure = failure?.ToString(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private void Assert(string name, bool passed, string details)
        {
            _checks.Add(new Check(name, passed, details));
            if (!passed)
                throw new InvalidOperationException($"{name}: {details}");
        }

        private void CheckVisibleWindow(nint handle, string name)
        {
            Assert($"{name}-visible", DesktopInterop.IsWindowVisible(handle), $"HWND=0x{handle:X}");
            var rectangle = DesktopInterop.Bounds(handle);
            Assert($"{name}-on-screen", Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(rectangle)), rectangle.ToString());
            Assert($"{name}-responsive", DesktopInterop.Responsive(handle), "WM_NULL replied within 1500 ms");
        }

        private async Task CheckApplicationAsync()
        {
            var handle = DesktopInterop.FindWindow(null, _applicationTitle);
            Assert("application-window-found", handle != 0, _applicationTitle!);
            CheckVisibleWindow(handle, "application");
            DesktopInterop.GetWindowThreadProcessId(handle, out var processId);
            using var application = Process.GetProcessById(checked((int)processId));
            var executable = application.MainModule?.FileName
                ?? throw new InvalidOperationException("Could not identify application executable");

            Assert("close-request-posted", DesktopInterop.PostMessage(handle, 0x0010, 0, 0), "WM_CLOSE sent to Window Position only");
            await WaitForAsync(() => !DesktopInterop.IsWindowVisible(handle) || application.HasExited);
            Assert("close-keeps-resident-process", !application.HasExited, $"PID={processId}");
            Assert("close-hides-settings-window", !DesktopInterop.IsWindowVisible(handle), $"HWND=0x{handle:X}");

            using var second = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
                ?? throw new InvalidOperationException("Could not run second application invocation");
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert("second-instance-exits", second.ExitCode == 0, $"Second PID={second.Id}; ExitCode={second.ExitCode}");
            await WaitForAsync(() => DesktopInterop.IsWindowVisible(handle));
            DesktopInterop.GetWindowThreadProcessId(handle, out var restoredProcessId);
            Assert("second-instance-restores-resident-window", restoredProcessId == processId && !application.HasExited,
                $"Original and restored PID={processId}");
            CheckVisibleWindow(handle, "application-restored");
            await AppUiChecks.RunAsync(handle, executable, Assert);
            DesktopInterop.SetForegroundWindow(handle);
            await Task.Delay(250);
            var bounds = DesktopInterop.Bounds(handle);
            using var screenshot = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(screenshot))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            _screenshotPath = Path.ChangeExtension(_output, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(_screenshotPath)!);
            screenshot.Save(_screenshotPath, ImageFormat.Png);
            Assert("application-screenshot-created", File.Exists(_screenshotPath), _screenshotPath);
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            var started = Stopwatch.StartNew();
            while (!condition())
            {
                if (started.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException("Application did not reach expected state within 5 seconds");
                await Task.Delay(50);
            }
        }

        private async Task RunIntegrationAsync()
        {
            // Native and engine integration checks are implemented in IntegrationChecks.cs.
            await IntegrationChecks.RunAsync(Assert, CheckVisibleWindow);
        }

    }

    private sealed record Check(string Name, bool Passed, string Details);
}
