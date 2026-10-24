using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint LLKHF_INJECTED = 0x00000001;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

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

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly StringBuilder _currentWord = new();
    private readonly StringBuilder _rollingBuffer = new();
    private readonly object _bufferLock = new();
    private readonly AutoCorrectDictionary _dictionary;
    private readonly AppSettings _settings;
    private System.Threading.Timer? _batchDebounceTimer;

    private IntPtr _lastForegroundWindow = IntPtr.Zero;
    private long _lastKeystrokeTime = 0;
    private long _tabPressedTime = 0;
    private long _altPressedTime = 0;
    private string _previousWord = string.Empty;
    private string _pendingTabCompletion = string.Empty;

    public event Action<string, string>? OnWordCorrected;
    public event Action<string, string>? OnSuggestionFound;
    public event Action<string, string, int, int>? OnSuggestionDetected;
    public event Action<string, IntPtr>? OnTextBatchUpdated;
    public event Action<string, string>? OnTabCompletionAvailable;
    public event Action? OnTabCompletionDismissed;
    public event Action<string, List<string>>? OnHardWordFound;

    public LowLevelKeyboardHook(AutoCorrectDictionary dictionary, AppSettings settings)
    {
        _dictionary = dictionary;
        _settings = settings;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _batchDebounceTimer?.Dispose();
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

    private void ScheduleBatchUpdate()
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
        }, null, 350, System.Threading.Timeout.Infinite);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Skip keystrokes simulated by our own SendInput
            if ((hookStruct.flags & LLKHF_INJECTED) != 0)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (ProcessKeystroke((int)hookStruct.vkCode, (int)hookStruct.scanCode))
            {
                // Delimiter was suppressed because autocorrect is rewriting the word cleanly
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
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

        // Reset buffer if user switched to another window (ignoring FreeAutocorrect's own UI)
        IntPtr currentHwnd = GetForegroundWindow();
        if (currentHwnd != _lastForegroundWindow && currentHwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(currentHwnd, out uint pid);
            if (pid != (uint)Environment.ProcessId)
            {
                _lastForegroundWindow = currentHwnd;
                _currentWord.Clear();
                _previousWord = string.Empty;
                _pendingTabCompletion = string.Empty;
                _tabPressedTime = 0;
                _altPressedTime = 0;
                lock (_bufferLock) { _rollingBuffer.Clear(); }
                OnTabCompletionDismissed?.Invoke();
                OnTextBatchUpdated?.Invoke(string.Empty, currentHwnd);
            }
        }

        // Reset buffer if user paused typing for > 2.5 seconds
        long now = Environment.TickCount64;
        if (_lastKeystrokeTime > 0 && (now - _lastKeystrokeTime) > 2500)
        {
            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            _tabPressedTime = 0;
            _altPressedTime = 0;
            OnTabCompletionDismissed?.Invoke();
        }
        _lastKeystrokeTime = now;

        // 1. Configurable Auto-Finish Trigger (e.g. Tab+Right, Tab, Ctrl+Tab, Right, Shift+Tab, Alt+Right)
        if (_settings.TabCompletionEnabled && !string.IsNullOrEmpty(_pendingTabCompletion) && _currentWord.Length > 0)
        {
            if (CheckIsAutoFinishTrigger(vkCode))
            {
                string completedWord = _pendingTabCompletion;
                int typedLen = _currentWord.Length;
                IntPtr target = _lastForegroundWindow;
                _currentWord.Clear();
                _pendingTabCompletion = string.Empty;
                _tabPressedTime = 0;
                _altPressedTime = 0;
                OnTabCompletionDismissed?.Invoke();

                Task.Run(async () =>
                {
                    await Task.Delay(10);
                    TextAutomationService.ReplaceText(typedLen, completedWord, ' ', target);
                });

                _previousWord = completedWord;
                _settings.TotalWordsCompleted++;
                _settings.Save();
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

        // Navigation keys reset buffer
        if (vkCode == VK_ESCAPE || vkCode == VK_LEFT || 
            vkCode == VK_UP || vkCode == VK_DOWN || vkCode == VK_DELETE)
        {
            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            _tabPressedTime = 0;
            _altPressedTime = 0;
            OnTabCompletionDismissed?.Invoke();
            return false;
        }

        if (vkCode == VK_RIGHT)
        {
            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            _tabPressedTime = 0;
            _altPressedTime = 0;
            OnTabCompletionDismissed?.Invoke();
            return false;
        }

        if (vkCode == VK_BACK)
        {
            lock (_bufferLock)
            {
                if (_rollingBuffer.Length > 0)
                {
                    _rollingBuffer.Length--;
                }
            }
            ScheduleBatchUpdate();

            if (_currentWord.Length > 0)
            {
                _currentWord.Length--;
                if (_settings.TabCompletionEnabled && _currentWord.Length >= 3)
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
            ScheduleBatchUpdate();

            string word = _currentWord.ToString().Trim();
            _currentWord.Clear();

            if (!string.IsNullOrEmpty(word))
            {
                // A. Check multi-word phrase correction if previous word exists
                if (!string.IsNullOrEmpty(_previousWord))
                {
                    string phrase = $"{_previousWord} {word}";
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
                if (_settings.CustomSnippets != null && _settings.CustomSnippets.TryGetValue(word, out var snippet))
                {
                    int sEnd = 0;
                    int sStart = 0;
                    lock (_bufferLock)
                    {
                        sEnd = _rollingBuffer.Length;
                        sStart = Math.Max(0, sEnd - word.Length - 1);
                    }
                    OnSuggestionDetected?.Invoke(word, snippet, sStart, sEnd);
                    OnSuggestionFound?.Invoke(word, snippet);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(word, snippet, typedChar);
                        _previousWord = snippet;
                        return true; // Suppress delimiter
                    }
                    _previousWord = word;
                    return false;
                }

                // C. Check autocorrect dictionary
                if (_dictionary.TryGetCorrection(word, out var corrected) && !string.Equals(word, corrected, StringComparison.Ordinal))
                {
                    int wEnd = 0;
                    int wStart = 0;
                    lock (_bufferLock)
                    {
                        wEnd = _rollingBuffer.Length;
                        wStart = Math.Max(0, wEnd - word.Length - 1);
                    }
                    OnSuggestionDetected?.Invoke(word, corrected, wStart, wEnd);
                    OnSuggestionFound?.Invoke(word, corrected);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(word, corrected, typedChar);
                        _previousWord = corrected;
                        return true; // Suppress delimiter
                    }
                    _previousWord = word;
                    return false;
                }
                else if (word.Length >= 4 && !_dictionary.IsValidWord(word))
                {
                    // Word is hard for auto-suggestion (unrecognized & ambiguous) -> underline & show candidates
                    var candidates = _dictionary.GetHardWordCandidates(word);
                    OnHardWordFound?.Invoke(word, candidates);
                }

                _previousWord = word;
            }
        }
        else if (char.IsLetterOrDigit(typedChar) || typedChar == '\'' || typedChar == '-')
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

            if (_currentWord.Length < 60)
            {
                _currentWord.Append(typedChar);

                // Auto-suggest Tab completion in real time as user types
                if (_settings.TabCompletionEnabled && _currentWord.Length >= 3)
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
        }
        else if (typedChar != '\0')
        {
            _currentWord.Clear();
            _previousWord = string.Empty;
            _pendingTabCompletion = string.Empty;
            OnTabCompletionDismissed?.Invoke();
        }

        return false;
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

            int backspaces = typedPhrase.Length;
            Task.Run(async () =>
            {
                await Task.Delay(10);
                TextAutomationService.ReplaceText(backspaces, replacement, delimiter);
            });

            _settings.TotalWordsCorrected++;
            _settings.Save();

            OnWordCorrected?.Invoke(typedPhrase, replacement);
        }
        catch { }
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

            // Backspace count is EXACTLY typedWord.Length because the delimiter was suppressed!
            int backspaces = typedWord.Length;
            Task.Run(async () =>
            {
                // Give active application 10ms to settle
                await Task.Delay(10);
                TextAutomationService.ReplaceText(backspaces, replacement, delimiter);
            });

            _settings.TotalWordsCorrected++;
            _settings.Save();

            OnWordCorrected?.Invoke(typedWord, replacement);
        }
        catch { }
    }

    public void TriggerAutoFinish()
    {
        if (string.IsNullOrEmpty(_pendingTabCompletion) || _currentWord.Length == 0) return;

        string completedWord = _pendingTabCompletion;
        int typedLen = _currentWord.Length;
        IntPtr target = _lastForegroundWindow;
        _currentWord.Clear();
        _pendingTabCompletion = string.Empty;
        _tabPressedTime = 0;
        _altPressedTime = 0;
        OnTabCompletionDismissed?.Invoke();

        Task.Run(async () =>
        {
            await Task.Delay(10);
            TextAutomationService.ReplaceText(typedLen, completedWord, ' ', target);
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
