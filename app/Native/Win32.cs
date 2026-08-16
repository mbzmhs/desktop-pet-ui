using System;
using System.Runtime.InteropServices;

namespace DesktopPetUi.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool Contains(POINT p) =>
        p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}

public static class WindowUtil
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static RECT? GetRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowRect(hwnd, out RECT r) ? r : null;
    }

    public static bool SetPosition(IntPtr hwnd, int x, int y, bool topmost, bool noActivate = true)
    {
        if (hwnd == IntPtr.Zero) return false;
        var flags = SWP_NOSIZE | (noActivate ? SWP_NOACTIVATE : 0);
        var insertAfter = topmost ? HWND_TOPMOST : IntPtr.Zero;
        return SetWindowPos(hwnd, insertAfter, x, y, 0, 0, flags);
    }
}

public static class CursorUtil
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public static POINT GetPosition()
    {
        GetCursorPos(out POINT p);
        return p;
    }
}

public static class ClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    public static IntPtr GetStylePtr(IntPtr hwnd, int index = GWL_EXSTYLE)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    }

    public static void SetStylePtr(IntPtr hwnd, int index, IntPtr style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, index, style);
        else SetWindowLong32(hwnd, index, style.ToInt32());
    }

    private static IntPtr GetStyle(IntPtr hwnd) => GetStylePtr(hwnd, GWL_EXSTYLE);

    private static void SetStyle(IntPtr hwnd, IntPtr style) => SetStylePtr(hwnd, GWL_EXSTYLE, style);

    public static bool IsPassThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return (GetStyle(hwnd).ToInt64() & WS_EX_TRANSPARENT) != 0;
    }

    public static void SetPassThrough(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;
        var style = GetStyle(hwnd).ToInt64();
        var next = enabled ? (style | WS_EX_TRANSPARENT) : (style & ~WS_EX_TRANSPARENT);
        if (next != style) SetStyle(hwnd, new IntPtr(next));
    }
}
