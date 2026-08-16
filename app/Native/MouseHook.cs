using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopPetUi.Native;

public sealed class MouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    public event Action<POINT>? MouseMoved;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private HookProc? _proc;
    private IntPtr _hookId;
    private bool _disposed;

    public bool IsRunning => _hookId != IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public bool Start()
    {
        if (_hookId != IntPtr.Zero) return true;
        _proc = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var hModule = GetModuleHandle(module?.ModuleName);
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hModule, 0);
        return _hookId != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_MOUSEMOVE && GetCursorPos(out var pt))
        {
            MouseMoved?.Invoke(pt);
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _proc = null;
    }
}
