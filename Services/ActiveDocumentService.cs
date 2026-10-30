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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
                if ((style & ES_READONLY) == 0)
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
            var ct = element.Current.ControlType;

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

                try
                {
                    var selections = tp.GetSelection();
                    if (selections != null && selections.Length > 0)
                    {
                        var caret = selections[0];
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

                if (!string.IsNullOrWhiteSpace(fullText) || !string.IsNullOrWhiteSpace(wordAtCaret))
                {
                    return new DocumentContext
                    {
                        FullText = fullText,
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

    public static string GetWordPrefixBeforeCaret(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;

        try
        {
            var element = AutomationElement.FocusedElement;
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
            var element = AutomationElement.FocusedElement;
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

    public static bool SelectAndReplaceTypo(IntPtr hwnd, string typo, string fix)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(typo) || string.IsNullOrEmpty(fix)) return false;

        try
        {
            var ctx = GetDocumentContext(hwnd);
            if (ctx.TargetElement != null &&
                ctx.TargetElement.TryGetCurrentPattern(TextPattern.Pattern, out object tpObj) &&
                tpObj is TextPattern tp)
            {
                // Find occurrence of typo in document
                var matchRange = tp.DocumentRange.FindText(typo, false, true);
                if (matchRange != null)
                {
                    matchRange.Select();
                    matchRange.ScrollIntoView(true);
                    Thread.Sleep(25);
                    TextAutomationService.ReplaceSelectedTextInActiveApp(fix);
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
}
