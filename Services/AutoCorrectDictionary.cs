using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FreeAutocorrect.Services;

public class AutoCorrectDictionary
{
    private readonly Dictionary<string, string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _validWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _wordBuckets = new();

    public AutoCorrectDictionary()
    {
        InitializeDictionary();
        LoadValidWordList();
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
                            string key = char.ToLower(trimmed[0]) + "_" + trimmed.Length;
                            if (!_wordBuckets.TryGetValue(key, out var list))
                            {
                                list = new List<string>();
                                _wordBuckets[key] = list;
                            }
                            list.Add(trimmed);
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
            "experience", "assistant", "system", "windows", "application", "desktop", "feature", "features", "always"
        };
        foreach (var w in coreWords)
        {
            _validWords.Add(w);
            string key = char.ToLower(w[0]) + "_" + w.Length;
            if (!_wordBuckets.TryGetValue(key, out var list))
            {
                list = new List<string>();
                _wordBuckets[key] = list;
            }
            if (!list.Contains(w)) list.Add(w);
        }
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
            string? singleMatch = null;
            int matchCount = 0;

            for (int len = word.Length - 1; len <= word.Length + 1; len++)
            {
                string key = firstChar + "_" + len;
                if (_wordBuckets.TryGetValue(key, out var bucket))
                {
                    foreach (var candidate in bucket)
                    {
                        if (FastEditDistance(word, candidate, 1) == 1)
                        {
                            singleMatch = candidate;
                            matchCount++;
                            if (matchCount > 1) break; // Ambiguous, don't guess wrongly
                        }
                    }
                }
                if (matchCount > 1) break;
            }

            if (matchCount == 1 && !string.IsNullOrEmpty(singleMatch))
            {
                correction = PreserveCase(word, singleMatch);
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
        }
    }
}
