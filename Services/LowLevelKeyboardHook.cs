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

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private readonly StringBuilder _currentWord = new();
    private readonly AutoCorrectDictionary _dictionary;
    private readonly AppSettings _settings;

    public event Action<string, string>? OnWordCorrected;
    public event Action<string, string>? OnSuggestionFound;

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

            ProcessKeystroke((int)hookStruct.vkCode, (int)hookStruct.scanCode);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void ProcessKeystroke(int vkCode, int scanCode)
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

        // Navigation keys reset buffer
        if (vkCode == VK_ESCAPE || vkCode == VK_LEFT || vkCode == VK_RIGHT || 
            vkCode == VK_UP || vkCode == VK_DOWN || vkCode == VK_DELETE)
        {
            _currentWord.Clear();
            return;
        }

        if (vkCode == VK_BACK)
        {
            if (_currentWord.Length > 0)
            {
                _currentWord.Length--;
            }
            return;
        }

        // Convert virtual key to character reliably
        char typedChar = GetCharFromKey((uint)vkCode, (uint)scanCode);

        // Word delimiters: Space, Return, Tab, Punctuation (. , ! ? ; :)
        bool isDelimiter = (vkCode == VK_SPACE || vkCode == VK_RETURN || vkCode == VK_TAB ||
                            typedChar == '.' || typedChar == ',' || typedChar == '!' || 
                            typedChar == '?' || typedChar == ';' || typedChar == ':');

        if (isDelimiter)
        {
            string word = _currentWord.ToString().Trim();
            _currentWord.Clear();

            if (!string.IsNullOrEmpty(word))
            {
                // Check custom snippets first
                if (_settings.CustomSnippets != null && _settings.CustomSnippets.TryGetValue(word, out var snippet))
                {
                    OnSuggestionFound?.Invoke(word, snippet);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(word, snippet, typedChar);
                    }
                    return;
                }

                // Check autocorrect dictionary
                if (_dictionary.TryGetCorrection(word, out var corrected) && !string.Equals(word, corrected, StringComparison.Ordinal))
                {
                    OnSuggestionFound?.Invoke(word, corrected);
                    if (_settings.LiveAutocorrectEnabled)
                    {
                        PerformAutocorrect(word, corrected, typedChar);
                    }
                    return;
                }
            }
        }
        else if (char.IsLetterOrDigit(typedChar) || typedChar == '\'' || typedChar == '-')
        {
            if (_currentWord.Length < 60)
            {
                _currentWord.Append(typedChar);
            }
        }
        else if (typedChar != '\0')
        {
            // Non-word characters clear word buffer
            _currentWord.Clear();
        }
    }

    private void PerformAutocorrect(string typedWord, string replacement, char delimiter)
    {
        try
        {
            // Backspace count: typed word length + 1 (the delimiter that was just typed)
            int backspaces = typedWord.Length + 1;
            Task.Run(async () =>
            {
                // Allow the active application 25ms to process the delimiter keystroke
                await Task.Delay(25);
                TextAutomationService.ReplaceText(backspaces, replacement, delimiter);
            });

            _settings.TotalWordsCorrected++;
            _settings.Save();

            OnWordCorrected?.Invoke(typedWord, replacement);
        }
        catch { }
    }

    private char GetCharFromKey(uint vkCode, uint scanCode)
    {
        // 1. Letters A-Z
        if (vkCode >= 0x41 && vkCode <= 0x5A)
        {
            bool shift = GetKeyState(0x10) < 0; // VK_SHIFT (< 0 means pressed)
            bool caps = (GetKeyState(0x14) & 1) != 0; // VK_CAPITAL (CapsLock active)
            bool isUpper = shift ^ caps;
            return isUpper ? (char)vkCode : (char)(vkCode + 32);
        }

        // 2. Digits 0-9
        if (vkCode >= 0x30 && vkCode <= 0x39)
        {
            bool shift = GetKeyState(0x10) < 0;
            if (!shift) return (char)vkCode;
            return vkCode switch
            {
                0x31 => '!', 0x32 => '@', 0x33 => '#', 0x34 => '$', 0x35 => '%',
                0x36 => '^', 0x37 => '&', 0x38 => '*', 0x39 => '(', 0x30 => ')',
                _ => (char)vkCode
            };
        }

        // 3. Punctuation
        bool isShift = GetKeyState(0x10) < 0;
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
