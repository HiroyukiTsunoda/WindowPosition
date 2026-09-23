using System.IO;
using System.Windows;

namespace WindowPosition.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
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
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WindowPosition.Show");
        var window = new MainWindow();
        MainWindow = window;
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
