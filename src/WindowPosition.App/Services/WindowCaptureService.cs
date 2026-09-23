using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WindowPosition.Core;

namespace WindowPosition.App.Services;

/// <summary>
/// Short-lived, UI-thread mouse hook. Only the selection click is swallowed;
/// application callbacks and window inspection run outside the native callback.
/// </summary>
public sealed class WindowCaptureService : IDisposable
{
    private readonly NativeWindowSystem _windowSystem;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _safetyTimer;
    // This strong reference must outlive the native hook.
    private readonly NativeMethods.HookCallback _hookCallback;
    private readonly NativeMethods.HookCallback _keyboardHookCallback;
    private nint _hook;
    private nint _keyboardHook;
    private nint _selectedWindow;
    private long _expiresAt;
    private int _generation;
    private bool _waitingForRelease;
    private bool _completionQueued;
    private bool _disposed;

    public WindowCaptureService(NativeWindowSystem windowSystem, Dispatcher? dispatcher = null)
    {
        _windowSystem = windowSystem;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _hookCallback = OnMouse;
        _keyboardHookCallback = OnKeyboard;
        _safetyTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(75),
        };
        _safetyTimer.Tick += OnSafetyTick;
    }

    public event EventHandler<WindowSnapshot>? Captured;
    public event EventHandler<string>? Failed;
    public event EventHandler? Cancelled;

    public bool IsCapturing => _hook != 0;
    public string? LastCancellationReason { get; private set; }

    public void Start()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing)
        {
            return;
        }

        LastCancellationReason = null;
        _generation++;
        _waitingForRelease = false;
        _completionQueued = false;
        _selectedWindow = 0;
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _hookCallback,
            NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Failed?.Invoke(this, $"キャプチャを開始できませんでした。({error}: {new Win32Exception(error).Message})");
            return;
        }

        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardHookCallback,
            NativeMethods.GetModuleHandleW(null), 0);
        if (_keyboardHook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            StopCapture();
            Failed?.Invoke(this, $"Esc キーの監視を開始できませんでした。({error}: {new Win32Exception(error).Message})");
            return;
        }

        _expiresAt = Environment.TickCount64 + 20_000;
        _safetyTimer.Start();
    }

    public void Cancel()
    {
        _dispatcher.VerifyAccess();
        CancelWithReason("キャプチャをキャンセルしました。");
    }

    public void Dispose()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(Dispose);
            return;
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCapture();
        _safetyTimer.Tick -= OnSafetyTick;
        GC.SuppressFinalize(this);
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code < 0 || _hook == 0 || _completionQueued)
        {
            return NativeMethods.CallNextHookEx(_hook, code, message, data);
        }

        try
        {
            if ((int)message == NativeMethods.WmLeftButtonDown)
            {
                var mouse = Marshal.PtrToStructure<NativeMethods.MouseHookData>(data);
                var hit = NativeMethods.WindowFromPoint(mouse.Point);
                var root = NativeMethods.GetAncestor(hit, NativeMethods.GaRoot);
                NativeMethods.GetWindowThreadProcessId(root, out var processId);
                if (processId == Environment.ProcessId)
                {
                    // Own controls, including Cancel, retain normal behavior.
                    return NativeMethods.CallNextHookEx(_hook, code, message, data);
                }

                _selectedWindow = root;
                _waitingForRelease = true;
                return 1;
            }

            if ((int)message == NativeMethods.WmLeftButtonUp && _waitingForRelease)
            {
                _completionQueued = true;
                var selectedWindow = _selectedWindow;
                var generation = _generation;
                _dispatcher.BeginInvoke(DispatcherPriority.Input,
                    new Action(() => CompleteCapture(selectedWindow, generation)));
                return 1;
            }
        }
        catch (Exception)
        {
            // No managed exception may cross the unmanaged hook boundary.
            StopCapture();
            QueueHookFailure();
        }

        return NativeMethods.CallNextHookEx(_hook, code, message, data);
    }

    private nint OnKeyboard(int code, nint message, nint data)
    {
        if (code < 0 || _hook == 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
        }

        try
        {
            var keyboard = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
            if (keyboard.VirtualKeyCode == NativeMethods.VkEscape)
            {
                if ((int)message is NativeMethods.WmKeyDown or NativeMethods.WmSystemKeyDown)
                {
                    if (!_completionQueued)
                    {
                        _completionQueued = true;
                        var generation = _generation;
                        _dispatcher.BeginInvoke(DispatcherPriority.Input,
                            new Action(() => CancelWithReason("Esc キーでキャプチャをキャンセルしました。", generation)));
                    }

                    return 1;
                }

                if (_completionQueued && (int)message is NativeMethods.WmKeyUp or NativeMethods.WmSystemKeyUp)
                {
                    return 1;
                }
            }
        }
        catch (Exception)
        {
            StopCapture();
            QueueHookFailure();
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void QueueHookFailure()
    {
        // Dispatcher shutdown can race input. Reporting must never throw back
        // through a native hook even when no dispatcher is left to receive it.
        try
        {
            if (!_dispatcher.HasShutdownStarted)
            {
                var generation = _generation;
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation == _generation && !_disposed)
                    {
                        Failed?.Invoke(this, "キャプチャを中断しました。もう一度キャプチャモードを開始してください。");
                    }
                }));
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CompleteCapture(nint handle, int generation)
    {
        if (!IsCapturing || _disposed || generation != _generation)
        {
            return;
        }

        StopCapture();
        var snapshot = _windowSystem.GetWindow(handle);
        if (snapshot is null || snapshot.IsMinimized)
        {
            Failed?.Invoke(this, "タイトルのある通常のアプリウィンドウを選んでください。デスクトップやタスクバーは登録できません。");
            return;
        }

        Captured?.Invoke(this, snapshot);
    }

    private void OnSafetyTick(object? sender, EventArgs args)
    {
        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkEscape) & 0x8000) != 0)
        {
            CancelWithReason("Esc キーでキャプチャをキャンセルしました。");
        }
        else if (Environment.TickCount64 >= _expiresAt)
        {
            CancelWithReason("20 秒経過したため、キャプチャを終了しました。");
        }
    }

    private void CancelWithReason(string reason, int? generation = null)
    {
        if (!IsCapturing || (generation.HasValue && generation.Value != _generation))
        {
            return;
        }

        LastCancellationReason = reason;
        StopCapture();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void StopCapture()
    {
        _generation++;
        _safetyTimer.Stop();
        var hook = _hook;
        _hook = 0;
        _waitingForRelease = false;
        _selectedWindow = 0;
        if (hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
        }

        var keyboardHook = _keyboardHook;
        _keyboardHook = 0;
        if (keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(keyboardHook);
        }
    }
}
