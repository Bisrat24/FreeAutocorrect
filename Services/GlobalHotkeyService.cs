using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;

namespace FreeAutocorrect.Services;

public class GlobalHotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HOTKEY_ID = 9001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle = IntPtr.Zero;
    private HwndSource? _source;
    public event Action? OnHotkeyPressed;

    public void Register(IntPtr hWnd)
    {
        _windowHandle = hWnd;
        _source = HwndSource.FromHwnd(hWnd);
        _source?.AddHook(HwndHook);

        // Register Ctrl + Shift + G
        uint modifiers = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
        uint vk = 0x47; // 'G' key
        RegisterHotKey(_windowHandle, HOTKEY_ID, modifiers, vk);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            handled = true;
            OnHotkeyPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _source?.RemoveHook(HwndHook);
            _windowHandle = IntPtr.Zero;
        }
    }
}
