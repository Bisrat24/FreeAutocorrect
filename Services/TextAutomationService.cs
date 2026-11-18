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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static string GetProcessNameFromHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
        }
        catch { }
        return string.Empty;
    }

    public static void ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == hWnd) return;

            uint fgThread = GetWindowThreadProcessId(foreground, out _);
            uint curThread = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != curThread)
            {
                AttachThreadInput(curThread, fgThread, true);
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
                AttachThreadInput(curThread, fgThread, false);
            }
            else
            {
                SetForegroundWindow(hWnd);
                BringWindowToTop(hWnd);
            }
        }
        catch { }
    }

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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr currentFg = GetForegroundWindow();

        if (targetHwnd != IntPtr.Zero && currentFg != targetHwnd)
        {
            AppLogger.Debug("Automation", $"Target 0x{targetHwnd.ToInt64():X} not foreground (current=0x{currentFg.ToInt64():X}). Activating...");
            ActivateWindow(targetHwnd);
            Thread.Sleep(25);
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

        AppLogger.Info("Automation", $"ReplacePreviousWord START: Dist={distanceFromCaret}, TypoLen={typoLength}, Replacement='{replacement}', Trailing='{(trailingChar == '\0' ? "none" : trailingChar.ToString())}', Target=0x{targetHwnd.ToInt64():X}");

        // 1. Move caret left to end of typo word
        var leftInputs = new List<INPUT>();
        for (int i = 0; i < distanceFromCaret; i++)
        {
            leftInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_LEFT, dwFlags = 0 } });
            leftInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_LEFT, dwFlags = KEYEVENTF_KEYUP } });
        }
        SendInput((uint)leftInputs.Count, leftInputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(8);

        // 2. Delete the typo word with individual hardware backspaces (scanCode 0x0E)
        for (int i = 0; i < typoLength; i++)
        {
            var bs = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = KEYEVENTF_KEYUP } }
            };
            SendInput((uint)bs.Length, bs, Marshal.SizeOf(typeof(INPUT)));
            if (typoLength > 1) Thread.Sleep(2);
        }

        Thread.Sleep(8);

        // 3. Type replacement text via Unicode SendInput
        var textInputs = new List<INPUT>();
        foreach (char c in replacement)
        {
            textInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE } });
            textInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
        }
        if (trailingChar != '\0')
        {
            textInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE } });
            textInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)trailingChar, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } });
        }
        SendInput((uint)textInputs.Count, textInputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(8);

        // 4. Move caret forward right back to where user was actively typing
        var rightInputs = new List<INPUT>();
        for (int i = 0; i < distanceFromCaret; i++)
        {
            rightInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_RIGHT, dwFlags = 0 } });
            rightInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_RIGHT, dwFlags = KEYEVENTF_KEYUP } });
        }
        SendInput((uint)rightInputs.Count, rightInputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));

        RestoreModifiers(mods);
        sw.Stop();
        AppLogger.Info("Automation", $"ReplacePreviousWord COMPLETE in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Deletes the previous word(s) using Ctrl+Backspace - a single undoable command in most apps -
    /// then types the replacement and the suppressed delimiter. Returns false when input injection failed.
    /// </summary>
    public static bool ReplaceWordBeforeCaret(int wordCount, string replacement, char trailingChar = '\0', IntPtr targetHwnd = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr currentFg = GetForegroundWindow();

        if (targetHwnd != IntPtr.Zero && currentFg != targetHwnd)
        {
            AppLogger.Debug("Automation", $"Target 0x{targetHwnd.ToInt64():X} not foreground (current=0x{currentFg.ToInt64():X}). Activating...");
            ActivateWindow(targetHwnd);
            Thread.Sleep(25);
        }

        var mods = ReleaseModifiers();
        bool ok;

        // Ctrl+Backspace deletes the whole word at once, avoiding N separate undo steps
        // and the long backspace+delay cadence that lets keystrokes interleave.
        var deleteInputs = new List<INPUT>();
        int deletes = Math.Max(1, wordCount);
        for (int i = 0; i < deletes; i++)
        {
            deleteInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } });
            deleteInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = 0 } });
            deleteInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = KEYEVENTF_KEYUP } });
            deleteInputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } });
        }

        ok = SendInput((uint)deleteInputs.Count, deleteInputs.ToArray(), Marshal.SizeOf(typeof(INPUT))) != 0;
        if (!ok)
        {
            AppLogger.Warn("Automation", "ReplaceWordBeforeCaret: Ctrl+Backspace injection failed");
        }

        Thread.Sleep(8);

        if (ok)
        {
            ok = SendUnicodeText(replacement, trailingChar);
        }

        RestoreModifiers(mods);
        sw.Stop();
        AppLogger.Info("Automation", $"ReplaceWordBeforeCaret COMPLETE in {sw.ElapsedMilliseconds}ms (Words={deletes}, Success={ok})");
        return ok;
    }

    public static void ReplaceText(int backspaceCount, string replacement, char trailingChar, IntPtr targetHwnd = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr currentFg = GetForegroundWindow();

        if (targetHwnd != IntPtr.Zero && currentFg != targetHwnd)
        {
            AppLogger.Debug("Automation", $"Target 0x{targetHwnd.ToInt64():X} not foreground (current=0x{currentFg.ToInt64():X}). Activating...");
            ActivateWindow(targetHwnd);
            Thread.Sleep(25);
        }

        var mods = ReleaseModifiers();

        AppLogger.Info("Automation", $"ReplaceText START: Backspaces={backspaceCount}, Replacement='{replacement}', Trailing='{(trailingChar == '\0' ? "none" : trailingChar.ToString())}', Target=0x{targetHwnd.ToInt64():X}");

        // 1. Delete typo via backspaces with hardware scanCode 0x0E
        // Sending each backspace with a 2ms interval guarantees that Chromium / Electron / Opera
        // processes EVERY backspace sequentially and never coalesces or drops repeat events.
        if (backspaceCount > 0)
        {
            for (int i = 0; i < backspaceCount; i++)
            {
                var bs = new INPUT[]
                {
                    new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = 0 } },
                    new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = KEYEVENTF_KEYUP } }
                };
                SendInput((uint)bs.Length, bs, Marshal.SizeOf(typeof(INPUT)));
                if (backspaceCount > 1) Thread.Sleep(2);
            }

            // Short cadence allowing DOM / document model to register deletion before typing
            Thread.Sleep(8);
        }

        // 2. Type replacement characters + trailing delimiter via Unicode SendInput
        SendUnicodeText(replacement, trailingChar);

        RestoreModifiers(mods);
        sw.Stop();
        AppLogger.Info("Automation", $"ReplaceText COMPLETE in {sw.ElapsedMilliseconds}ms");
    }

    public static bool AppendSuffix(string suffix, char trailingChar = ' ', IntPtr targetHwnd = default)
    {
        if (string.IsNullOrEmpty(suffix) && trailingChar == '\0') return true;

        var mods = ReleaseModifiers();
        bool ok = SendUnicodeText(suffix, trailingChar);
        RestoreModifiers(mods);
        return ok;
    }

    /// <summary>Types text and an optional trailing delimiter as UTF-16 code units. Returns false if both paths failed.</summary>
    private static bool SendUnicodeText(string text, char trailingChar)
    {
        var inputs = new List<INPUT>();

        foreach (char c in text)
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

        if (inputs.Count == 0) return true;

        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        if (sent == 0)
        {
            AppLogger.Warn("Automation", "SendInput returned 0, falling back to keybd_event");
            return TypeViaKeybdEvent(text, trailingChar);
        }

        return true;
    }

    private static bool TypeViaKeybdEvent(string text, char trailingChar)
    {
        string full = text + (trailingChar != '\0' ? trailingChar.ToString() : "");
        bool sentAny = full.Length == 0;
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
                    sentAny = true;
                }
            }
            Thread.Sleep(3);
        }
        return sentAny;
    }

    private static void RunInSta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try { action(); } catch { }
            return;
        }

        var thread = new Thread(() =>
        {
            try { action(); } catch { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Clipboard COM calls are apartment-affine: wait for completion instead of
        // returning while the worker may still overwrite the user's clipboard.
        thread.Join();
    }

    /// <summary>Best-effort snapshot of the current clipboard so it can be restored after a Ctrl+C probe.</summary>
    private static object? TrySnapshotClipboard()
    {
        object? result = null;
        RunInSta(() =>
        {
            try
            {
                var source = System.Windows.Clipboard.GetDataObject();
                if (source == null) return;

                var copy = new System.Windows.DataObject();
                foreach (var format in source.GetFormats())
                {
                    try
                    {
                        if (format == System.Windows.DataFormats.FileDrop)
                        {
                            if (source.GetData(format) is string[] files && files.Length > 0)
                            {
                                var list = new System.Collections.Specialized.StringCollection();
                                list.AddRange(files);
                                copy.SetFileDropList(list);
                            }
                        }
                        else if (format == System.Windows.DataFormats.Bitmap)
                        {
                            if (source.GetData(format) is System.Windows.Media.Imaging.BitmapSource bmp)
                            {
                                copy.SetImage(bmp);
                            }
                        }
                        else
                        {
                            var data = source.GetData(format);
                            if (data != null) copy.SetData(format, data);
                        }
                    }
                    catch { }
                }

                if (copy.GetFormats().Length > 0) result = copy;
            }
            catch { }
        });
        return result;
    }

    private static void RestoreClipboard(object? snapshot)
    {
        if (snapshot == null) return;
        RunInSta(() =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetDataObject(snapshot, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(30);
                }
            }
        });
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageReplace(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    public static string GetSelectedTextFromActiveApp(IntPtr targetHwnd = default)
    {
        // 1. Try UI Automation and Win32 Edit selection first (zero clipboard noise, zero physical key conflicts)
        try
        {
            string uiaSel = ActiveDocumentService.GetSelectedText(targetHwnd);
            if (!string.IsNullOrWhiteSpace(uiaSel))
            {
                AppLogger.Info("SelectedText", $"Retrieved {uiaSel.Length} chars via UIA/Win32");
                return uiaSel;
            }
        }
        catch { }

        // 2. Fall back to focused Ctrl+C copy with active window activation and modifier key release
        if (targetHwnd != IntPtr.Zero)
        {
            ActivateWindow(targetHwnd);
            Thread.Sleep(30);
        }

        object? snapshot = TrySnapshotClipboard();
        var (c, s, a) = ReleaseModifiers();

        try
        {
            ClearClipboardSafe();

            // Send clean Ctrl+C
            var inputs = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_C, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_C, dwFlags = KEYEVENTF_KEYUP } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));

            // Poll for copied text (up to 200ms)
            string selected = string.Empty;
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(25);
                if (TryGetClipboardTextSafe(out string text) && !string.IsNullOrWhiteSpace(text))
                {
                    selected = text;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(selected))
            {
                AppLogger.Info("SelectedText", $"Retrieved {selected.Length} chars via Ctrl+C from 0x{targetHwnd.ToInt64():X}");
                return selected;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SelectedText", $"GetSelectedTextFromActiveApp exception: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            // Restore original clipboard if nothing was retrieved
            RestoreModifiers((c, s, a));
            RestoreClipboard(snapshot);
        }
    }

    private static void ClearClipboardSafe()
    {
        RunInSta(() =>
        {
            try { System.Windows.Clipboard.Clear(); } catch { }
        });
    }

    private static bool TryGetClipboardTextSafe(out string text)
    {
        string res = string.Empty;
        RunInSta(() =>
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    res = System.Windows.Clipboard.GetText();
                }
            }
            catch { }
        });
        text = res;
        return !string.IsNullOrEmpty(text);
    }

    private static void TrySetClipboardText(string text)
    {
        RunInSta(() =>
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    return;
                }
                catch
                {
                    Thread.Sleep(30);
                }
            }
        });
    }

    public static void ReplaceSelectedTextInActiveApp(string newText)
    {
        ReplaceSelectedTextInActiveApp(IntPtr.Zero, newText);
    }

    public static void ReplaceSelectedTextInActiveApp(IntPtr targetHwnd, string newText)
    {
        if (string.IsNullOrEmpty(newText)) return;

        object? clipboardSnapshot = TrySnapshotClipboard();
        bool usedClipboard = false;
        try
        {
            if (targetHwnd != IntPtr.Zero)
            {
                ActivateWindow(targetHwnd);
                Thread.Sleep(35);
            }

            // 1. Try Win32 EM_REPLACESEL if focused window is a Win32 Edit
            if (targetHwnd != IntPtr.Zero)
            {
                try
                {
                    var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                    if (focused != null && focused.Current.NativeWindowHandle != 0)
                    {
                        IntPtr editHwnd = (IntPtr)focused.Current.NativeWindowHandle;
                        SendMessageReplace(editHwnd, 0x00C2 /* EM_REPLACESEL */, (IntPtr)1, newText);
                        AppLogger.Info("SelectedText", $"Replaced selected text via EM_REPLACESEL in 0x{editHwnd.ToInt64():X}");
                        return;
                    }
                }
                catch { }
            }

            // 2. Universal Paste via Clipboard
            usedClipboard = true;
            TrySetClipboardText(newText);
            Thread.Sleep(35);
            ReleaseModifiers();

            // Send Ctrl+V
            var inputs = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = 0 } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } },
                new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } },
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            AppLogger.Info("SelectedText", $"Replaced selected text via Ctrl+V in 0x{targetHwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SelectedText", $"ReplaceSelectedText error: {ex.Message}");
        }
        finally
        {
            // Let the target app finish reading the clipboard before putting the user's data back.
            if (usedClipboard) Thread.Sleep(120);
            RestoreClipboard(clipboardSnapshot);
        }
    }
}
