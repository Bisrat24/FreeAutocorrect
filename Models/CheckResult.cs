using System.Collections.Generic;

namespace FreeAutocorrect.Models;

public class CheckResult
{
    public string OriginalText { get; set; } = string.Empty;
    public List<LintItem> Lints { get; set; } = new();
    public int WordCount { get; set; }
    public int CharacterCount { get; set; }
    public int SentenceCount { get; set; }
    public double ReadingTimeMinutes { get; set; }
    public double ReadabilityScore { get; set; }
    public string ReadabilityGrade { get; set; } = "Plain English";
    public string ToneSummary { get; set; } = "Neutral";
    public int SpellingErrorsCount { get; set; }
    public int GrammarErrorsCount { get; set; }
    public int StyleSuggestionsCount { get; set; }
}
