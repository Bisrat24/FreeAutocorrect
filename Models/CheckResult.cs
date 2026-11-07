using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeAutocorrect.Models;

public class CheckResult
{
    public string OriginalText { get; set; } = string.Empty;
    public List<LintItem> Lints { get; set; } = new();
    public int WordCount { get; set; }
    public int CharacterCount { get; set; }
    public int SentenceCount { get; set; }
    public double ReadingTimeMinutes { get; set; }
    public double SpeakingTimeMinutes { get; set; }
    public double ReadabilityScore { get; set; }
    public string ReadabilityGrade { get; set; } = "Plain English";
    public string ToneSummary { get; set; } = "Neutral";
    public int SpellingErrorsCount { get; set; }
    public int GrammarErrorsCount { get; set; }
    public int StyleSuggestionsCount { get; set; }

    // Official Grammarly 4-Pillars Count Breakdown
    public int CorrectnessCount => Lints.Count(l => l.Pillar == GrammarlyPillar.Correctness);
    public int ClarityCount => Lints.Count(l => l.Pillar == GrammarlyPillar.Clarity);
    public int EngagementCount => Lints.Count(l => l.Pillar == GrammarlyPillar.Engagement);
    public int DeliveryCount => Lints.Count(l => l.Pillar == GrammarlyPillar.Delivery);

    // Grammarly Overall Performance Score (0 - 100)
    public int OverallScore
    {
        get
        {
            if (WordCount == 0 && Lints.Count == 0) return 100;
            if (Lints.Count == 0) return 100;

            int penalty = (CorrectnessCount * 6) + (ClarityCount * 3) + (EngagementCount * 2) + (DeliveryCount * 1);
            int score = 100 - penalty;
            return Math.Clamp(score, 15, 100);
        }
    }

    public string ScoreRating
    {
        get
        {
            return OverallScore switch
            {
                >= 95 => "Exceptional",
                >= 85 => "Great job!",
                >= 70 => "Looking good",
                >= 50 => "Needs polish",
                _ => "Needs attention"
            };
        }
    }

    public string ScoreRatingColor
    {
        get
        {
            return OverallScore switch
            {
                >= 85 => "#10B981", // Emerald
                >= 70 => "#38BDF8", // Sky Blue
                >= 50 => "#F59E0B", // Amber
                _ => "#EF4444"      // Red
            };
        }
    }
}
