using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public class AutoCorrectDictionary
{
    private readonly Dictionary<string, string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _validWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _c2Vocabulary = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _wordBuckets = new();
    private readonly Dictionary<string, string> _phrases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _popularCompletions = new();

    public AutoCorrectDictionary()
    {
        InitializeDictionary();
        InitializePhrases();
        LoadValidWordList();
        LoadExternalTypos();
    }

    private void InitializePhrases()
    {
        var phraseList = new (string phrase, string fix)[]
        {
            ("a apple", "an apple"),
            ("a hour", "an hour"),
            ("a honest", "an honest"),
            ("a honor", "an honor"),
            ("an car", "a car"),
            ("an book", "a book"),
            ("an user", "a user"),
            ("an university", "a university"),
            ("could of", "could have"),
            ("should of", "should have"),
            ("would of", "would have"),
            ("must of", "must have"),
            ("might of", "might have"),
            ("the the", "the"),
            ("that that", "that"),
            ("this this", "this"),
            ("is is", "is"),
            ("it it", "it"),
            ("in in", "in"),
            ("to to", "to"),
            ("for for", "for"),
            ("and and", "and"),
            ("on on", "on")
        };

        foreach (var (p, f) in phraseList)
        {
            _phrases[p] = f;
        }
    }

    private void LoadExternalTypos()
    {
        try
        {
            var paths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harper-bin", "typos.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "harper-bin", "typos.json"),
                @"C:\Users\Bisrat\Documents\APPS\FreeAutocorrect\harper-bin\typos.json",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeAutocorrect", "custom_typos.json")
            };

            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    var json = File.ReadAllText(p);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (dict != null)
                        {
                            MergeExternalTypos(dict);
                        }
                    }
                }
            }
        }
        catch { }
    }

    public int MergeExternalTypos(Dictionary<string, string> entries)
    {
        int added = 0;
        foreach (var kvp in entries)
        {
            if (!_words.ContainsKey(kvp.Key))
            {
                _words[kvp.Key] = kvp.Value;
                _validWords.Add(kvp.Value);
                _c2Vocabulary.Add(kvp.Value);
                added++;
            }
        }
        return added;
    }

    public bool TryGetPhraseCorrection(string twoWords, out string correction)
    {
        correction = string.Empty;
        if (string.IsNullOrWhiteSpace(twoWords)) return false;

        if (_phrases.TryGetValue(twoWords.Trim(), out var fix))
        {
            correction = PreserveCase(twoWords, fix);
            return true;
        }

        return false;
    }

    public bool GetPrefixCompletion(string prefix, out string completion)
    {
        completion = string.Empty;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) return false;

        string lowerPrefix = prefix.ToLowerInvariant();

        // 1. Check top popular completion list first
        foreach (var word in _popularCompletions)
        {
            if (word.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase) && word.Length > prefix.Length)
            {
                completion = PreserveCase(prefix, word);
                return true;
            }
        }

        // 2. Check word buckets of matching starting letter
        char firstChar = lowerPrefix[0];
        string? bestCandidate = null;

        for (int len = prefix.Length + 1; len <= prefix.Length + 10; len++)
        {
            string key = firstChar + "_" + len;
            if (_wordBuckets.TryGetValue(key, out var bucket))
            {
                foreach (var candidate in bucket)
                {
                    if (candidate.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        bestCandidate = candidate;
                        break;
                    }
                }
            }
            if (bestCandidate != null) break;
        }

        if (bestCandidate != null)
        {
            completion = PreserveCase(prefix, bestCandidate);
            return true;
        }

        return false;
    }

    public bool IsValidWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return true;
        return _validWords.Contains(word);
    }

    public List<string> GetHardWordCandidates(string word, int maxCandidates = 3)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 3) return new List<string>();

        var candidates = new List<(string word, int dist, bool isC2)>();

        // 0. If there is a direct known typo or repeated character correction, prioritize it as #1
        if (TryGetCorrection(word, out var directFix) && !directFix.Equals(word, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add((directFix, 0, true));
        }

        char firstChar = char.ToLower(word[0]);

        for (int len = Math.Max(2, word.Length - 1); len <= word.Length + 2; len++)
        {
            string key = firstChar + "_" + len;
            if (_wordBuckets.TryGetValue(key, out var bucket))
            {
                foreach (var candidate in bucket)
                {
                    if (candidate.Length < 2 || !IsPureAsciiWord(candidate)) continue;

                    int dist = FastEditDistance(word, candidate, 2);
                    if (dist >= 1 && dist <= 2)
                    {
                        bool isC2 = _c2Vocabulary.Contains(candidate);
                        candidates.Add((candidate, dist, isC2));
                    }
                }
            }
        }

        // Prioritize:
        // 1. Edit distance 1 over distance 2
        // 2. Familiar, common C2-level English words over obscure dictionary words
        // 3. Length closeness
        var sorted = candidates
            .DistinctBy(c => c.word, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.dist)
            .ThenByDescending(c => c.isC2)
            .ThenBy(c => Math.Abs(c.word.Length - word.Length))
            .Take(maxCandidates)
            .Select(c => PreserveCase(word, c.word))
            .ToList();

        return sorted;
    }

    private static bool IsPureAsciiWord(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
        }
        return true;
    }

    private void LoadValidWordList()
    {
        try
        {
            var possiblePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harper-bin", "words.txt"),
                Path.Combine(Directory.GetCurrentDirectory(), "harper-bin", "words.txt"),
                @"C:\Users\Bisrat\Documents\APPS\FreeAutocorrect\harper-bin\words.txt"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
                    {
                        var trimmed = line.Trim().Trim('"', '\'');
                        if (trimmed.Length >= 2 && !trimmed.Contains(' '))
                        {
                            _validWords.Add(trimmed);
                            if (IsPureAsciiWord(trimmed))
                            {
                                if (trimmed.Length >= 3 && trimmed.Length <= 14 && char.IsLower(trimmed[0]))
                                {
                                    _c2Vocabulary.Add(trimmed);
                                }
                                string key = char.ToLower(trimmed[0]) + "_" + trimmed.Length;
                                if (!_wordBuckets.TryGetValue(key, out var list))
                                {
                                    list = new List<string>();
                                    _wordBuckets[key] = list;
                                }
                                list.Add(trimmed);
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch { }

        // Core fallback words in case file is absent
        string[] coreWords =
        {
            "it", "is", "not", "working", "hello", "the", "that", "this", "with", "from", "have", "what", "where",
            "when", "there", "please", "really", "good", "great", "thanks", "suggestion", "suggestions", "different",
            "repository", "minimize", "continuously", "grammar", "tomorrow", "necessary", "language", "offline",
            "experience", "assistant", "system", "windows", "application", "desktop", "feature", "features", "always",
            "word", "words", "text", "check", "check", "write", "writing", "replace", "setting", "settings",
            "can't", "won't", "don't", "doesn't", "didn't", "isn't", "aren't", "wasn't", "weren't", "haven't",
            "hasn't", "hadn't", "wouldn't", "couldn't", "shouldn't", "it's", "that's", "what's", "there's", "here's",
            "they're", "we're", "you're", "I'm", "I've", "we've", "they've", "I'll", "you'll", "he'll", "she'll",
            "we'll", "they'll", "I'd", "you'd", "he'd", "she'd", "we'd", "they'd"
        };
        foreach (var w in coreWords)
        {
            _validWords.Add(w);
            _c2Vocabulary.Add(w);
            if (IsPureAsciiWord(w))
            {
                string key = char.ToLower(w[0]) + "_" + w.Length;
                if (!_wordBuckets.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    _wordBuckets[key] = list;
                }
                if (!list.Contains(w)) list.Add(w);
            }
        }

        string[] popular =
        {
            "suggestion", "suggestions", "different", "repository", "continuously", "minimize",
            "grammar", "tomorrow", "necessary", "language", "offline", "experience",
            "assistant", "application", "windows", "desktop", "features", "development",
            "environment", "government", "knowledge", "recommend", "recommended",
            "opportunity", "intelligence", "interesting", "immediately", "unfortunately",
            "completely", "definitely", "basically", "especially", "communication",
            "understand", "information", "important", "question", "questions",
            "problem", "problems", "solution", "solutions", "technology", "community",
            "antigravity", "gemini", "github"
        };
        _popularCompletions.AddRange(popular);
    }

    public bool TryGetCorrection(string word, out string correction)
    {
        correction = string.Empty;
        if (string.IsNullOrWhiteSpace(word)) return false;

        // If single lowercase 'i'
        if (word == "i")
        {
            correction = "I";
            return true;
        }

        if (word.Length < 2) return false;

        // Check if word is already completely valid
        bool isAlreadyValid = _validWords.Contains(word);

        // Double caps at start, e.g. "THis" -> "This", "ABout" -> "About"
        if (word.Length >= 3 && char.IsUpper(word[0]) && char.IsUpper(word[1]) && char.IsLower(word[2]))
        {
            bool restLower = true;
            for (int i = 3; i < word.Length; i++)
            {
                if (!char.IsLower(word[i]))
                {
                    restLower = false;
                    break;
                }
            }

            if (restLower)
            {
                correction = char.ToUpper(word[0]) + word.Substring(1).ToLower();
                return true;
            }
        }

        // 1. Explicit typo table (O(1) high priority)
        if (_words.TryGetValue(word, out var target))
        {
            correction = PreserveCase(word, target);
            return true;
        }

        // 2. Repeated character typos:
        // e.g. "itt" -> "it", "issss" -> "is", "notttt" -> "not", "worrrking" -> "working", "helllo" -> "hello"
        if (!isAlreadyValid && Regex.IsMatch(word, @"(.)\1+"))
        {
            string c1 = Regex.Replace(word, @"(.)\1+", "$1");
            string c2 = Regex.Replace(word, @"(.)\1{2,}", "$1$1");

            if (_validWords.Contains(c1))
            {
                // If c1 is valid and is a short/core word, or c2 is invalid, prefer c1
                if (!_validWords.Contains(c2) || c1.Length <= 3)
                {
                    correction = PreserveCase(word, c1);
                    return true;
                }
            }

            if (_validWords.Contains(c2))
            {
                correction = PreserveCase(word, c2);
                return true;
            }

            if (_validWords.Contains(c1))
            {
                correction = PreserveCase(word, c1);
                return true;
            }
        }

        // 3. Fast Damerau-Levenshtein algorithmic candidate matching for unknown words (length >= 4)
        if (!isAlreadyValid && word.Length >= 4 && !word.Contains('\'') && !word.Contains('-'))
        {
            char firstChar = char.ToLower(word[0]);
            string? bestMatch = null;
            int c2MatchCount = 0;
            int totalMatchCount = 0;

            for (int len = word.Length - 1; len <= word.Length + 1; len++)
            {
                string key = firstChar + "_" + len;
                if (_wordBuckets.TryGetValue(key, out var bucket))
                {
                    foreach (var candidate in bucket)
                    {
                        if (!IsPureAsciiWord(candidate)) continue;

                        if (FastEditDistance(word, candidate, 1) == 1)
                        {
                            totalMatchCount++;
                            if (_c2Vocabulary.Contains(candidate))
                            {
                                bestMatch = candidate;
                                c2MatchCount++;
                            }
                            else if (bestMatch == null)
                            {
                                bestMatch = candidate;
                            }
                        }
                    }
                }
            }

            // Prefer unambiguous match among familiar C2 words
            if (c2MatchCount == 1 && !string.IsNullOrEmpty(bestMatch))
            {
                correction = PreserveCase(word, bestMatch);
                return true;
            }
            else if (totalMatchCount == 1 && !string.IsNullOrEmpty(bestMatch))
            {
                correction = PreserveCase(word, bestMatch);
                return true;
            }
        }

        return false;
    }

    private static int FastEditDistance(string s, string t, int maxDist = 1)
    {
        int n = s.Length, m = t.Length;
        if (Math.Abs(n - m) > maxDist) return 99;

        if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) return 0;

        // Same length: check substitution or adjacent transposition
        if (n == m)
        {
            int diff = 0;
            int firstDiff = -1;
            for (int i = 0; i < n; i++)
            {
                if (char.ToLower(s[i]) != char.ToLower(t[i]))
                {
                    diff++;
                    if (diff > 2) return 99;
                    if (firstDiff == -1) firstDiff = i;
                    else
                    {
                        // Check adjacent transposition: "teh" <-> "the"
                        if (firstDiff == i - 1 &&
                            char.ToLower(s[firstDiff]) == char.ToLower(t[i]) &&
                            char.ToLower(s[i]) == char.ToLower(t[firstDiff]))
                        {
                            diff = 1;
                        }
                    }
                }
            }
            return diff;
        }

        // Difference in length by 1: check single insertion or deletion
        string shorter = n < m ? s : t;
        string longer = n < m ? t : s;
        int iShort = 0, iLong = 0, mismatches = 0;
        while (iShort < shorter.Length && iLong < longer.Length)
        {
            if (char.ToLower(shorter[iShort]) == char.ToLower(longer[iLong]))
            {
                iShort++;
                iLong++;
            }
            else
            {
                mismatches++;
                if (mismatches > 1) return 99;
                iLong++;
            }
        }
        return 1;
    }

    public void AddCustomWord(string typo, string fix)
    {
        _words[typo] = fix;
        _validWords.Add(fix);
    }

    public void RemoveCustomWord(string typo)
    {
        _words.Remove(typo);
    }

    public List<LintItem> LintText(string text)
    {
        var list = new List<LintItem>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        // Extract all link and URL ranges so no suggestions or lint errors are ever generated on links
        var linkRanges = LinkFilterHelper.GetLinkRanges(text);

        // 1. Check all individual words across the multi-line text
        var wordMatches = Regex.Matches(text, @"\b[\w';-]+\b");
        foreach (Match m in wordMatches)
        {
            string rawWord = m.Value;
            if (rawWord.Length < 2) continue;

            // Never make suggestions or flag errors on links, URLs, emails, or file paths
            if (LinkFilterHelper.IsInsideLink(m.Index, m.Length, linkRanges))
            {
                continue;
            }

            // 1. Double Capitals check (e.g. THis -> This, HEllo -> Hello)
            if (rawWord.Length >= 3 && char.IsUpper(rawWord[0]) && char.IsUpper(rawWord[1]) && char.IsLower(rawWord[2]))
            {
                bool allUpper = rawWord.All(c => char.IsUpper(c) || !char.IsLetter(c));
                if (!allUpper)
                {
                    string fixedDoubleCaps = char.ToUpper(rawWord[0]) + rawWord.Substring(1).ToLowerInvariant();
                    int line = 1, lastNewline = -1;
                    for (int i = 0; i < m.Index; i++) if (text[i] == '\n') { line++; lastNewline = i; }
                    list.Add(new LintItem
                    {
                        Rule = "Double Capitals",
                        Kind = "Spelling",
                        Category = LintCategory.Spelling,
                        CharStart = m.Index,
                        CharEnd = m.Index + m.Length,
                        Line = line,
                        Column = m.Index - lastNewline,
                        MatchedText = rawWord,
                        Message = $"Double capital letter: '{rawWord}' → '{fixedDoubleCaps}'",
                        Suggestions = new List<string> { $"Replace with: \"{fixedDoubleCaps}\"" },
                        Priority = 92
                    });
                    continue;
                }
            }

            // 2. Dictionary typo check
            if (TryGetCorrection(rawWord, out var fix) && !string.Equals(rawWord, fix, StringComparison.Ordinal))
            {
                // Calculate line and column
                int line = 1;
                int lastNewline = -1;
                for (int i = 0; i < m.Index; i++)
                {
                    if (text[i] == '\n')
                    {
                        line++;
                        lastNewline = i;
                    }
                }
                int col = m.Index - lastNewline;

                list.Add(new LintItem
                {
                    Rule = "Typo Correction",
                    Kind = "Spelling",
                    Category = LintCategory.Spelling,
                    CharStart = m.Index,
                    CharEnd = m.Index + m.Length,
                    Line = line,
                    Column = col,
                    MatchedText = rawWord,
                    Message = $"Spelling error on line {line}: '{rawWord}' → '{fix}'",
                    Suggestions = new List<string> { $"Replace with: \"{fix}\"" },
                    Priority = 90
                });
            }
            else if (char.IsLower(rawWord[0]))
            {
                // 3. Check uncapitalized sentence start (requires preceding newline or sentence punctuation)
                int i = m.Index - 1;
                while (i >= 0 && (text[i] == ' ' || text[i] == '\t')) i--;
                bool isSentenceStart = i >= 0 && (text[i] == '\n' || text[i] == '\r' || text[i] == '.' || text[i] == '!' || text[i] == '?');
                if (isSentenceStart && (text[i] != '.' || i == 0 || !char.IsDigit(text[i - 1])))
                {
                    // Also exclude ellipsis "..."
                    if (text[i] == '.' && i > 0 && text[i - 1] == '.')
                    {
                        isSentenceStart = false;
                    }
                }
                if (isSentenceStart)
                {
                    string cap = char.ToUpper(rawWord[0]) + rawWord.Substring(1);
                    int line = 1, lastNewline = -1;
                    for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
                    list.Add(new LintItem
                    {
                        Rule = "Sentence Capitalization",
                        Kind = "Grammar",
                        Category = LintCategory.Grammar,
                        CharStart = m.Index,
                        CharEnd = m.Index + m.Length,
                        Line = line,
                        Column = m.Index - lastNewline,
                        MatchedText = rawWord,
                        Message = $"Capitalize first letter: '{rawWord}' → '{cap}'",
                        Suggestions = new List<string> { $"Replace with: \"{cap}\"" },
                        Priority = 95
                    });
                }
            }
        }

        // 2. Check multi-word phrases across the multi-line text
        for (int i = 0; i < wordMatches.Count - 1; i++)
        {
            var m1 = wordMatches[i];
            var m2 = wordMatches[i + 1];

            if (LinkFilterHelper.IsInsideLink(m1.Index, (m2.Index + m2.Length) - m1.Index, linkRanges))
            {
                continue;
            }

            int betweenStart = m1.Index + m1.Length;
            int betweenLen = m2.Index - betweenStart;
            if (betweenLen >= 1 && betweenLen <= 4)
            {
                string between = text.Substring(betweenStart, betweenLen);
                if (between.Trim().Length == 0) // whitespace or newline
                {
                    string phrase = $"{m1.Value} {m2.Value}";
                    if (TryGetPhraseCorrection(phrase, out var phraseFix) && !string.Equals(phrase, phraseFix, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new LintItem
                        {
                            Rule = "Grammar Phrase",
                            Kind = "Grammar",
                            Category = LintCategory.Grammar,
                            CharStart = m1.Index,
                            CharEnd = m2.Index + m2.Length,
                            MatchedText = phrase,
                            Message = $"Grammar phrase error: '{phrase}' → '{phraseFix}'",
                            Suggestions = new List<string> { $"Replace with: \"{phraseFix}\"" },
                            Priority = 95
                        });
                    }
                }
            }
        }

        // 3. Extra Spaces & Spacing Punctuation Checks
        // 3a. Multiple consecutive spaces between words
        var extraSpaceMatches = Regex.Matches(text, @"(?<=\S)[^\S\r\n]{2,}(?=\S)");
        foreach (Match m in extraSpaceMatches)
        {
            if (LinkFilterHelper.IsInsideLink(m.Index, m.Length, linkRanges)) continue;

            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            list.Add(new LintItem
            {
                Rule = "Extra Space",
                Kind = "Punctuation",
                Category = LintCategory.Punctuation,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Extra spaces detected ({m.Length} spaces) between words",
                Suggestions = new List<string> { " " },
                Priority = 85
            });
        }

        // 3b. Space before punctuation (e.g. "word , next" or "end .")
        var spaceBeforePunctMatches = Regex.Matches(text, @"(?<=\S)[^\S\r\n]+([,.:;?!])");
        foreach (Match m in spaceBeforePunctMatches)
        {
            if (LinkFilterHelper.IsInsideLink(m.Index, m.Length, linkRanges)) continue;

            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            string punct = m.Groups[1].Value;
            list.Add(new LintItem
            {
                Rule = "Space Before Punctuation",
                Kind = "Punctuation",
                Category = LintCategory.Punctuation,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Unnecessary space before punctuation '{punct}'",
                Suggestions = new List<string> { punct },
                Priority = 88
            });
        }

        // 3c. Missing space after comma, semicolon, or colon
        var missingCommaSpaceMatches = Regex.Matches(text, @"(?<!\d)([,;:])([A-Za-z])");
        foreach (Match m in missingCommaSpaceMatches)
        {
            if (LinkFilterHelper.IsInsideLink(m.Index, m.Length, linkRanges)) continue;

            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            string fix = $"{m.Groups[1].Value} {m.Groups[2].Value}";
            list.Add(new LintItem
            {
                Rule = "Missing Space",
                Kind = "Punctuation",
                Category = LintCategory.Punctuation,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Missing space after punctuation: '{m.Value}' → '{fix}'",
                Suggestions = new List<string> { fix },
                Priority = 87
            });
        }

        // 3d. Missing space after sentence punctuation (. ! ?) followed by uppercase letter
        var missingSentenceSpace = Regex.Matches(text, @"(?<!\d|\b(?:Mr|Mrs|Ms|Dr|Prof|vs|etc|e\.g|i\.e|[A-Z]))([.!?])([A-Z][a-z])");
        foreach (Match m in missingSentenceSpace)
        {
            if (LinkFilterHelper.IsInsideLink(m.Index, m.Length, linkRanges)) continue;
            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            string fix = $"{m.Groups[1].Value} {m.Groups[2].Value}";
            list.Add(new LintItem
            {
                Rule = "Missing Space",
                Kind = "Punctuation",
                Category = LintCategory.Punctuation,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Missing space between sentences: '{m.Value}' → '{fix}'",
                Suggestions = new List<string> { fix },
                Priority = 89
            });
        }

        // 4. Annotations Checks
        // 4a. Developer & editorial tags: TODO, FIXME, NOTE, TBD, HACK, BUG, XXX, WIP
        var tagMatches = Regex.Matches(text, @"\b(TODO|FIXME|NOTE|TBD|HACK|BUG|XXX|WIP)\s*:\s*([^\r\n]*)", RegexOptions.IgnoreCase);
        foreach (Match m in tagMatches)
        {
            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            string noteBody = m.Groups[2].Value.Trim();
            list.Add(new LintItem
            {
                Rule = "Draft Annotation",
                Kind = "Style",
                Category = LintCategory.Style,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Draft annotation or reminder left in text: '{m.Value}'",
                Suggestions = string.IsNullOrEmpty(noteBody) ? new List<string> { "" } : new List<string> { noteBody, "" },
                Priority = 80
            });
        }

        // 4b. Editorial bracketed placeholders and annotations like [citation needed], [placeholder], [insert ...], [todo ...]
        var bracketMatches = Regex.Matches(text, @"\[(citation needed|clarification needed|placeholder|insert[^\]]*|add[^\]]*|todo[^\]]*|draft|sic|note:[^\]]*)\]|\((author's note|see note)[^)]*\)", RegexOptions.IgnoreCase);
        foreach (Match m in bracketMatches)
        {
            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            list.Add(new LintItem
            {
                Rule = "Editorial Annotation",
                Kind = "Style",
                Category = LintCategory.Style,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Editorial placeholder or draft annotation: '{m.Value}'",
                Suggestions = new List<string> { "" },
                Priority = 82
            });
        }

        // 4c. Code comments left in prose text: /* ... */ or <!-- ... -->
        var commentMatches = Regex.Matches(text, @"/\*[\s\S]*?\*/|<!--[\s\S]*?-->");
        foreach (Match m in commentMatches)
        {
            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            list.Add(new LintItem
            {
                Rule = "Comment Annotation",
                Kind = "Style",
                Category = LintCategory.Style,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Unresolved comment annotation found: '{m.Value}'",
                Suggestions = new List<string> { "" },
                Priority = 81
            });
        }

        // 4d. Consecutive duplicate repeated words (e.g. "the the", "is is")
        var duplicateWordMatches = Regex.Matches(text, @"\b([A-Za-z]{2,})\s+\1\b", RegexOptions.IgnoreCase);
        foreach (Match m in duplicateWordMatches)
        {
            int line = 1, lastNewline = -1;
            for (int k = 0; k < m.Index; k++) if (text[k] == '\n') { line++; lastNewline = k; }
            string singleWord = m.Groups[1].Value;
            list.Add(new LintItem
            {
                Rule = "Duplicate Word",
                Kind = "Grammar",
                Category = LintCategory.Grammar,
                CharStart = m.Index,
                CharEnd = m.Index + m.Length,
                Line = line,
                Column = m.Index - lastNewline,
                MatchedText = m.Value,
                Message = $"Repeated consecutive word: '{m.Value}' → '{singleWord}'",
                Suggestions = new List<string> { singleWord },
                Priority = 94
            });
        }

        return list;
    }

    private string PreserveCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(replacement))
            return replacement;

        // All uppercase
        bool allUpper = true;
        foreach (char c in original)
        {
            if (char.IsLetter(c) && !char.IsUpper(c))
            {
                allUpper = false;
                break;
            }
        }
        if (allUpper && original.Length > 1)
        {
            return replacement.ToUpper();
        }

        // Title case
        if (char.IsUpper(original[0]))
        {
            if (replacement.Length == 1) return replacement.ToUpper();
            return char.ToUpper(replacement[0]) + replacement.Substring(1);
        }

        return replacement;
    }

    private void InitializeDictionary()
    {
        var list = new (string typo, string fix)[]
        {
            ("teh", "the"),
            ("taht", "that"),
            ("thsi", "this"),
            ("thier", "their"),
            ("tiem", "time"),
            ("waht", "what"),
            ("wiht", "with"),
            ("woudl", "would"),
            ("coudl", "could"),
            ("shoudl", "should"),
            ("writng", "writing"),
            ("writting", "writing"),
            ("becuase", "because"),
            ("beacuse", "because"),
            ("becasue", "because"),
            ("beleive", "believe"),
            ("recieve", "receive"),
            ("recieved", "received"),
            ("recieving", "receiving"),
            ("seperate", "separate"),
            ("seperated", "separated"),
            ("seperately", "separately"),
            ("definately", "definitely"),
            ("definitly", "definitely"),
            ("occurrd", "occurred"),
            ("occured", "occurred"),
            ("occuring", "occurring"),
            ("untill", "until"),
            ("wierd", "weird"),
            ("acheive", "achieve"),
            ("acheived", "achieved"),
            ("acheiving", "achieving"),
            ("truely", "truly"),
            ("goverment", "government"),
            ("reccomend", "recommend"),
            ("recommed", "recommend"),
            ("recommand", "recommend"),
            ("calender", "calendar"),
            ("embarass", "embarrass"),
            ("embarassing", "embarrassing"),
            ("neccessary", "necessary"),
            ("necesary", "necessary"),
            ("neccesary", "necessary"),
            ("accomodate", "accommodate"),
            ("accomodation", "accommodation"),
            ("adress", "address"),
            ("adresses", "addresses"),
            ("agressive", "aggressive"),
            ("alot", "a lot"),
            ("alright", "all right"),
            ("apparantly", "apparently"),
            ("arguement", "argument"),
            ("basicly", "basically"),
            ("begining", "beginning"),
            ("collegue", "colleague"),
            ("commited", "committed"),
            ("concious", "conscious"),
            ("dindt", "didn't"),
            ("enviroment", "environment"),
            ("existance", "existence"),
            ("familar", "familiar"),
            ("finaly", "finally"),
            ("foriegn", "foreign"),
            ("fourty", "forty"),
            ("foward", "forward"),
            ("futher", "further"),
            ("grammer", "grammar"),
            ("gaurantee", "guarantee"),
            ("guarentee", "guarantee"),
            ("happend", "happened"),
            ("harasment", "harassment"),
            ("heigth", "height"),
            ("hiearchy", "hierarchy"),
            ("humourous", "humorous"),
            ("ignorence", "ignorance"),
            ("immediatly", "immediately"),
            ("incidently", "incidentally"),
            ("independant", "independent"),
            ("inteligence", "intelligence"),
            ("intresting", "interesting"),
            ("interupt", "interrupt"),
            ("knowlege", "knowledge"),
            ("liason", "liaison"),
            ("libary", "library"),
            ("lightening", "lightning"),
            ("maintainance", "maintenance"),
            ("millenium", "millennium"),
            ("mischievious", "mischievous"),
            ("misspell", "misspell"),
            ("mispell", "misspell"),
            ("mispelled", "misspelled"),
            ("neice", "niece"),
            ("noticable", "noticeable"),
            ("occurence", "occurrence"),
            ("oppurtunity", "opportunity"),
            ("oponent", "opponent"),
            ("paralell", "parallel"),
            ("pastime", "pastime"),
            ("persistant", "persistent"),
            ("peice", "piece"),
            ("posession", "possession"),
            ("posess", "possess"),
            ("potatos", "potatoes"),
            ("prefered", "preferred"),
            ("privilege", "privilege"),
            ("privelege", "privilege"),
            ("priviledge", "privilege"),
            ("profesor", "professor"),
            ("promiss", "promise"),
            ("pronounciation", "pronunciation"),
            ("publically", "publicly"),
            ("realy", "really"),
            ("religous", "religious"),
            ("remeber", "remember"),
            ("resistence", "resistance"),
            ("rythm", "rhythm"),
            ("secratary", "secretary"),
            ("sieze", "seize"),
            ("sence", "sense"),
            ("similer", "similar"),
            ("sincerly", "sincerely"),
            ("speach", "speech"),
            ("succesful", "successful"),
            ("supercede", "supersede"),
            ("suprise", "surprise"),
            ("tommorow", "tomorrow"),
            ("tommorrow", "tomorrow"),
            ("tomorow", "tomorrow"),
            ("tounge", "tongue"),
            ("twelth", "twelfth"),
            ("tyrany", "tyranny"),
            ("underate", "underrate"),
            ("unforseen", "unforeseen"),
            ("unfortunatly", "unfortunately"),
            ("usefull", "useful"),
            ("vehical", "vehicle"),
            ("vicious", "vicious"),
            ("wheather", "whether"),
            ("whereever", "wherever"),
            ("whitch", "which"),
            ("wich", "which"),
            ("writen", "written"),
            ("writting", "writing"),
            ("yeild", "yield"),

            // Common contractions
            ("dont", "don't"),
            ("cant", "can't"),
            ("wont", "won't"),
            ("didnt", "didn't"),
            ("isnt", "isn't"),
            ("arent", "aren't"),
            ("wasnt", "wasn't"),
            ("werent", "weren't"),
            ("couldnt", "couldn't"),
            ("shouldnt", "shouldn't"),
            ("wouldnt", "wouldn't"),
            ("doesnt", "doesn't"),
            ("hasnt", "hasn't"),
            ("havent", "haven't"),
            ("hadnt", "hadn't"),
            ("youre", "you're"),
            ("theyre", "they're"),
            ("weve", "we've"),
            ("theyve", "they've"),
            ("youve", "you've"),
            ("whats", "what's"),
            ("thats", "that's"),
            ("theres", "there's"),
            ("heres", "here's"),
            ("hows", "how's"),
            ("whos", "who's"),
            ("im", "I'm"),
            ("ive", "I've"),
            ("id", "I'd"),

            // Additional slips
            ("adn", "and"),
            ("nad", "and"),
            ("ahve", "have"),
            ("hvae", "have"),
            ("haev", "have"),
            ("fo", "of"),
            ("ot", "to"),
            ("towrads", "towards"),
            ("yuor", "your"),
            ("you;re", "you're"),
            ("they;re", "they're"),
            ("we;re", "we're"),
            ("don;t", "don't"),
            ("can;t", "can't"),
            ("won;t", "won't"),
            ("it;s", "it's"),
            ("abouta", "about a"),
            ("aswell", "as well"),
            ("eachother", "each other"),
            ("infact", "in fact"),
            ("infront", "in front"),
            ("nevermind", "never mind"),
            ("thankyou", "thank you"),
            ("upto", "up to"),
            ("workign", "working"),
            ("lookign", "looking"),
            ("makign", "making"),
            ("takign", "taking"),
            ("comign", "coming"),
            ("goin", "going"),
            ("goign", "going"),
            ("thnaks", "thanks"),
            ("thansk", "thanks"),
            ("thnks", "thanks"),
            ("palce", "place"),
            ("pelase", "please"),
            ("pleae", "please"),
            ("plz", "please"),
            ("probly", "probably"),
            ("prolly", "probably"),
            ("supose", "suppose"),
            ("suposed", "supposed"),

            // User requested & workplace terms
            ("sugestion", "suggestion"),
            ("sugestions", "suggestions"),
            ("diffrent", "different"),
            ("repositiory", "repository"),
            ("doesent", "doesn't"),
            ("minimise", "minimize"),
            ("continously", "continuously"),
            ("grammerly", "Grammarly"),
            ("antigraivty", "Antigravity"),
            ("antigravity", "Antigravity"),
            ("lenght", "length"),
            ("widht", "width"),
            ("consistant", "consistent"),
            ("inconsistant", "inconsistent"),
            ("developement", "development"),
            ("environemnt", "environment"),
            ("occurrance", "occurrence"),
            ("refered", "referred"),
            ("transfered", "transferred"),
            ("cancled", "canceled"),
            ("beleived", "believed"),
            ("sucess", "success"),
            ("atleast", "at least"),
            ("afterall", "after all"),
            ("everytime", "every time"),
            ("didnot", "didn't"),
            ("couldnot", "couldn't"),
            ("shouldnot", "shouldn't"),
            ("wouldnot", "wouldn't")
        };

        foreach (var (typo, fix) in list)
        {
            _words[typo] = fix;
            _validWords.Add(fix);
            _c2Vocabulary.Add(fix);
        }
    }
}
