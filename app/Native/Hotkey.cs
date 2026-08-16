using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopPetUi.Native;

public sealed class Hotkey : IDisposable
{
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id;
    private bool _registered;

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    public Hotkey(HwndSource source, uint modifiers, uint vk, int id = 0xB007)
    {
        _source = source;
        _id = id;
        _registered = RegisterHotKey(source.Handle, _id, modifiers, vk);
        if (_registered) source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            try { UnregisterHotKey(_source.Handle, _id); } catch { }
            try { _source.RemoveHook(WndProc); } catch { }
        }
    }

    public static bool TryParse(string modifiers, string keyName, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(keyName) || !KeyMap.TryGetValue(keyName.Trim().ToLowerInvariant(), out vk))
            return false;
        if (!string.IsNullOrWhiteSpace(modifiers))
        {
            foreach (var part in modifiers.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                switch (part.ToLowerInvariant())
                {
                    case "alt": mods |= MOD_ALT; break;
                    case "ctrl": mods |= MOD_CONTROL; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win": mods |= MOD_WIN; break;
                    case "none": mods = 0; break;
                }
            }
        }
        return true;
    }

    private static readonly Dictionary<string, uint> KeyMap = BuildKeyMap();

    private static Dictionary<string, uint> BuildKeyMap()
    {
        var map = new Dictionary<string, uint>();
        for (var i = 0; i < 26; i++) map[((char)('a' + i)).ToString()] = (uint)('A' + i);
        for (var i = 0; i < 10; i++) map[i.ToString()] = (uint)('0' + i);
        map["space"] = 0x20; map["enter"] = 0x0D; map["esc"] = 0x1B; map["escape"] = 0x1B;
        map["tab"] = 0x09; map["backspace"] = 0x08; map["delete"] = 0x2E; map["insert"] = 0x2D;
        map["home"] = 0x24; map["end"] = 0x23; map["pageup"] = 0x21; map["pagedown"] = 0x22;
        map["left"] = 0x25; map["up"] = 0x26; map["right"] = 0x27; map["down"] = 0x28;
        for (var i = 1; i <= 24; i++) map["f" + i] = (uint)(0x6F + i);
        return map;
    }
}