using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RedMLocalClient;

public sealed record DisplayInfo(int Index, string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary)
{
    public override string ToString() => $"{Index + 1}: {DeviceName} · {Width} × {Height}{(IsPrimary ? " · Primary" : "")}";
}

public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

public static class DesktopDisplays
{
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DisplayInfo>();
        var displays = new List<DisplayInfo>();
        var enumerationError = 0;
        MonitorEnum callback = (IntPtr monitor, IntPtr _, ref NativeRect _, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (!GetMonitorInfo(monitor, ref info)) { enumerationError = Marshal.GetLastWin32Error(); return false; }
            displays.Add(new DisplayInfo(displays.Count, info.Device, info.Bounds.Left, info.Bounds.Top,
                info.Bounds.Right - info.Bounds.Left, info.Bounds.Bottom - info.Bounds.Top, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            throw new Win32Exception(enumerationError == 0 ? Marshal.GetLastWin32Error() : enumerationError);
        return displays;
    }

    public static DisplayInfo? Select(IReadOnlyList<DisplayInfo> displays, string deviceName, int? index)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
            return displays.FirstOrDefault(display => display.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Selected display {deviceName} is no longer connected. Select a display again.");
        if (index is { } selected)
            return displays.FirstOrDefault(display => display.Index == selected)
                ?? throw new InvalidOperationException("Selected display is no longer connected. Select a display again.");
        return null;
    }

    public static WindowBounds CalculatePlacement(DisplayInfo display, int width, int height)
    {
        if (width <= 0 || height <= 0 || display.Width <= 0 || display.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Window and display dimensions must be positive.");
        return new WindowBounds(display.Left + Math.Max(0, (display.Width - width) / 2),
            display.Top + Math.Max(0, (display.Height - height) / 2), width, height);
    }

    public static void PlaceWindow(IntPtr window, DisplayInfo display, int clientWidth, int clientHeight, WindowMode mode)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Display placement requires Windows.");
        if (window == IntPtr.Zero) throw new ArgumentException("The game window is not ready.", nameof(window));
        const uint noZOrder = 0x0004, noActivate = 0x0010, noSize = 0x0001;
        if (mode is WindowMode.Fullscreen or WindowMode.Preserve)
        {
            if (!SetWindowPos(window, IntPtr.Zero, display.Left, display.Top, 0, 0, noZOrder | noActivate | noSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        var rect = new NativeRect { Right = clientWidth, Bottom = clientHeight };
        var style = unchecked((uint)GetWindowLong(window, -16));
        var extendedStyle = unchecked((uint)GetWindowLong(window, -20));
        var dpi = GetDpiForWindow(window);
        if (!AdjustWindowRectExForDpi(ref rect, style, false, extendedStyle, dpi == 0 ? 96u : dpi))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var placement = CalculatePlacement(display, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (!SetWindowPos(window, IntPtr.Zero, placement.Left, placement.Top, placement.Width, placement.Height, noZOrder | noActivate))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref NativeRect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
}
