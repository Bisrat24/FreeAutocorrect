using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace FreeAutocorrect.Services;

public class DocumentContext
{
    public string FullText { get; set; } = string.Empty;
    public string SelectedText { get; set; } = string.Empty;
    public string WordAtCaret { get; set; } = string.Empty;
    public string TextBeforeCaret { get; set; } = string.Empty;
    public bool IsSupported { get; set; } = false;
    public AutomationElement? TargetElement { get; set; }
}

public static class ActiveDocumentService
{
    private const int WM_GETTEXT = 0x000D;
    private const int WM_GETTEXTLENGTH = 0x000E;
    private const int GWL_STYLE = -16;
    private const int ES_READONLY = 0x0800;
    private const int ES_PASSWORD = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, out int wParam, out int lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Fast Win32 check for classic password edits (ES_PASSWORD). Cheap enough to run per keystroke.
    /// </summary>
    public static bool IsPasswordEditFocused()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint threadId = GetWindowThreadProcessId(fg, out _);
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero) return false;

            int style = GetWindowLong(info.hwndFocus, GWL_STYLE);
            return (style & ES_PASSWORD) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// UIA check for password fields (browsers, Electron apps). Called from the async scan timer, not the hook.
    /// </summary>
    public static bool IsFocusedElementPassword()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            return element != null && element.Current.IsPassword;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static DocumentContext GetDocumentContext(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new DocumentContext();

        try
        {
            uint targetPid = 0;
            GetWindowThreadProcessId(hwnd, out targetPid);

            // 1. Query focused element and verify it belongs to the target app and is EDITABLE
            AutomationElement? element = null;
            try
            {
                element = AutomationElement.FocusedElement;
                if (element != null && targetPid != 0)
                {
                    if (element.Current.ProcessId != (int)targetPid)
                    {
                        element = null;
                    }
                }
            }
            catch { element = null; }

            if (element != null && IsElementEditable(element, out var tp, out var vp))
            {
                var ctx = ExtractFromElement(element, tp, vp);
                if (ctx.IsSupported) return ctx;
            }

            // 2. Query focused child edit element in window (NEVER whole document or read-only page)
            try
            {
                var windowElement = AutomationElement.FromHandle(hwnd);
                if (windowElement != null)
                {
                    var cond = new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true)
                    );
                    var child = windowElement.FindFirst(TreeScope.Descendants, cond);
                    if (child != null && IsElementEditable(child, out var ctp, out var cvp))
                    {
                        var childCtx = ExtractFromElement(child, ctp, cvp);
                        if (childCtx.IsSupported) return childCtx;
                    }
                }
            }
            catch { }

            // 3. Fallback: Win32 WM_GETTEXT only for standard edit controls that are NOT read-only
            try
            {
                int style = GetWindowLong(hwnd, GWL_STYLE);
                if ((style & ES_READONLY) == 0 && (style & ES_PASSWORD) == 0)
                {
                    int len = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                    if (len > 0 && len < 10000)
                    {
                        var sb = new StringBuilder(len + 1);
                        SendMessage(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
                        string text = sb.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            return new DocumentContext
                            {
                                FullText = text,
                                TextBeforeCaret = text,
                                IsSupported = true
                            };
                        }
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ActiveDoc", $"GetDocumentContext error: {ex.Message}");
        }

        return new DocumentContext();
    }

    public static bool IsElementEditable(AutomationElement element, out TextPattern? tp, out ValuePattern? vp)
    {
        tp = null;
        vp = null;
        if (element == null) return false;

        try
        {
            // Password/secure fields must never be read, linted or rewritten
            if (element.Current.IsPassword)
            {
                return false;
            }

            var ct = element.Current.ControlType;

            // Hyperlinks are links, never editable text inputs
            if (ct == ControlType.Hyperlink)
            {
                return false;
            }

            // Check ValuePattern
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj) && vpObj is ValuePattern vPattern)
            {
                vp = vPattern;
                if (vp.Current.IsReadOnly)
                {
                    return false; // Explicitly marked Read-Only
                }
            }

            // Check TextPattern
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object tpObj) && tpObj is TextPattern tPattern)
            {
                tp = tPattern;
                try
                {
                    object roAttr = tp.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                    if (roAttr is bool isRo && isRo)
                    {
                        return false; // Document / text is strictly Read-Only
                    }
                }
                catch { }

                // In web browsers, whole pages are ControlType.Document.
                // An editable draft (contenteditable, textarea, rich editor) has an active caret/selection
                // or has keyboard focus. If a Document has no selection and no focus, it is a static page!
                if (ct == ControlType.Document)
                {
                    try
                    {
                        var selections = tp.GetSelection();
                        if (selections == null || selections.Length == 0)
                        {
                            return false; // Static page, not an active typing caret
                        }
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            // Must be Edit, or Document with active editable caret, or non-readonly ValuePattern
            if (ct == ControlType.Edit)
            {
                return true;
            }

            if (ct == ControlType.Document && tp != null)
            {
                return true;
            }

            if (vp != null && !vp.Current.IsReadOnly)
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static DocumentContext ExtractFromElement(AutomationElement element, TextPattern? tp, ValuePattern? vp)
    {
        try
        {
            if (tp != null)
            {
                string fullText = string.Empty;
                try
                {
                    // Restrict to active draft/edit bounds (max 5,000 characters to keep linting instant)
                    fullText = tp.DocumentRange.GetText(5000) ?? string.Empty;
                }
                catch { }

                string wordAtCaret = string.Empty;
                string textBeforeCaret = string.Empty;
                string selectedText = string.Empty;

                try
                {
                    var selections = tp.GetSelection();
                    if (selections != null && selections.Length > 0)
                    {
                        var caret = selections[0];
                        string sel = caret.GetText(-1)?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(sel))
                        {
                            selectedText = sel;
                        }

                        var wordRange = caret.Clone();
                        wordRange.ExpandToEnclosingUnit(TextUnit.Word);
                        wordAtCaret = wordRange.GetText(-1)?.Trim() ?? string.Empty;

                        var startRange = tp.DocumentRange.Clone();
                        startRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
                        string before = startRange.GetText(-1) ?? string.Empty;
                        if (before.Length > 800) before = before.Substring(before.Length - 800);
                        textBeforeCaret = before;
                    }
                }
                catch { }

                if (!string.IsNullOrWhiteSpace(fullText) || !string.IsNullOrWhiteSpace(wordAtCaret) || !string.IsNullOrWhiteSpace(selectedText))
                {
                    return new DocumentContext
                    {
                        FullText = fullText,
                        SelectedText = selectedText,
                        WordAtCaret = wordAtCaret,
                        TextBeforeCaret = !string.IsNullOrEmpty(textBeforeCaret) ? textBeforeCaret : fullText,
                        IsSupported = true,
                        TargetElement = element
                    };
                }
            }

            if (vp != null)
            {
                string val = vp.Current.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    string word = string.Empty;
                    string before = val;
                    string selectedText = string.Empty;

                    if (element.Current.NativeWindowHandle != 0)
                    {
                        IntPtr editHwnd = (IntPtr)element.Current.NativeWindowHandle;
                        selectedText = GetWin32EditSelectedText(editHwnd);
                    }

                    if (val.Length > 800) before = val.Substring(val.Length - 800);

                    // Extract last word from value
                    int end = val.Length - 1;
                    while (end >= 0 && char.IsWhiteSpace(val[end])) end--;
                    int start = end;
                    while (start >= 0 && LowLevelKeyboardHook.IsWordChar(val[start])) start--;
                    if (end >= 0 && start < end)
                    {
                        word = val.Substring(start + 1, end - start);
                    }

                    return new DocumentContext
                    {
                        FullText = val,
                        SelectedText = selectedText,
                        WordAtCaret = word,
                        TextBeforeCaret = before,
                        IsSupported = true,
                        TargetElement = element
                    };
                }
            }
        }
        catch { }

        return new DocumentContext();
    }

    private static AutomationElement? GetFocusedElementForWindow(IntPtr hwnd)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element == null) return null;

            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0 && element.Current.ProcessId != (int)pid) return null;
            }

            return element;
        }
        catch
        {
            return null;
        }
    }

    public static string GetWordPrefixBeforeCaret(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;

        try
        {
            var element = GetFocusedElementForWindow(hwnd);
            if (element != null && IsElementEditable(element, out var tp, out var vp))
            {
                if (tp != null)
                {
                    var selections = tp.GetSelection();
                    if (selections != null && selections.Length > 0)
                    {
                        var caret = selections[0];
                        var startRange = tp.DocumentRange.Clone();
                        startRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
                        string before = startRange.GetText(-1) ?? string.Empty;
                        if (!string.IsNullOrEmpty(before))
                        {
                            int i = before.Length - 1;
                            while (i >= 0 && LowLevelKeyboardHook.IsWordChar(before[i]))
                            {
                                i--;
                            }
                            int start = i + 1;
                            if (start < before.Length)
                            {
                                return before.Substring(start);
                            }
                        }
                    }
                }
                else if (vp != null && !vp.Current.IsReadOnly)
                {
                    string val = vp.Current.Value ?? string.Empty;
                    if (!string.IsNullOrEmpty(val))
                    {
                        int i = val.Length - 1;
                        while (i >= 0 && LowLevelKeyboardHook.IsWordChar(val[i]))
                        {
                            i--;
                        }
                        int start = i + 1;
                        if (start < val.Length)
                        {
                            return val.Substring(start);
                        }
                    }
                }
            }
        }
        catch { }

        return string.Empty;
    }

    public static string GetWordAtCaret(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;

        try
        {
            var element = GetFocusedElementForWindow(hwnd);
            if (element != null && IsElementEditable(element, out var tp, out var vp))
            {
                if (tp != null)
                {
                    var selections = tp.GetSelection();
                    if (selections != null && selections.Length > 0)
                    {
                        var caret = selections[0];
                        var wordRange = caret.Clone();
                        wordRange.ExpandToEnclosingUnit(TextUnit.Word);
                        string w = wordRange.GetText(-1)?.Trim() ?? string.Empty;
                        return w;
                    }
                }
                else if (vp != null && !vp.Current.IsReadOnly)
                {
                    string val = vp.Current.Value ?? string.Empty;
                    int end = val.Length - 1;
                    while (end >= 0 && char.IsWhiteSpace(val[end])) end--;
                    int start = end;
                    while (start >= 0 && LowLevelKeyboardHook.IsWordChar(val[start])) start--;
                    if (end >= 0 && start < end)
                    {
                        return val.Substring(start + 1, end - start);
                    }
                }
            }
        }
        catch { }

        return string.Empty;
    }

    public static string GetSelectedText(IntPtr hwnd = default)
    {
        if (hwnd == IntPtr.Zero)
        {
            hwnd = GetForegroundWindow();
        }

        try
        {
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return string.Empty;
            }

            var focused = (hwnd != IntPtr.Zero ? GetFocusedElementForWindow(hwnd) : null) ?? AutomationElement.FocusedElement;
            if (focused != null && IsElementEditable(focused, out var tp, out var vp))
            {
                if (tp != null)
                {
                    var selections = tp.GetSelection();
                    if (selections != null && selections.Length > 0)
                    {
                        string sel = selections[0].GetText(-1)?.Trim() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(sel))
                        {
                            AppLogger.Info("SelectedText", $"UIA: Found {sel.Length} chars selected text in 0x{hwnd.ToInt64():X}");
                            return sel;
                        }
                    }
                }

                if (focused.Current.NativeWindowHandle != 0)
                {
                    IntPtr editHwnd = (IntPtr)focused.Current.NativeWindowHandle;
                    string win32Sel = GetWin32EditSelectedText(editHwnd);
                    if (!string.IsNullOrWhiteSpace(win32Sel))
                    {
                        AppLogger.Info("SelectedText", $"Win32Edit: Found {win32Sel.Length} chars selected text in 0x{editHwnd.ToInt64():X}");
                        return win32Sel;
                    }
                }
            }

            if (hwnd != IntPtr.Zero && !IsPasswordEditFocused())
            {
                string guiSel = GetGUIThreadSelectedText(hwnd);
                if (!string.IsNullOrWhiteSpace(guiSel))
                {
                    AppLogger.Info("SelectedText", $"GUIThread: Found {guiSel.Length} chars selected text in 0x{hwnd.ToInt64():X}");
                    return guiSel;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("SelectedText", $"GetSelectedText error: {ex.Message}");
        }

        return string.Empty;
    }

    private static string GetWin32EditSelectedText(IntPtr editHwnd)
    {
        try
        {
            int style = GetWindowLong(editHwnd, GWL_STYLE);
            if ((style & ES_PASSWORD) != 0) return string.Empty;

            SendMessage(editHwnd, 0x00B0 /* EM_GETSEL */, out int start, out int end);
            if (start >= 0 && end > start)
            {
                int len = (int)SendMessage(editHwnd, 0x000E /* WM_GETTEXTLENGTH */, IntPtr.Zero, IntPtr.Zero);
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 1);
                    SendMessage(editHwnd, 0x000D /* WM_GETTEXT */, (IntPtr)(len + 1), sb);
                    string text = sb.ToString();
                    if (end <= text.Length)
                    {
                        return text.Substring(start, end - start).Trim();
                    }
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static string GetGUIThreadSelectedText(IntPtr hwnd)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            if (threadId != 0)
            {
                var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
                if (GetGUIThreadInfo(threadId, ref gui) && gui.hwndFocus != IntPtr.Zero)
                {
                    return GetWin32EditSelectedText(gui.hwndFocus);
                }
            }
        }
        catch { }
        return string.Empty;
    }

    // Search only the text before the caret so an earlier identical word is never overwritten.
    private static TextPatternRange? FindTypoRangeBeforeCaret(TextPattern tp, string typo, int caretOffset)
    {
        var docRange = tp.DocumentRange;
        int docLength = docRange.CompareEndpoints(TextPatternRangeEndpoint.End, docRange, TextPatternRangeEndpoint.Start);
        var searchRange = docRange.Clone();
        int delta = Math.Min(caretOffset, docLength) - docLength;
        if (delta < 0)
            searchRange.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, delta);

        // Backward search from the caret finds the word that was just typed.
        var match = searchRange.FindText(typo, true, true);
        if (match == null) return null;

        var left = match.Clone();
        left.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
        string before = left.GetText(1);
        var right = match.Clone();
        right.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
        string afterText = right.GetText(-1);
        char? after = string.IsNullOrEmpty(afterText) ? null : afterText[^1];

        bool leftOk = string.IsNullOrEmpty(before) || !char.IsLetterOrDigit(before[0]);
        bool rightOk = after == null || !char.IsLetterOrDigit(after.Value);
        return leftOk && rightOk ? match : null;
    }

    public static bool SelectAndReplaceTypo(IntPtr hwnd, string typo, string fix)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(typo) || string.IsNullOrEmpty(fix)) return false;
        if (IsPasswordEditFocused()) return false;

        try
        {
            var ctx = GetDocumentContext(hwnd);
            if (ctx.TargetElement != null &&
                ctx.TargetElement.TryGetCurrentPattern(TextPattern.Pattern, out object tpObj) &&
                tpObj is TextPattern tp)
            {
                var matchRange = FindTypoRangeBeforeCaret(tp, typo, ctx.TextBeforeCaret.Length);
                if (matchRange != null)
                {
                    matchRange.Select();
                    matchRange.ScrollIntoView(true);
                    Thread.Sleep(25);
                    TextAutomationService.ReplaceSelectedTextInActiveApp(hwnd, fix);
                    AppLogger.Info("ActiveDoc", $"SelectAndReplaceTypo: Found and replaced '{typo}' with '{fix}' via UIA in 0x{hwnd.ToInt64():X}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ActiveDoc", $"SelectAndReplaceTypo exception: {ex.Message}");
        }

        return false;
    }

    public static bool SelectAndHighlightTypo(IntPtr hwnd, string typo)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(typo)) return false;
        if (IsPasswordEditFocused()) return false;

        try
        {
            var ctx = GetDocumentContext(hwnd);
            if (ctx.TargetElement != null &&
                ctx.TargetElement.TryGetCurrentPattern(TextPattern.Pattern, out object tpObj) &&
                tpObj is TextPattern tp)
            {
                var matchRange = FindTypoRangeBeforeCaret(tp, typo, ctx.TextBeforeCaret.Length);
                if (matchRange != null)
                {
                    matchRange.Select();
                    matchRange.ScrollIntoView(true);
                    AppLogger.Info("ActiveDoc", $"SelectAndHighlightTypo: Located and highlighted '{typo}' via UIA in 0x{hwnd.ToInt64():X}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ActiveDoc", $"SelectAndHighlightTypo exception: {ex.Message}");
        }

        return false;
    }
}
