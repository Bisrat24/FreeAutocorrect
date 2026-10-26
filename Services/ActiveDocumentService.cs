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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public static DocumentContext GetDocumentContext(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return new DocumentContext();

        try
        {
            // 1. Query focused element
            AutomationElement? element = null;
            try
            {
                element = AutomationElement.FocusedElement;
            }
            catch { }

            if (element == null)
            {
                try
                {
                    element = AutomationElement.FromHandle(hwnd);
                }
                catch { }
            }

            if (element != null)
            {
                var ctx = ExtractFromElement(element);
                if (ctx.IsSupported) return ctx;
            }

            // 2. Query child document / edit element in window
            try
            {
                var windowElement = AutomationElement.FromHandle(hwnd);
                if (windowElement != null)
                {
                    var cond = new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
                    );
                    var child = windowElement.FindFirst(TreeScope.Descendants, cond);
                    if (child != null && child != element)
                    {
                        var childCtx = ExtractFromElement(child);
                        if (childCtx.IsSupported) return childCtx;
                    }
                }
            }
            catch { }

            // 3. Fallback: Win32 WM_GETTEXT for standard edit controls
            try
            {
                int len = (int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                if (len > 0 && len < 20000)
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
            catch { }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ActiveDoc", $"GetDocumentContext error: {ex.Message}");
        }

        return new DocumentContext();
    }

    private static DocumentContext ExtractFromElement(AutomationElement element)
    {
        try
        {
            // Try TextPattern (Outlook, Word, Web Mail in Opera/Chrome/Edge, Monaco in Antigravity)
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object tpObj) && tpObj is TextPattern tp)
            {
                string fullText = string.Empty;
                try
                {
                    fullText = tp.DocumentRange.GetText(12000) ?? string.Empty;
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
                        if (before.Length > 600) before = before.Substring(before.Length - 600);
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

            // Try ValuePattern (standard text boxes / inputs)
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj) && vpObj is ValuePattern vp)
            {
                string val = vp.Current.Value ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return new DocumentContext
                    {
                        FullText = val,
                        TextBeforeCaret = val,
                        IsSupported = true,
                        TargetElement = element
                    };
                }
            }
        }
        catch { }

        return new DocumentContext();
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
