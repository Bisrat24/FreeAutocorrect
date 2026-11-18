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

    private const int HOTKEY_CHECK_ID = 9001;  // Ctrl + Shift + G
    private const int HOTKEY_FILTER_ID = 9002; // Ctrl + Shift + F

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowHandle = IntPtr.Zero;
    private HwndSource? _source;

    /// <summary>Fired when Ctrl+Shift+G is pressed to audit selected text.</summary>
    public event Action? OnHotkeyPressed;

    /// <summary>Fired when Ctrl+Shift+F is pressed to toggle app filter mode on the fly.</summary>
    public event Action? OnFilterHotkeyPressed;

    public void Register(IntPtr hWnd)
    {
        _windowHandle = hWnd;
        _source = HwndSource.FromHwnd(hWnd);
        _source?.AddHook(HwndHook);

        uint modifiers = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;

        // Register Ctrl + Shift + G (vk = 0x47)
        RegisterHotKey(_windowHandle, HOTKEY_CHECK_ID, modifiers, 0x47);

        // Register Ctrl + Shift + F (vk = 0x46)
        RegisterHotKey(_windowHandle, HOTKEY_FILTER_ID, modifiers, 0x46);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_CHECK_ID)
            {
                handled = true;
                OnHotkeyPressed?.Invoke();
            }
            else if (id == HOTKEY_FILTER_ID)
            {
                handled = true;
                OnFilterHotkeyPressed?.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    public void TriggerFilterHotkeyManual()
    {
        OnFilterHotkeyPressed?.Invoke();
    }

    public void TriggerGrammarHotkeyManual()
    {
        OnHotkeyPressed?.Invoke();
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_CHECK_ID);
            UnregisterHotKey(_windowHandle, HOTKEY_FILTER_ID);
            _source?.RemoveHook(HwndHook);
            _windowHandle = IntPtr.Zero;
        }
    }
}
