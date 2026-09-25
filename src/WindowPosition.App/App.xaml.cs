using System.IO;
using System.Windows;
using WindowPosition.Core;

namespace WindowPosition.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    internal static DiagnosticLog Log { get; } = new(Path.Combine(AppContext.BaseDirectory, "WindowPosition.log"));
    private string _exitReason = "ApplicationShutdown";
    internal bool IsSessionEnding { get; private set; }

    internal void NoteExitReason(string reason)
    {
        _exitReason = reason;
        Log.Write("EXIT_REQUEST", reason);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, @"Local\WindowPosition.Singleton", out var created);
        if (!created)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\WindowPosition.Show"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        // Secondary invocations must not overwrite the resident process's lifecycle.
        Log.BeginSession($"version={typeof(App).Assembly.GetName().Version}; runtime={Environment.Version}; baseDirectory={AppContext.BaseDirectory}");
        DispatcherUnhandledException += (_, args) => Log.Fatal("WPF Dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal($"AppDomain; terminating={args.IsTerminating}", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Write("UNOBSERVED_TASK_EXCEPTION", "Task exception (not necessarily fatal)", args.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.Complete("RuntimeProcessExit", Environment.ExitCode);
        System.Windows.Forms.Application.ThreadException += (_, args) =>
        {
            Log.Fatal("Windows Forms thread", args.Exception);
            Shutdown(1);
        };
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WindowPosition.Show");
        var window = new MainWindow();
        MainWindow = window;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            Log.Write("SECOND_INSTANCE_REQUEST", "Show existing settings window");
            try
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(window.ShowFromTray);
            }
            catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted) { }
        }, null, Timeout.Infinite, false);
        window.RestoreStartupVisibility();
        Log.Write("READY", $"startupVisibility={(window.StartInTray ? "tray" : "window")}; tray and tracking initialized");
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
        NoteExitReason("WindowsSessionEnding:" + e.ReasonSessionEnding);
        base.OnSessionEnding(e);
        if (e.Cancel)
        {
            IsSessionEnding = false;
            Log.Write("SESSION_END_CANCELLED");
            _exitReason = "ApplicationShutdown";
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        base.OnExit(e);
        Log.Complete(_exitReason, e.ApplicationExitCode);
        _instance?.Dispose();
    }
}
