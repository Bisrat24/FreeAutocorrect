using System;
using System.Linq;
using System.Text.RegularExpressions;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public static class ReadabilityService
{
    public static CheckResult Analyze(string text)
    {
        var result = new CheckResult
        {
            OriginalText = text
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            result.ReadabilityGrade = "N/A";
            result.ToneSummary = "Neutral";
            return result;
        }

        result.CharacterCount = text.Length;

        // Count words
        var words = Regex.Matches(text, @"\b[\w'-]+\b")
            .Cast<Match>()
            .Select(m => m.Value)
            .ToList();

        result.WordCount = words.Count;
        if (result.WordCount == 0) return result;

        // Count sentences
        var sentences = Regex.Split(text, @"[.!?]+(\s+|$)")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        result.SentenceCount = Math.Max(1, sentences.Count);

        // Average reading speed: ~200 words per minute
        result.ReadingTimeMinutes = Math.Round((double)result.WordCount / 200.0, 1);

        // Count syllables for Flesch-Kincaid
        int totalSyllables = words.Sum(CountSyllables);

        // Flesch Reading Ease formula:
        // 206.835 - 1.015 * (total words / total sentences) - 84.6 * (total syllables / total words)
        double wordsPerSentence = (double)result.WordCount / result.SentenceCount;
        double syllablesPerWord = (double)totalSyllables / result.WordCount;

        double fleschScore = 206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord);
        result.ReadabilityScore = Math.Clamp(Math.Round(fleschScore, 1), 0, 100);

        result.ReadabilityGrade = result.ReadabilityScore switch
        {
            >= 90 => "Very Easy (5th grade)",
            >= 80 => "Easy (6th grade)",
            >= 70 => "Fairly Easy (7th grade)",
            >= 60 => "Standard (8th-9th grade)",
            >= 50 => "Fairly Difficult (10th-12th grade)",
            >= 30 => "Difficult (College)",
            _ => "Very Difficult (Graduate)"
        };

        // Determine Tone
        result.ToneSummary = DetectTone(text, words);

        return result;
    }

    private static int CountSyllables(string word)
    {
        word = word.ToLower().Trim();
        if (word.Length <= 3) return 1;

        word = Regex.Replace(word, @"(?:[^laeiouy]|ed|es|e)$", "");
        word = Regex.Replace(word, @"^y", "");

        var matches = Regex.Matches(word, @"[aeiouy]{1,2}");
        return Math.Max(1, matches.Count);
    }

    private static string DetectTone(string text, System.Collections.Generic.List<string> words)
    {
        int exclamationCount = text.Count(c => c == '!');
        int questionCount = text.Count(c => c == '?');

        var lowerWords = words.Select(w => w.ToLower()).ToHashSet();

        bool hasFormalWords = lowerWords.Overlaps(new[] { "furthermore", "consequently", "therefore", "subsequently", "accordingly", "whereas", "hereby", "moreover" });
        bool hasFriendlyWords = lowerWords.Overlaps(new[] { "thanks", "welcome", "glad", "great", "awesome", "happy", "cheers", "excited", "love", "wonderful" });
        bool hasConfidentWords = lowerWords.Overlaps(new[] { "definitely", "certainly", "clearly", "undoubtedly", "proven", "assure", "guarantee", "convinced" });
        bool hasCautiousWords = lowerWords.Overlaps(new[] { "maybe", "perhaps", "possibly", "might", "could", "somewhat", "presumably" });

        if (hasFormalWords) return "Formal & Academic";
        if (exclamationCount > 0 && hasFriendlyWords) return "Friendly & Enthusiastic";
        if (hasFriendlyWords) return "Warm & Friendly";
        if (hasConfidentWords) return "Confident & Convincing";
        if (hasCautiousWords) return "Cautious & Polite";
        if (words.Count > 10 && words.Count / Math.Max(1, text.Count(c => c == '.')) < 12) return "Direct & Concise";

        return "Neutral & Clear";
    }
}
