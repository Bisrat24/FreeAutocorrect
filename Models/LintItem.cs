using System.Collections.Generic;

namespace FreeAutocorrect.Models;

public enum LintCategory
{
    Spelling,
    Grammar,
    Style,
    Punctuation,
    Miscellaneous
}

public class LintItem
{
    public string Rule { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public LintCategory Category { get; set; } = LintCategory.Grammar;
    public int CharStart { get; set; }
    public int CharEnd { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Priority { get; set; }
    public List<string> Suggestions { get; set; } = new();
    public string MatchedText { get; set; } = string.Empty;

    public string PrimarySuggestion
    {
        get
        {
            if (Suggestions.Count == 0) return string.Empty;
            var s = Suggestions[0];
            // Harper outputs: "Replace with: “a”" or "Remove: “the”"
            if (s.StartsWith("Replace with: “") && s.EndsWith("”"))
            {
                return s.Substring("Replace with: “".Length, s.Length - "Replace with: “".Length - 1);
            }
            if (s.StartsWith("Replace with: \"") && s.EndsWith("\""))
            {
                return s.Substring("Replace with: \"".Length, s.Length - "Replace with: \"".Length - 1);
            }
            if (s.StartsWith("Remove: "))
            {
                return string.Empty;
            }
            return s;
        }
    }

    public string FormattedBadgeColor
    {
        get
        {
            return Category switch
            {
                LintCategory.Spelling => "#EF4444",      // Red
                LintCategory.Grammar => "#3B82F6",       // Blue
                LintCategory.Style => "#F59E0B",         // Amber
                LintCategory.Punctuation => "#10B981",   // Emerald
                _ => "#8B5CF6"                           // Purple
            };
        }
    }

    public string CategoryDisplayName
    {
        get
        {
            return Category switch
            {
                LintCategory.Spelling => "Spelling",
                LintCategory.Grammar => "Grammar",
                LintCategory.Style => "Style & Clarity",
                LintCategory.Punctuation => "Punctuation",
                _ => "Improvement"
            };
        }
    }
}
