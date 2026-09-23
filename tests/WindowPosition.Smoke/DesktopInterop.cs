using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowPosition.Smoke;

internal static class DesktopInterop
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;

    internal static Rectangle Bounds(nint handle)
    {
        if (!GetWindowRect(handle, out Rect rect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static Rectangle VisibleFrame(nint handle)
    {
        // Use DWM directly as the independent oracle for the displayed border;
        // GetWindowRect includes the invisible resize border on modern Windows.
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(handle, 9, out Rect rect, Marshal.SizeOf<Rect>()));
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    internal static Rectangle WorkingArea(nint handle)
    {
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    internal static void Move(nint handle, Rectangle bounds)
    {
        if (!SetWindowPos(handle, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate | SwpNoZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static nint RootWindowAtPoint(Point point) => GetAncestor(WindowFromPoint(point), 2);

    internal static bool Responsive(nint handle) =>
        SendMessageTimeout(handle, 0, 0, 0, 2, 1500, out _) != 0;

    internal static (string Station, string Desktop) DesktopNames() =>
        (ObjectName(GetProcessWindowStation()), ObjectName(GetThreadDesktop(GetCurrentThreadId())));

    private static string ObjectName(nint handle)
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, name, name.Capacity * sizeof(char), out _)
            ? name.ToString()
            : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void LeftClick(Point position)
    {
        SetCursorPos(position.X, position.Y);
        Input[] inputs = [new() { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0002 } } },
                          new() { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x0004 } } }];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void Escape()
    {
        Input[] inputs = [new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x1B } } },
                          new() { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = 0x1B, Flags = 0x0002 } } }];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void MouseWheel(Point position, int delta)
    {
        if (!SetCursorPos(position.X, position.Y))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Input[] inputs = [new()
        {
            Type = 0,
            Data = new InputUnion { Mouse = new MouseInput { MouseData = unchecked((uint)delta), Flags = 0x0800 } },
        }];
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out Rect rect, int size);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint handle, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")]
    internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string? className, string? title);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint handle, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder data, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
