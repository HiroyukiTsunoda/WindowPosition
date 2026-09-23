using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using WindowPosition.Core;

namespace WindowPosition.App.Services;

/// <summary>
/// Uses physical screen pixels for both capture and restoration. The application
/// must run with PerMonitorV2 DPI awareness before any windows are created.
/// </summary>
public sealed class NativeWindowSystem : IWindowSystem
{
    private readonly int _ownProcessId = Environment.ProcessId;

    public IReadOnlyList<WindowSnapshot> EnumerateWindows()
    {
        var windows = new List<WindowSnapshot>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            var window = GetWindow(handle);
            if (window is not null)
            {
                windows.Add(window);
            }

            return true;
        }, 0);
        return windows;
    }

    public WindowSnapshot? GetWindowAtPoint(int x, int y)
    {
        var handle = NativeMethods.WindowFromPoint(new NativeMethods.Point { X = x, Y = y });
        return GetWindow(NativeMethods.GetAncestor(handle, NativeMethods.GaRoot));
    }

    public WindowSnapshot? GetWindow(nint handle)
    {
        if (handle == 0 || !NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle)
            || handle == NativeMethods.GetShellWindow() || handle == NativeMethods.GetDesktopWindow()
            || NativeMethods.GetAncestor(handle, NativeMethods.GaRoot) != handle)
        {
            return null;
        }

        if (NativeMethods.GetWindowThreadProcessId(handle, out var processId) == 0
            || processId == _ownProcessId || processId == 0
            || (NativeMethods.GetExtendedStyle(handle) & NativeMethods.WsExToolWindow) != 0)
        {
            return null;
        }

        if (NativeMethods.DwmGetWindowAttribute(handle, NativeMethods.DwmwaCloaked, out var cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return null;
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(handle, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32769")
        {
            return null;
        }

        var length = NativeMethods.GetWindowTextLengthW(handle);
        if (length <= 0)
        {
            return null;
        }

        var title = new StringBuilder(Math.Min(length, 32767) + 1);
        if (NativeMethods.GetWindowTextW(handle, title, title.Capacity) == 0
            || !NativeMethods.GetWindowRect(handle, out var rectangle))
        {
            return null;
        }

        var width = (long)rectangle.Right - rectangle.Left;
        var height = (long)rectangle.Bottom - rectangle.Top;
        if (width <= 0 || width > int.MaxValue || height <= 0 || height > int.MaxValue
            || string.IsNullOrWhiteSpace(title.ToString()))
        {
            return null;
        }

        return new WindowSnapshot(handle, unchecked((int)processId), title.ToString(),
            new WindowBounds(rectangle.Left, rectangle.Top, (int)width, (int)height),
            NativeMethods.IsIconic(handle), NativeMethods.IsZoomed(handle));
    }

    /// <summary>Calculates an edge target from live physical-pixel bounds without moving the window.</summary>
    public bool TryGetEdgeBounds(nint handle, WindowEdge edge, out WindowBounds bounds, out string? error)
    {
        bounds = new WindowBounds(0, 0, 0, 0);
        error = null;
        var current = GetWindow(handle);
        if (current is null)
        {
            error = "対象ウィンドウが閉じられたか、操作できないウィンドウです。再度キャプチャしてください。";
            return false;
        }

        bounds = current.Bounds;
        if (current.IsMinimized)
        {
            error = "対象ウィンドウは最小化されています。通常表示に戻してから端に寄せてください。";
            return false;
        }

        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfoW(monitor, ref info)
            || !TryReadBounds(info.Work, out var workArea))
        {
            error = "対象モニターの作業領域を取得できませんでした。再度お試しください。";
            return false;
        }

        var visible = current.Bounds;
        if (NativeMethods.DwmGetWindowRectAttribute(handle, NativeMethods.DwmwaExtendedFrameBounds,
                out var frame, Marshal.SizeOf<NativeMethods.Rect>()) == 0
            && TryReadBounds(frame, out var frameBounds))
        {
            visible = frameBounds;
        }

        return WindowAlignment.TryAlign(current.Bounds, visible, workArea, edge, out bounds, out error);
    }

    /// <summary>
    /// Returns true when the target already matches or Windows accepts the
    /// asynchronous move request. A target application may impose its own minimum size.
    /// </summary>
    public bool TrySetBounds(nint handle, WindowBounds bounds, out string? error)
    {
        error = null;
        if (bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue)
        {
            error = "幅と高さには正の整数を指定し、座標が Windows の範囲内に収まるようにしてください。";
            return false;
        }

        var current = GetWindow(handle);
        if (current is null)
        {
            error = "対象ウィンドウが閉じられたか、操作できないウィンドウです。再度キャプチャしてください。";
            return false;
        }

        if (current.IsMinimized)
        {
            error = "対象ウィンドウは最小化されています。通常表示に戻すと適用できます。";
            return false;
        }

        var target = NormalizeBounds(bounds);
        if (!current.IsMaximized && current.Bounds == target)
        {
            return true;
        }

        // The two requests are posted to the target UI queue; neither activates
        // its window nor waits for an unresponsive target application.
        if (current.IsMaximized && !NativeMethods.ShowWindowAsync(handle, NativeMethods.SwShowNoActivate))
        {
            error = DescribeNativeFailure("最大化状態を解除できませんでした");
            return false;
        }

        if (!NativeMethods.SetWindowPos(handle, 0, target.X, target.Y, target.Width, target.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate
                | NativeMethods.SwpNoOwnerZOrder | NativeMethods.SwpAsyncWindowPos))
        {
            error = DescribeNativeFailure("位置とサイズを適用できませんでした");
            return false;
        }

        return true;
    }

    public WindowBounds NormalizeBounds(WindowBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0
            || (long)bounds.X + bounds.Width > int.MaxValue
            || (long)bounds.Y + bounds.Height > int.MaxValue)
        {
            return bounds;
        }

        var rectangle = new NativeMethods.Rect
        {
            Left = bounds.X,
            Top = bounds.Y,
            Right = bounds.X + bounds.Width,
            Bottom = bounds.Y + bounds.Height,
        };

        // Require a reachable part of the title area, not a one-pixel resize
        // border. A maximized window on a disconnected adjacent monitor can
        // leave its invisible border overlapping the remaining screen.
        var inset = Math.Min(64, bounds.Width / 4);
        var titleArea = new NativeMethods.Rect
        {
            Left = rectangle.Left + inset,
            Right = rectangle.Right - inset,
            Top = rectangle.Top + Math.Min(8, bounds.Height / 4),
            Bottom = rectangle.Top + Math.Min(40, bounds.Height),
        };
        if (NativeMethods.MonitorFromRect(ref titleArea, NativeMethods.MonitorDefaultToNull) != 0)
        {
            return bounds;
        }

        var monitor = NativeMethods.MonitorFromRect(ref rectangle, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == 0 || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return bounds;
        }

        var width = Math.Min(bounds.Width, Math.Max(1, info.Work.Right - info.Work.Left));
        var height = Math.Min(bounds.Height, Math.Max(1, info.Work.Bottom - info.Work.Top));
        return new WindowBounds(
            Math.Clamp(bounds.X, info.Work.Left, info.Work.Right - width),
            Math.Clamp(bounds.Y, info.Work.Top, info.Work.Bottom - height), width, height);
    }

    private static bool TryReadBounds(NativeMethods.Rect rectangle, out WindowBounds bounds)
    {
        var width = (long)rectangle.Right - rectangle.Left;
        var height = (long)rectangle.Bottom - rectangle.Top;
        bounds = new WindowBounds(0, 0, 0, 0);
        if (width <= 0 || width > int.MaxValue || height <= 0 || height > int.MaxValue)
        {
            return false;
        }

        bounds = new WindowBounds(rectangle.Left, rectangle.Top, (int)width, (int)height);
        return true;
    }

    private static string DescribeNativeFailure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return error == 5
            ? $"{message}。対象が管理者権限で動作している場合は、WindowPosition も管理者として起動してください。"
            : $"{message}。対象アプリが応答しているか確認してください。"
              + (error == 0 ? string.Empty : $" ({error}: {new Win32Exception(error).Message})");
    }
}
