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
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)]
        public int type;
        [FieldOffset(8)]
        public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void ReplaceText(int backspaceCount, string replacement, char trailingChar)
    {
        // 1. Send Backspaces via keybd_event for universal compatibility across all apps
        for (int i = 0; i < backspaceCount; i++)
        {
            keybd_event((byte)VK_BACK, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VK_BACK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        Thread.Sleep(10);

        var textInputs = new List<INPUT>();

        // 2. Type replacement word via Unicode SendInput
        foreach (char c in replacement)
        {
            textInputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE }
            });
            textInputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            });
        }

        // 3. Re-type the trailing delimiter
        if (trailingChar != '\0')
        {
            textInputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE }
            });
            textInputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            });
        }

        if (textInputs.Count > 0)
        {
            SendInput((uint)textInputs.Count, textInputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
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
