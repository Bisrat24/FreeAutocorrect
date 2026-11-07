using System;
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

public enum GrammarlyPillar
{
    Correctness,
    Clarity,
    Engagement,
    Delivery
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

    public GrammarlyPillar Pillar
    {
        get
        {
            return Category switch
            {
                LintCategory.Spelling => GrammarlyPillar.Correctness,
                LintCategory.Grammar => GrammarlyPillar.Correctness,
                LintCategory.Punctuation => GrammarlyPillar.Correctness,
                LintCategory.Style => (Rule.Contains("Wordiness", StringComparison.OrdinalIgnoreCase) || 
                                       Rule.Contains("Vocabulary", StringComparison.OrdinalIgnoreCase) || 
                                       Rule.Contains("Repetition", StringComparison.OrdinalIgnoreCase) ||
                                       Rule.Contains("Variety", StringComparison.OrdinalIgnoreCase))
                    ? GrammarlyPillar.Engagement
                    : GrammarlyPillar.Clarity,
                _ => (Rule.Contains("Tone", StringComparison.OrdinalIgnoreCase) || 
                      Rule.Contains("Polite", StringComparison.OrdinalIgnoreCase) ||
                      Rule.Contains("Confidence", StringComparison.OrdinalIgnoreCase))
                    ? GrammarlyPillar.Delivery
                    : GrammarlyPillar.Clarity
            };
        }
    }

    public string PillarName => Pillar.ToString();

    public string PillarColor => Pillar switch
    {
        GrammarlyPillar.Correctness => "#EF4444", // Red / Coral
        GrammarlyPillar.Clarity => "#0284C7",     // Sky Blue
        GrammarlyPillar.Engagement => "#10B981",  // Emerald Green
        GrammarlyPillar.Delivery => "#8B5CF6",    // Violet / Purple
        _ => "#0284C7"
    };

    public string PillarBadgeBg => Pillar switch
    {
        GrammarlyPillar.Correctness => "#3F1D24", // Dark Red
        GrammarlyPillar.Clarity => "#0C4A6E",     // Dark Blue
        GrammarlyPillar.Engagement => "#064E3B",  // Dark Green
        GrammarlyPillar.Delivery => "#3B0764",    // Dark Purple
        _ => "#0C4A6E"
    };

    public string PillarIcon => Pillar switch
    {
        GrammarlyPillar.Correctness => "🔴",
        GrammarlyPillar.Clarity => "🔵",
        GrammarlyPillar.Engagement => "🟢",
        GrammarlyPillar.Delivery => "🟣",
        _ => "🔵"
    };

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
            return PillarColor;
        }
    }

    public string CategoryDisplayName
    {
        get
        {
            return Pillar switch
            {
                GrammarlyPillar.Correctness => "Correctness",
                GrammarlyPillar.Clarity => "Clarity",
                GrammarlyPillar.Engagement => "Engagement",
                GrammarlyPillar.Delivery => "Delivery",
                _ => "Clarity"
            };
        }
    }
}
