using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FreeAutocorrect.Services;

public class TextAutomationService
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_BACK = 0x08;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public int type;
        [FieldOffset(8)]
        public MOUSEINPUT mi;
        [FieldOffset(8)]
        public KEYBDINPUT ki;
        [FieldOffset(8)]
        public HARDWAREINPUT hi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static (bool ctrl, bool shift, bool alt) ReleaseModifiers()
    {
        bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;

        if (ctrl) keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (shift) keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (alt) keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

        if (ctrl || shift || alt) Thread.Sleep(15);
        return (ctrl, shift, alt);
    }

    private static void RestoreModifiers((bool ctrl, bool shift, bool alt) mods)
    {
        if (mods.ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
        if (mods.shift) keybd_event(0x10, 0, 0, UIntPtr.Zero);
        if (mods.alt) keybd_event(0x12, 0, 0, UIntPtr.Zero);
    }

    public static void ReplacePreviousWord(int distanceFromCaret, int typoLength, string replacement, char trailingChar = '\0', IntPtr targetHwnd = default)
    {
        if (targetHwnd != IntPtr.Zero)
        {
            SetForegroundWindow(targetHwnd);
            Thread.Sleep(35);
        }

        if (distanceFromCaret <= 0)
        {
            ReplaceText(typoLength, replacement, trailingChar, targetHwnd);
            return;
        }

        var mods = ReleaseModifiers();

        const ushort VK_BACK = 0x08;
        const ushort VK_LEFT = 0x25;
        const ushort VK_RIGHT = 0x27;

        var inputs = new List<INPUT>();

        // 1. Move caret left to end of typo word
        for (int i = 0; i < distanceFromCaret; i++)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_LEFT, dwFlags = 0 } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_LEFT, dwFlags = KEYEVENTF_KEYUP } });
        }

        // 2. Delete the typo word with atomic backspaces
        for (int i = 0; i < typoLength; i++)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, dwFlags = 0 } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, dwFlags = KEYEVENTF_KEYUP } });
        }

        // 3. Type replacement text via Unicode SendInput
        foreach (char c in replacement)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
        }

        // 4. Move caret forward right back to where user was actively typing
        for (int i = 0; i < distanceFromCaret; i++)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_RIGHT, dwFlags = 0 } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_RIGHT, dwFlags = KEYEVENTF_KEYUP } });
        }

        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        if (sent == 0)
        {
            // Fallback if needed
            TypeViaKeybdEvent(replacement, '\0');
        }

        RestoreModifiers(mods);
    }

    public static void ReplaceText(int backspaceCount, string replacement, char trailingChar, IntPtr targetHwnd = default)
    {
        var mods = ReleaseModifiers();
        var inputs = new List<INPUT>();

        // 1. Queue all backspaces atomically into SendInput (prevents user typing from interleaving)
        for (int i = 0; i < backspaceCount; i++)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = VK_BACK, dwFlags = 0 }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = VK_BACK, dwFlags = KEYEVENTF_KEYUP }
            });
        }

        // 2. Queue replacement characters atomically via Unicode
        foreach (char c in replacement)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            });
        }

        // 3. Queue the trailing delimiter
        if (trailingChar != '\0')
        {
            if (trailingChar == '\r' || trailingChar == '\n')
            {
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = 0 } });
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x0D, dwFlags = KEYEVENTF_KEYUP } });
            }
            else if (trailingChar == '\t')
            {
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x09, dwFlags = 0 } });
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0x09, dwFlags = KEYEVENTF_KEYUP } });
            }
            else
            {
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE }
                });
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
                });
            }
        }

        // 4. Send EVERYTHING in one atomic Win32 call!
        uint sent = 0;
        if (inputs.Count > 0)
        {
            sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        // Fallback: if atomic SendInput failed, fallback to sequential typing
        if (sent == 0)
        {
            for (int i = 0; i < backspaceCount; i++)
            {
                keybd_event((byte)VK_BACK, 0, 0, UIntPtr.Zero);
                keybd_event((byte)VK_BACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                Thread.Sleep(3);
            }
            TypeViaKeybdEvent(replacement, trailingChar);
        }

        RestoreModifiers(mods);
    }

    public static void AppendSuffix(string suffix, char trailingChar = ' ', IntPtr targetHwnd = default)
    {
        if (string.IsNullOrEmpty(suffix) && trailingChar == '\0') return;

        var mods = ReleaseModifiers();
        var inputs = new List<INPUT>();

        foreach (char c in suffix)
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            });
        }

        if (trailingChar != '\0')
        {
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE }
            });
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            });
        }

        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        if (sent == 0)
        {
            TypeViaKeybdEvent(suffix, trailingChar);
        }

        RestoreModifiers(mods);
    }

    private static void TypeViaKeybdEvent(string text, char trailingChar)
    {
        string full = text + (trailingChar != '\0' ? trailingChar.ToString() : "");
        foreach (char ch in full)
        {
            if (ch == '\r' || ch == '\n')
            {
                keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            else if (ch == '\t')
            {
                keybd_event(0x09, 0, 0, UIntPtr.Zero);
                keybd_event(0x09, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            else
            {
                short scan = VkKeyScan(ch);
                if (scan != -1)
                {
                    byte vk = (byte)(scan & 0xFF);
                    byte shift = (byte)((scan >> 8) & 0xFF);
                    if ((shift & 1) != 0) keybd_event(0x10, 0, 0, UIntPtr.Zero);
                    keybd_event(vk, 0, 0, UIntPtr.Zero);
                    keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    if ((shift & 1) != 0) keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
            }
            Thread.Sleep(3);
        }
    }

    public static string GetSelectedTextFromActiveApp()
    {
        // Try getting text via Clipboard copy simulation
        string originalClipboard = string.Empty;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                originalClipboard = System.Windows.Clipboard.GetText();
            }
        }
        catch { }

        try
        {
            // Send Ctrl+C
            var inputs = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_C, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_C, dwFlags = KEYEVENTF_KEYUP } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

            Thread.Sleep(120);

            string selected = string.Empty;
            if (System.Windows.Clipboard.ContainsText())
            {
                selected = System.Windows.Clipboard.GetText();
            }

            return selected;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void ReplaceSelectedTextInActiveApp(string newText)
    {
        try
        {
            System.Windows.Clipboard.SetText(newText);
            Thread.Sleep(50);

            // Send Ctrl+V
            var inputs = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }
        catch { }
    }
}
