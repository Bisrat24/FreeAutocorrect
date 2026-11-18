using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const uint LLKHF_INJECTED = 0x00000001;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private readonly LowLevelKeyboardProc _proc;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _hookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private readonly StringBuilder _currentWord = new();
    private readonly StringBuilder _rollingBuffer = new();
    private readonly object _bufferLock = new();
    // Guards word/caret state shared between the hook callback and the async document-scan timer.
    private readonly object _stateLock = new();
    private readonly AutoCorrectDictionary _dictionary;
    private readonly AppSettings _settings;
    private System.Threading.Timer? _batchDebounceTimer;
    private System.Threading.Timer? _docScanTimer;
    private System.Threading.Timer? _windowPollTimer;

    private class WindowState
    {
        public string RollingBuffer { get; set; } = string.Empty;
        public string CurrentWord { get; set; } = string.Empty;
        public string PreviousWord { get; set; } = string.Empty;
        public long LastUsed { get; set; } = Environment.TickCount64;
    }
    private readonly Dictionary<IntPtr, WindowState> _windowStates = new();

    private IntPtr _lastForegroundWindow = IntPtr.Zero;
    public IntPtr LastActiveWindow => _lastForegroundWindow;
    private long _lastKeystrokeTime = 0;
    private long _tabPressedTime = 0;
    private long _altPressedTime = 0;
    private string _previousWord = string.Empty;
    private string _pendingTabCompletion = string.Empty;

    // Password/secure-field context detected by the async document scan (UIA) and Win32 focus styles.
    private IntPtr _passwordHwnd = IntPtr.Zero;
    private bool _passwordFieldActive = false;

    public event Action<string, string>? OnWordCorrected;
    public event Action<string, string>? OnSuggestionFound;
    public event Action? OnSuggestionDismissed;
    public event Action<string, string, int, int>? OnSuggestionDetected;
    public event Action<string, IntPtr>? OnTextBatchUpdated;
    public event Action<string, IntPtr>? OnSelectedTextDetected;
    public event Action<string, string>? OnTabCompletionAvailable;
    public event Action? OnTabCompletionDismissed;
    public event Action<string, List<string>>? OnHardWordFound;

    public LowLevelKeyboardHook(AutoCorrectDictionary dictionary, AppSettings settings)
    {
        _dictionary = dictionary;
        _settings = settings;
        _proc = HookCallback;
        _mouseProc = MouseHookCallback;
    }

    public void Start()
    {
        if (_hookId == IntPtr.Zero)
        {
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }
        if (_mouseHookId == IntPtr.Zero)
        {
            try
            {
                _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
            }
            catch { }
        }

        // Periodic foreground window checker (runs every 600ms) to detect window switches (e.g. Alt-Tab or clicking another window)
        _windowPollTimer?.Dispose();
        _windowPollTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != _lastForegroundWindow)
                {
                    GetWindowThreadProcessId(fg, out uint pid);
                    if (pid != (uint)Environment.ProcessId)
                    {
                        _lastForegroundWindow = fg;
                        ScheduleActiveDocumentScan(fg);
                    }
                }
            }
            catch { }
        }, null, 1000, 600);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        if (_mouseHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
        _batchDebounceTimer?.Dispose();
        _docScanTimer?.Dispose();
        _windowPollTimer?.Dispose();
    }

    public string GetRollingText()
    {
        lock (_bufferLock)
        {
            return _rollingBuffer.ToString();
        }
    }

    public int GetDistanceFromCaret(int charEnd)
    {
        lock (_bufferLock)
        {
            int dist = _rollingBuffer.Length - charEnd;
            return dist >= 0 ? dist : 0;
        }
    }

    public void UpdateRollingBuffer(string newText)
    {
        lock (_bufferLock)
        {
            _rollingBuffer.Clear();
            _rollingBuffer.Append(newText);
        }
    }

    private void ScheduleBatchUpdate(int delayMs = 50)
    {
        _batchDebounceTimer?.Dispose();
        _batchDebounceTimer = new System.Threading.Timer(_ =>
        {
            string text;
            IntPtr hwnd = _lastForegroundWindow;
            lock (_bufferLock)
            {
                text = _rollingBuffer.ToString();
            }
            OnTextBatchUpdated?.Invoke(text, hwnd);
        }, null, delayMs, System.Threading.Timeout.Infinite);
    }

    public void ScheduleActiveDocumentScan(IntPtr hwnd, int delayMs = 60)
    {
        if (hwnd == IntPtr.Zero || !IsWindowAppEnabled(hwnd)) return;

        _docScanTimer?.Dispose();
        _docScanTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                // Never lint or rewrite password/secure fields. UIA check runs here (not in the hook).
                bool isPassword = ActiveDocumentService.IsPasswordEditFocused() ||
                                  ActiveDocumentService.IsFocusedElementPassword();

                lock (_stateLock)
                {
                    // Normalize to the top-level window: mouse clicks pass a child handle,
                    // while the keyboard hook compares against the foreground window.
                    IntPtr root = GetAncestor(hwnd, 2 /* GA_ROOT */);
                    _passwordHwnd = root != IntPtr.Zero ? root : hwnd;
                    _passwordFieldActive = isPassword;
                    if (isPassword)
                    {
                        _currentWord.Clear();
                        _previousWord = string.Empty;
                        _pendingTabCompletion = string.Empty;
                    }
                }

                if (isPassword)
                {
                    OnSuggestionDismissed?.Invoke();
                    OnTabCompletionDismissed?.Invoke();
                    return;
                }

                var doc = ActiveDocumentService.GetDocumentContext(hwnd);
                if (!doc.IsSupported) return;

                if (!string.IsNullOrWhiteSpace(doc.SelectedText))
                {
                    AppLogger.Info("ActiveDoc", $"ScanActiveDocument: Selected text detected ({doc.SelectedText.Length} chars) in 0x{hwnd.ToInt64():X}");
                    OnSelectedTextDetected?.Invoke(doc.SelectedText, hwnd);
                }
                else if (!string.IsNullOrWhiteSpace(doc.FullText))
                {
                    AppLogger.Info("ActiveDoc", _settings.VerboseLogging
                        ? $"ScanActiveDocument: Found {doc.FullText.Length} chars in 0x{hwnd.ToInt64():X}. WordAtCaret='{doc.WordAtCaret}'"
                        : $"ScanActiveDocument: Found {doc.FullText.Length} chars in 0x{hwnd.ToInt64():X}. WordAtCaret length={doc.WordAtCaret.Length}");
                    OnTextBatchUpdated?.Invoke(doc.FullText, hwnd);
                }

                lock (_bufferLock)
                {
                    if (!string.IsNullOrEmpty(doc.TextBeforeCaret))
                    {
                        _rollingBuffer.Clear();
                        _rollingBuffer.Append(doc.TextBeforeCaret);
                    }
                }

                string? completionPrefix = null;
                string? completionWord = null;
                lock (_stateLock)
                {
                    if (!string.IsNullOrEmpty(doc.WordAtCaret))
                    {
                        _currentWord.Clear();
                        _currentWord.Append(doc.WordAtCaret);

                        if (_settings.TabCompletionEnabled && _currentWord.Length >= 3)
                        {
                            string cur = _currentWord.ToString();
                            if (_dictionary.GetPrefixCompletion(cur, out var comp) && comp.Length > cur.Length)
                            {
                                _pendingTabCompletion = comp;
                                completionPrefix = cur;
                                completionWord = comp;
                            }
                        }
                    }
                }

                if (completionPrefix != null && completionWord != null)
                {
                    OnTabCompletionAvailable?.Invoke(completionPrefix, completionWord);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ActiveDoc", $"ScheduleActiveDocumentScan error: {ex.Message}");
            }
        }, null, delayMs, System.Threading.Timeout.Infinite);
    }

    private string ExtractTailWordFromRollingBuffer(int offsetFromEnd = 0)
    {
        lock (_bufferLock)
        {
            int end = _rollingBuffer.Length - 1 - offsetFromEnd;
            if (end >= 0 && IsWordChar(_rollingBuffer[end]))
            {
                int start = end;
                while (start >= 0 && IsWordChar(_rollingBuffer[start]))
                {
                    start--;
                }
                start++;
                return _rollingBuffer.ToString(start, end - start + 1).Trim();
            }
        }
        return string.Empty;
    }

    private readonly ConcurrentDictionary<uint, (string Name, long Timestamp)> _processNameCache = new();

    private string GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return string.Empty;

            long now = Environment.TickCount64;
            if (_processNameCache.TryGetValue(pid, out var cached) && (now - cached.Timestamp) < 3000)
            {
                return cached.Name;
            }

            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            _processNameCache[pid] = (name, now);

            if (_processNameCache.Count > 256)
            {
                foreach (var kvp in _processNameCache)
                {
                    if (now - kvp.Value.Timestamp > 60_000)
                    {
                        _processNameCache.TryRemove(kvp.Key, out _);
                    }
                }
            }

            return name;
        }
        catch
        {
            return string.Empty;
        }
    }

    public bool IsWindowAppEnabled(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false; // FreeAutocorrect UI
            if (pid == 0) return false;

            return _settings.IsAppEnabled(GetProcessName(hwnd));
        }
        catch
        {
            // Fail closed: never intercept text from a window we cannot identify.
            return false;
        }
    }

    private bool IsPasswordContext(IntPtr hwnd)
    {
        // Cheap Win32 check for classic ES_PASSWORD edits.
        if (ActiveDocumentService.IsPasswordEditFocused()) return true;

        lock (_stateLock)
        {
            return hwnd != IntPtr.Zero &&
                   hwnd == _passwordHwnd &&
                   _passwordFieldActive;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Skip keystrokes simulated by our own SendInput
            if ((hookStruct.flags & LLKHF_INJECTED) != 0)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            IntPtr fg = GetForegroundWindow();
            if (!IsWindowAppEnabled(fg))
            {
                // App is disabled (e.g. explorer.exe unless enabled) -> pass through with zero interference
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (IsPasswordContext(fg))
            {
                // Password fields are never tracked, suggested on, or rewritten.
                lock (_stateLock)
                {
                    _currentWord.Clear();
                    _previousWord = string.Empty;
                    _pendingTabCompletion = string.Empty;
                }
                lock (_bufferLock) { _rollingBuffer.Clear(); }
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            bool suppress;
            lock (_stateLock)
            {
                suppress = ProcessKeystroke((int)hookStruct.vkCode, (int)hookStruct.scanCode);
            }

            if (suppress)
            {
                // Delimiter was suppressed because autocorrect is rewriting the word cleanly
                return (IntPtr)1;
            }
        }
        }
        catch (Exception ex)
        {
            // A managed exception must never cross the native WH_KEYBOARD_LL boundary.
            AppLogger.Debug("Hook", $"HookCallback error: {ex.Message}");
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN || 
                           wParam == (IntPtr)WM_MBUTTONDOWN || wParam == (IntPtr)WM_NCLBUTTONDOWN))
        {
            try
            {
                var mouseStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr hwnd = WindowFromPoint(mouseStruct.pt);
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != (uint)Environment.ProcessId)
                    {
                        if (!IsWindowAppEnabled(hwnd))
                        {
                            OnSuggestionDismissed?.Invoke();
                            OnTabCompletionDismissed?.Invoke();
                            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
                        }

                        // User clicked in document, another app, or desktop -> reset caret word & dismiss banners
                        _lastForegroundWindow = hwnd;
                        lock (_stateLock)
                        {
                            _currentWord.Clear();
                            _previousWord = string.Empty;
                            _pendingTabCompletion = string.Empty;
                        }
                        lock (_bufferLock)
                        {
                            _rollingBuffer.Clear();
                        }
                        // A click may have moved focus to a different control (e.g. password field).
                        lock (_stateLock)
                        {
                            _passwordFieldActive = false;
                        }
                        OnSuggestionDismissed?.Invoke();
                        OnTabCompletionDismissed?.Invoke();
                        ScheduleActiveDocumentScan(hwnd, 50);
                    }
                }
            }
            catch { }
        }
        else if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONUP)
        {
            try
            {
                var mouseStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr hwnd = WindowFromPoint(mouseStruct.pt);
                if (hwnd != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid != (uint)Environment.ProcessId && IsWindowAppEnabled(hwnd))
                    {
                        ScheduleActiveDocumentScan(hwnd, 70);
                    }
                }
            }
            catch { }
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private bool ProcessKeystroke(int vkCode, int scanCode)
    {
        const int VK_BACK = 0x08;
        const int VK_TAB = 0x09;
        const int VK_RETURN = 0x0D;
        const int VK_ESCAPE = 0x1B;
        const int VK_SPACE = 0x20;
        const int VK_LEFT = 0x25;
        const int VK_RIGHT = 0x27;
        const int VK_UP = 0x26;
        const int VK_DOWN = 0x28;
        const int VK_DELETE = 0x2E;

        // Reset or restore buffer if user switched to another window (ignoring FreeAutocorrect's own UI)
        IntPtr currentHwnd = GetForegroundWindow();
        if (currentHwnd != _lastForegroundWindow && currentHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(currentHwnd, out uint pid);
            if (pid != (uint)Environment.ProcessId)
            {
                // Save state of previous window so returning to it retains typed letters
                if (_lastForegroundWindow != IntPtr.Zero)
                {
                    lock (_bufferLock)
                    {
                        _windowStates[_lastForegroundWindow] = new WindowState
                        {
                            RollingBuffer = _rollingBuffer.ToString(),
                            CurrentWord = _currentWord.ToString(),
                            PreviousWord = _previousWord,
                            LastUsed = Environment.TickCount64
                        };
                    }

                    if (_windowStates.Count > 32)
                    {
                        long cutoff = Environment.TickCount64 - 300_000;
                        foreach (var stale in _windowStates.Where(kv => kv.Value.LastUsed < cutoff).Select(kv => kv.Key).ToList())
                        {
                            _windowStates.Remove(stale);
                        }
                    }
                }

                _lastForegroundWindow = currentHwnd;
                AppLogger.Info("Window", $"Active window changed to: 0x{currentHwnd.ToInt64():X} (PID: {pid})");

                if (_windowStates.TryGetValue(currentHwnd, out var saved))
                {
                    lock (_bufferLock)
                    {
                        _rollingBuffer.Clear();
                        _rollingBuffer.Append(saved.RollingBuffer);
                    }
                    _currentWord.Clear();
                    _currentWord.Append(saved.CurrentWord);
                    _previousWord = saved.PreviousWord;
                }
                else
                {
                    _currentWord.Clear();
                    _previousWord = string.Empty;
                    lock (_bufferLock) { _rollingBuffer.Clear(); }
                }

                _pendingTabCompletion = string.Empty;
                _tabPressedTime = 0;
                _altPressedTime = 0;
                _passwordFieldActive = false;
                OnTabCompletionDismissed?.Invoke();
                ScheduleActiveDocumentScan(currentHwnd, 60);
            }
        }

        // Never wipe _currentWord simply because user paused typing to think or check something!
        long now = Environment.TickCount64;
        _lastKeystrokeTime = now;

        // 1. Configurable Auto-Finish Trigger (e.g. Tab+Right, Tab, Ctrl+Tab, Right, Shift+Tab, Alt+Right)
        if (_settings.TabCompletionEnabled && !string.IsNullOrEmpty(_pendingTabCompletion) && _currentWord.Length > 0)
        {
            if (CheckIsAutoFinishTrigger(vkCode))
            {
                string completedWord = _pendingTabCompletion;
                string typedStr = _currentWord.ToString();
                int typedLen = typedStr.Length;
                IntPtr target = _lastForegroundWindow;
                _currentWord.Clear();
                _pendingTabCompletion = string.Empty;
                _tabPressedTime = 0;
                _altPressedTime = 0;
                OnTabCompletionDismissed?.Invoke();

                Task.Run(async () =>
                {
                    await Task.Delay(20);
                    if (completedWord.StartsWith(typedStr, StringComparison.OrdinalIgnoreCase) && completedWord.Length > typedLen)
                    {
                        string suffix = completedWord.Substring(typedLen);
                        TextAutomationService.AppendSuffix(suffix, ' ', target);
                    }
                    else
                    {
                        TextAutomationService.ReplaceText(typedLen, completedWord, ' ', target);
                    }
                });

                _previousWord = completedWord;
                _settings.TotalWordsCompleted++;
                _settings.SaveDebounced();
                return true; // Suppress trigger key from reaching active window
            }
            else if ((_settings.AutoFinishHotkey == "Tab+Right" || string.IsNullOrEmpty(_settings.AutoFinishHotkey)) && vkCode == VK_TAB)
            {
                _tabPressedTime = Environment.TickCount64;
                return true; // Suppress Tab key in Tab+Right combination so indent isn't inserted
            }
            else if ((_settings.AutoFinishHotkey == "Alt+Right") && (vkCode == 0x12 || vkCode == 0xA4 || vkCode == 0xA5))
            {
                _altPressedTime = Environment.TickCount64;
                return true; // Suppress Alt key in Alt+Right combination so menu bar isn't triggered
            }
        }

        const int VK_PRIOR = 0x21;
        const int VK_NEXT = 0x22;
        const int VK_END = 0x23;
        const int VK_HOME = 0x24;

        // Reset if user moved caret in document (arrow keys, home, end, pgup, pgdn, escape)
        if (vkCode == VK_ESCAPE || vkCode == VK_LEFT || vkCode == VK_RIGHT || 
            vkCode == VK_UP || vkCode == VK_DOWN || vkCode == VK_HOME ||
            vkCode == VK_END || vkCode == VK_PRIOR || vkCode == VK_NEXT)
        {
            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            _tabPressedTime = 0;
            _altPressedTime = 0;
            lock (_bufferLock) { _rollingBuffer.Clear(); }
            OnSuggestionDismissed?.Invoke();
            OnTabCompletionDismissed?.Invoke();
            ScheduleActiveDocumentScan(_lastForegroundWindow != IntPtr.Zero ? _lastForegroundWindow : currentHwnd);
            return false;
        }

        // Forward deletion
        if (vkCode == VK_DELETE)
        {
            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            _tabPressedTime = 0;
            _altPressedTime = 0;
            lock (_bufferLock) { _rollingBuffer.Clear(); }
            OnSuggestionDismissed?.Invoke();
            OnTabCompletionDismissed?.Invoke();
            ScheduleActiveDocumentScan(_lastForegroundWindow != IntPtr.Zero ? _lastForegroundWindow : currentHwnd);
            return false;
        }

        bool isCtrlDown = (GetAsyncKeyState(0x11) & 0x8000) != 0;

        if (vkCode == VK_BACK)
        {
            if (isCtrlDown)
            {
                // Ctrl+Backspace deletes entire word backward
                _currentWord.Clear();
                _previousWord = string.Empty;
                _pendingTabCompletion = string.Empty;
                lock (_bufferLock)
                {
                    int idx = _rollingBuffer.Length - 1;
                    while (idx >= 0 && char.IsWhiteSpace(_rollingBuffer[idx])) idx--;
                    while (idx >= 0 && !char.IsWhiteSpace(_rollingBuffer[idx])) idx--;
                    if (idx >= 0) _rollingBuffer.Length = idx + 1;
                    else _rollingBuffer.Clear();
                }
                OnSuggestionDismissed?.Invoke();
                OnTabCompletionDismissed?.Invoke();
                ScheduleBatchUpdate();
                return false;
            }

            lock (_bufferLock)
            {
                if (_rollingBuffer.Length > 0)
                {
                    _rollingBuffer.Length--;
                }
            }
            ScheduleBatchUpdate(20);

            // Always dismiss suggestion when characters are removed/modified!
            OnSuggestionDismissed?.Invoke();

            if (_currentWord.Length > 0)
            {
                _currentWord.Length--;
                if (_currentWord.Length == 0)
                {
                    _pendingTabCompletion = string.Empty;
                    OnTabCompletionDismissed?.Invoke();
                }
                else if (_settings.TabCompletionEnabled && _currentWord.Length >= 3)
                {
                    string cur = _currentWord.ToString();
                    if (_dictionary.GetPrefixCompletion(cur, out var comp) && comp.Length > cur.Length)
                    {
                        _pendingTabCompletion = comp;
                        OnTabCompletionAvailable?.Invoke(cur, comp);
                    }
                    else
                    {
                        _pendingTabCompletion = string.Empty;
                        OnTabCompletionDismissed?.Invoke();
                    }
                }
                else
                {
                    _pendingTabCompletion = string.Empty;
                    OnTabCompletionDismissed?.Invoke();
                }
            }
            else if (!string.IsNullOrEmpty(_previousWord))
            {
                // User backspaced over space back into previous word!
                _currentWord.Append(_previousWord);
                _previousWord = string.Empty;
            }
            else
            {
                // User backspaced over multiple words or into an existing word in the buffer!
                // Reconstruct _currentWord from contiguous word characters at the tail of _rollingBuffer
                lock (_bufferLock)
                {
                    int idx = _rollingBuffer.Length - 1;
                    if (idx >= 0 && IsWordChar(_rollingBuffer[idx]))
                    {
                        int wordEnd = idx;
                        while (idx >= 0 && IsWordChar(_rollingBuffer[idx]))
                        {
                            idx--;
                        }
                        int wordStart = idx + 1;
                        string extracted = _rollingBuffer.ToString(wordStart, wordEnd - wordStart + 1);
                        _currentWord.Clear();
                        _currentWord.Append(extracted);
                    }
                }
            }
            return false;
        }

        // Modifier shortcuts are never text input: Ctrl+S, Ctrl+V, Alt+Tab, Win+R, etc.
        // Without this gate those letters were appended to the word buffer and Alt+Tab could
        // trigger a correction on the word being left behind.
        bool isAltDown = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool isWinDown = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        if (isCtrlDown || isAltDown || isWinDown)
        {
            // Editing shortcuts (undo/cut/select-all) invalidate the buffered text; plain shortcuts do not.
            if (isCtrlDown && (vkCode == 'A' || vkCode == 'Z' || vkCode == 'Y' || vkCode == 'X'))
            {
                _currentWord.Clear();
                _previousWord = string.Empty;
                _pendingTabCompletion = string.Empty;
                lock (_bufferLock) { _rollingBuffer.Clear(); }
                OnSuggestionDismissed?.Invoke();
                OnTabCompletionDismissed?.Invoke();
                ScheduleBatchUpdate();
            }
            return false;
        }

        // Convert virtual key to character reliably
        char typedChar = GetCharFromKey((uint)vkCode, (uint)scanCode);

        // Word delimiters: Space, Return, Tab, Punctuation (. , ! ? ; :)
        bool isDelimiter = (vkCode == VK_SPACE || vkCode == VK_RETURN || vkCode == VK_TAB ||
                            typedChar == '.' || typedChar == ',' || typedChar == '!' || 
                            typedChar == '?' || typedChar == ';' || typedChar == ':');

        if (isDelimiter)
        {
            _pendingTabCompletion = string.Empty;
            OnTabCompletionDismissed?.Invoke();

            char dChar = (vkCode == VK_RETURN) ? '\n' : (vkCode == VK_TAB ? '\t' : (typedChar != '\0' ? typedChar : ' '));
            lock (_bufferLock)
            {
                if (_rollingBuffer.Length > 1500)
                {
                    _rollingBuffer.Remove(0, 300);
                }
                _rollingBuffer.Append(dChar);
            }
            ScheduleBatchUpdate(0);

            string rawWord = _currentWord.ToString().Trim();
            _currentWord.Clear();

            // Extract contiguous word token immediately before the delimiter from _rollingBuffer
            string bufferWord = ExtractTailWordFromRollingBuffer(1);

            // When rewriting or adding letters to an existing word, prioritize the full contiguous token on screen.
            // (No UIA call here: cross-process queries belong on the scan timer, never in the keyboard hook.)
            if (!string.IsNullOrEmpty(bufferWord))
            {
                if (string.IsNullOrEmpty(rawWord))
                {
                    rawWord = bufferWord;
                }
                else if (bufferWord.Length > rawWord.Length && bufferWord.EndsWith(rawWord, StringComparison.OrdinalIgnoreCase))
                {
                    rawWord = bufferWord;
                }
            }

            if (!string.IsNullOrEmpty(rawWord))
            {
                // Never autocorrect, capitalize, or show suggestions on links, URLs, directory paths, file names, or file types!
                string bufferSnapshot;
                lock (_bufferLock) { bufferSnapshot = _rollingBuffer.ToString(); }
                if (PathAndFileFilter.IsPathOrFileOrLinkContext(bufferSnapshot, rawWord.Length, typedChar) ||
                    PathAndFileFilter.ShouldIgnoreToken(rawWord))
                {
                    OnSuggestionDismissed?.Invoke();
                    OnTabCompletionDismissed?.Invoke();
                    _previousWord = rawWord;
                    return false;
                }

                // Editors/terminals get no prose rules: no auto-capitalization, snippets, rewrites or banners.
                bool codeMode = _settings.IsCodeApp(GetProcessName(_lastForegroundWindow));

                // 1. Check Double Capitals (e.g. THis -> This, HEllo -> Hello)
                if (!codeMode && _settings.FixDoubleCapitals && rawWord.Length >= 3 &&
                    char.IsUpper(rawWord[0]) && char.IsUpper(rawWord[1]) && char.IsLower(rawWord[2]))
                {
                    bool allUpper = rawWord.All(c => char.IsUpper(c) || !char.IsLetter(c));
                    if (!allUpper)
                    {
                        string doubleCapsFixed = char.ToUpper(rawWord[0]) + rawWord.Substring(1).ToLowerInvariant();
                        int dcEnd = 0;
                        int dcStart = 0;
                        lock (_bufferLock)
                        {
                            dcEnd = _rollingBuffer.Length;
                            dcStart = Math.Max(0, dcEnd - rawWord.Length - 1);
                        }
                        OnSuggestionDetected?.Invoke(rawWord, doubleCapsFixed, dcStart, dcEnd);
                        OnSuggestionFound?.Invoke(rawWord, doubleCapsFixed);
                        if (_settings.LiveAutocorrectEnabled)
                        {
                            PerformAutocorrect(rawWord, doubleCapsFixed, typedChar);
                            _previousWord = doubleCapsFixed;
                            return true; // Suppress delimiter
                        }
                    }
                }

                // 2. Check Sentence Auto-Capitalization (e.g. "hello" -> "Hello" at start of sentence/line)
                bool atSentenceStart = !codeMode && _settings.AutoCapitalizeSentence && IsAtSentenceStart(rawWord.Length);

                // A. Check multi-word phrase correction if previous word exists
                if (!codeMode && !string.IsNullOrEmpty(_previousWord))
                {
                    string phrase = $"{_previousWord} {rawWord}";
                    if (_dictionary.TryGetPhraseCorrection(phrase, out var phraseFix) &&
                        !string.Equals(phrase, phraseFix, StringComparison.OrdinalIgnoreCase))
                    {
                        int pEnd = 0;
                        int pStart = 0;
                        lock (_bufferLock)
                        {
                            pEnd = _rollingBuffer.Length;
                            pStart = Math.Max(0, pEnd - phrase.Length - 1);
                        }
                        OnSuggestionDetected?.Invoke(phrase, phraseFix, pStart, pEnd);
                        OnSuggestionFound?.Invoke(phrase, phraseFix);
                        if (_settings.LiveAutocorrectEnabled)
                        {
                            PerformPhraseAutocorrect(phrase, phraseFix, typedChar);
                            _previousWord = phraseFix;
                            return true; // Suppress delimiter
                        }
                    }
                }

                // B. Check custom snippets
                if (!codeMode && _settings.CustomSnippets != null && _settings.CustomSnippets.TryGetValue(rawWord, out var snippet))
                {
                    int sEnd = 0;
                    int sStart = 0;
                    lock (_bufferLock)
                    {
                        sEnd = _rollingBuffer.Length;
                        sStart = Math.Max(0, sEnd - rawWord.Length - 1);
                    }
                    OnSuggestionDetected?.Invoke(rawWord, snippet, sStart, sEnd);
                    OnSuggestionFound?.Invoke(rawWord, snippet);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(rawWord, snippet, typedChar);
                        _previousWord = snippet;
                        return true; // Suppress delimiter
                    }
                    _previousWord = rawWord;
                    return false;
                }

                // C. Check autocorrect dictionary.
                // Conservative mode (default) only auto-applies high-confidence typos; edit-distance
                // guesses become suggestions so names, brands and jargon are never rewritten.
                string corrected = string.Empty;
                bool hasAutoCorrection = false;
                if (!codeMode)
                {
                    hasAutoCorrection = _settings.ConservativeAutocorrect
                        ? _dictionary.TryGetAutoCorrection(rawWord, out corrected)
                        : _dictionary.TryGetCorrection(rawWord, out corrected);
                    hasAutoCorrection = hasAutoCorrection &&
                                        !string.Equals(rawWord, corrected, StringComparison.Ordinal);
                }

                if (hasAutoCorrection)
                {
                    if (atSentenceStart && char.IsLower(corrected[0]))
                    {
                        corrected = char.ToUpper(corrected[0]) + corrected.Substring(1);
                    }

                    int wEnd = 0;
                    int wStart = 0;
                    lock (_bufferLock)
                    {
                        wEnd = _rollingBuffer.Length;
                        wStart = Math.Max(0, wEnd - rawWord.Length - 1);
                    }
                    OnSuggestionDetected?.Invoke(rawWord, corrected, wStart, wEnd);
                    OnSuggestionFound?.Invoke(rawWord, corrected);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(rawWord, corrected, typedChar);
                        _previousWord = corrected;
                        return true; // Suppress delimiter
                    }
                    _previousWord = rawWord;
                    return false;
                }
                else if (atSentenceStart && char.IsLower(rawWord[0]) && rawWord.Length >= 2)
                {
                    // Auto-capitalize first letter of sentence!
                    string capitalized = char.ToUpper(rawWord[0]) + rawWord.Substring(1);
                    int cStart = 0;
                    int cEnd = 0;
                    lock (_bufferLock)
                    {
                        cEnd = _rollingBuffer.Length;
                        cStart = Math.Max(0, cEnd - rawWord.Length - 1);
                    }
                    OnSuggestionDetected?.Invoke(rawWord, capitalized, cStart, cEnd);
                    OnSuggestionFound?.Invoke(rawWord, capitalized);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(rawWord, capitalized, typedChar);
                        _previousWord = capitalized;
                        return true; // Suppress delimiter
                    }
                    _previousWord = rawWord;
                    return false;
                }
                else if (!codeMode && _settings.ConservativeAutocorrect &&
                         _dictionary.TryGetCorrection(rawWord, out var suggestedFix) &&
                         !string.Equals(rawWord, suggestedFix, StringComparison.Ordinal))
                {
                    // Low-confidence match: show it, never rewrite it.
                    int gEnd;
                    int gStart;
                    lock (_bufferLock)
                    {
                        gEnd = _rollingBuffer.Length;
                        gStart = Math.Max(0, gEnd - rawWord.Length - 1);
                    }
                    OnSuggestionDetected?.Invoke(rawWord, suggestedFix, gStart, gEnd);
                    OnSuggestionFound?.Invoke(rawWord, suggestedFix);
                }
                else if (!codeMode && rawWord.Length >= 4 && !_dictionary.IsValidWord(rawWord))
                {
                    // Word is hard for auto-suggestion (unrecognized & ambiguous) -> underline & show candidates
                    var candidates = _dictionary.GetHardWordCandidates(rawWord);
                    OnHardWordFound?.Invoke(rawWord, candidates);
                }
                else
                {
                    // Valid word typed cleanly or corrected manually -> dismiss previous suggestion banner
                    OnSuggestionDismissed?.Invoke();
                }

                _previousWord = rawWord;
            }
        }
        else if (IsWordChar(typedChar))
        {
            if (_currentWord.Length == 0)
            {
                // Recover pre-existing prefix from _rollingBuffer (seeded by the async document scan
                // on focus/click). No UIA call happens on the hook thread.
                string prefixFromBuffer = ExtractTailWordFromRollingBuffer();
                if (!string.IsNullOrEmpty(prefixFromBuffer))
                {
                    _currentWord.Append(prefixFromBuffer);
                }
            }

            lock (_bufferLock)
            {
                if (_rollingBuffer.Length > 1500)
                {
                    _rollingBuffer.Remove(0, 300);
                }
                _rollingBuffer.Append(typedChar);
            }
            ScheduleBatchUpdate(50);

            if (_currentWord.Length < 60)
            {
                _currentWord.Append(typedChar);

                // Auto-suggest Tab completion in real time as user types (only for normal prose, never on links)
                string cur = _currentWord.ToString();
                string buf;
                lock (_bufferLock) { buf = _rollingBuffer.ToString(); }
                if (_settings.TabCompletionEnabled && _currentWord.Length >= 3 &&
                    !PathAndFileFilter.IsPathOrFileOrLinkContext(buf, cur.Length, '\0') &&
                    !PathAndFileFilter.ShouldIgnoreToken(cur))
                {
                    if (_dictionary.GetPrefixCompletion(cur, out var comp) && comp.Length > cur.Length)
                    {
                        _pendingTabCompletion = comp;
                        OnTabCompletionAvailable?.Invoke(cur, comp);
                    }
                    else
                    {
                        _pendingTabCompletion = string.Empty;
                        OnTabCompletionDismissed?.Invoke();
                    }
                }
                else
                {
                    _pendingTabCompletion = string.Empty;
                    OnTabCompletionDismissed?.Invoke();
                }
            }
        }
        else if (typedChar != '\0')
        {
            lock (_bufferLock)
            {
                if (_rollingBuffer.Length > 1500)
                {
                    _rollingBuffer.Remove(0, 300);
                }
                _rollingBuffer.Append(typedChar);
            }
            ScheduleBatchUpdate();

            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            OnTabCompletionDismissed?.Invoke();
        }

        return false;
    }

    internal static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '\'' || c == '-';
    }

    private void PerformPhraseAutocorrect(string typedPhrase, string replacement, char delimiter)
    {
        try
        {
            lock (_bufferLock)
            {
                string cur = _rollingBuffer.ToString();
                int idx = cur.LastIndexOf(typedPhrase, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx >= cur.Length - typedPhrase.Length - 3)
                {
                    _rollingBuffer.Remove(idx, typedPhrase.Length);
                    _rollingBuffer.Insert(idx, replacement);
                }
            }

            IntPtr target = _lastForegroundWindow;
            LogCorrection("Phrase triggered", typedPhrase, replacement, delimiter);
            Task.Run(() =>
            {
                bool ok = TextAutomationService.ReplaceWordBeforeCaret(2, replacement, delimiter, target);
                if (!ok && delimiter != '\0')
                {
                    // Never swallow the delimiter if the rewrite could not be injected.
                    TextAutomationService.AppendSuffix(string.Empty, delimiter, target);
                }
            });

            _settings.TotalWordsCorrected++;
            _settings.SaveDebounced();

            OnWordCorrected?.Invoke(typedPhrase, replacement);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Autocorrect", $"PerformPhraseAutocorrect error: {ex.Message}", ex);
        }
    }

    private void PerformAutocorrect(string typedWord, string replacement, char delimiter)
    {
        try
        {
            lock (_bufferLock)
            {
                string cur = _rollingBuffer.ToString();
                int idx = cur.LastIndexOf(typedWord, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && idx >= cur.Length - typedWord.Length - 3)
                {
                    _rollingBuffer.Remove(idx, typedWord.Length);
                    _rollingBuffer.Insert(idx, replacement);
                }
            }

            // The delimiter was suppressed: Ctrl+Backspace removes the whole visible word in one
            // undoable step, then the replacement + delimiter are typed.
            IntPtr target = _lastForegroundWindow;
            LogCorrection("Word triggered", typedWord, replacement, delimiter);
            Task.Run(() =>
            {
                bool ok = TextAutomationService.ReplaceWordBeforeCaret(1, replacement, delimiter, target);
                if (!ok && delimiter != '\0')
                {
                    // Never swallow the delimiter if the rewrite could not be injected.
                    TextAutomationService.AppendSuffix(string.Empty, delimiter, target);
                }
            });

            _settings.TotalWordsCorrected++;
            _settings.SaveDebounced();

            OnWordCorrected?.Invoke(typedWord, replacement);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Autocorrect", $"PerformAutocorrect error: {ex.Message}", ex);
        }
    }

    private void LogCorrection(string kind, string typed, string replacement, char delimiter)
    {
        string delim = delimiter == '\n' ? "\\n" : delimiter == '\t' ? "\\t" : delimiter.ToString();
        if (_settings.VerboseLogging)
        {
            AppLogger.Info("Autocorrect", $"{kind}: '{typed}' -> '{replacement}', Delimiter='{delim}'");
        }
        else
        {
            // Privacy: do not persist user content by default.
            AppLogger.Info("Autocorrect", $"{kind}: {typed.Length} chars -> {replacement.Length} chars, Delimiter='{delim}'");
        }
    }

    internal bool IsAtSentenceStart(int wordLength)
    {
        lock (_bufferLock)
        {
            // _rollingBuffer has: [...preceding text...][rawWord][dChar]
            int beforeWordIndex = _rollingBuffer.Length - wordLength - 1;
            if (beforeWordIndex <= 0)
            {
                // Not enough preceding buffer context to verify sentence boundary.
                // NEVER assume sentence start without positive evidence!
                return false;
            }

            // Scan backwards past spaces and tabs
            int i = beforeWordIndex - 1;
            while (i >= 0 && (_rollingBuffer[i] == ' ' || _rollingBuffer[i] == '\t'))
            {
                i--;
            }

            if (i < 0)
            {
                // Preceded only by whitespace at buffer head -> unconfirmed sentence start
                return false;
            }

            char c = _rollingBuffer[i];
            if (c == '\n' || c == '\r' || c == '.' || c == '!' || c == '?')
            {
                // In English sentences, a period is always separated from the next sentence by whitespace.
                // If the period immediately touches the word (e.g. "google.com", "site.org"), it is a link/domain!
                if (c == '.' && i == beforeWordIndex - 1)
                {
                    return false;
                }

                // Exclude URLs, web domains, directory paths, file names, file types, and links
                if (c == '.')
                {
                    string textBefore = _rollingBuffer.ToString(0, beforeWordIndex);
                    if (PathAndFileFilter.IsPathOrFileOrLinkContext(textBefore, 0, '.'))
                    {
                        return false;
                    }
                }

                // Exclude decimal numbers like 3.14
                if (c == '.' && i > 0 && char.IsDigit(_rollingBuffer[i - 1]))
                {
                    return false;
                }

                // Exclude ellipsis like "..."
                if (c == '.' && i > 0 && _rollingBuffer[i - 1] == '.')
                {
                    return false;
                }

                // Exclude common abbreviations and file types like "mr.", "dr.", "vs.", "etc.", "e.g.", "i.e.", "file.txt"
                if (c == '.' && i >= 1)
                {
                    int wordStart = i - 1;
                    while (wordStart >= 0 && char.IsLetter(_rollingBuffer[wordStart]))
                    {
                        wordStart--;
                    }
                    string abbrev = _rollingBuffer.ToString(wordStart + 1, (i - 1) - wordStart).ToLowerInvariant();
                    if (PathAndFileFilter.CommonAbbreviations.Contains(abbrev) || PathAndFileFilter.IsKnownFileType(abbrev))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }
    }

    internal bool TestSentenceStartCheck(string bufferContent, int wordLength)
    {
        lock (_bufferLock)
        {
            _rollingBuffer.Clear();
            _rollingBuffer.Append(bufferContent);
            return IsAtSentenceStart(wordLength);
        }
    }

    public void TriggerAutoFinish()
    {
        if (string.IsNullOrEmpty(_pendingTabCompletion) || _currentWord.Length == 0) return;

        string completedWord = _pendingTabCompletion;
        string typedStr = _currentWord.ToString();
        int typedLen = typedStr.Length;
        IntPtr target = _lastForegroundWindow;
        _currentWord.Clear();
        _pendingTabCompletion = string.Empty;
        _tabPressedTime = 0;
        _altPressedTime = 0;
        OnTabCompletionDismissed?.Invoke();

        Task.Run(async () =>
        {
            await Task.Delay(20);
            if (completedWord.StartsWith(typedStr, StringComparison.OrdinalIgnoreCase) && completedWord.Length > typedLen)
            {
                string suffix = completedWord.Substring(typedLen);
                TextAutomationService.AppendSuffix(suffix, ' ', target);
            }
            else
            {
                TextAutomationService.ReplaceText(typedLen, completedWord, ' ', target);
            }
        });

        _previousWord = completedWord;
        _settings.TotalWordsCompleted++;
        _settings.Save();
    }

    private bool CheckIsAutoFinishTrigger(int vkCode)
    {
        if (!_settings.TabCompletionEnabled || string.IsNullOrEmpty(_pendingTabCompletion) || _currentWord.Length == 0)
            return false;

        string hotkey = _settings.AutoFinishHotkey ?? "Tab+Right";

        bool isCtrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
        bool isShift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        bool isAlt = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        bool isTabDown = (GetAsyncKeyState(0x09) & 0x8000) != 0;
        long now = Environment.TickCount64;

        switch (hotkey)
        {
            case "Tab":
                return vkCode == 0x09 && !isCtrl && !isAlt;

            case "Ctrl+Tab":
                return (vkCode == 0x09 && isCtrl) || (vkCode == 0x11 && isTabDown);

            case "Shift+Tab":
                return (vkCode == 0x09 && isShift) || (vkCode == 0x10 && isTabDown);

            case "Right":
                return vkCode == 0x27 && !isCtrl && !isAlt;

            case "Alt+Right":
                if (vkCode == 0x27)
                {
                    bool altRecentlyTapped = (_altPressedTime > 0 && (now - _altPressedTime) < 3000);
                    return isAlt || altRecentlyTapped;
                }
                return false;

            case "Tab+Right":
            default:
                if (vkCode == 0x27)
                {
                    bool tabRecentlyTapped = (_tabPressedTime > 0 && (now - _tabPressedTime) < 3000);
                    return isTabDown || tabRecentlyTapped;
                }
                return false;
        }
    }

    private char GetCharFromKey(uint vkCode, uint scanCode)
    {
        bool isShift = (GetAsyncKeyState(0x10) & 0x8000) != 0; // VK_SHIFT physical state

        // 1. Letters A-Z
        if (vkCode >= 0x41 && vkCode <= 0x5A)
        {
            bool caps = (GetKeyState(0x14) & 1) != 0; // VK_CAPITAL (CapsLock toggle active)
            bool isUpper = isShift ^ caps;
            return isUpper ? (char)vkCode : (char)(vkCode + 32);
        }

        // 2. Digits 0-9
        if (vkCode >= 0x30 && vkCode <= 0x39)
        {
            if (!isShift) return (char)vkCode;
            return vkCode switch
            {
                0x31 => '!', 0x32 => '@', 0x33 => '#', 0x34 => '$', 0x35 => '%',
                0x36 => '^', 0x37 => '&', 0x38 => '*', 0x39 => '(', 0x30 => ')',
                _ => (char)vkCode
            };
        }

        // 3. Punctuation
        return vkCode switch
        {
            0x20 => ' ',               // Space
            0x0D => '\n',              // Enter
            0x09 => '\t',              // Tab
            0xBE => isShift ? '>' : '.', // Period
            0xBC => isShift ? '<' : ',', // Comma
            0xBF => isShift ? '?' : '/', // Slash / Question mark
            0xBA => isShift ? ':' : ';', // Semicolon / Colon
            0xDE => isShift ? '"' : '\'', // Quote / Apostrophe
            0xBD => isShift ? '_' : '-', // Hyphen / Underscore
            _ => '\0'
        };
    }

    public void Dispose()
    {
        Stop();
    }
}
