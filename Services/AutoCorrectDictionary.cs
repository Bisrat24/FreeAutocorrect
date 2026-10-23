using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FreeAutocorrect.Services;

public class AutoCorrectDictionary
{
    private readonly Dictionary<string, string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _validWords = new(StringComparer.OrdinalIgnoreCase);

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
                    foreach (var line in File.ReadLines(path))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                        {
                            _validWords.Add(trimmed);
                        }
                    }
                    break;
                }
            }
        }
        catch { }

        // Core fallback words in case file is absent
        string[] coreWords = { "it", "is", "not", "working", "hello", "the", "that", "this", "with", "from", "have", "what", "where", "when", "there", "please", "really", "good", "great", "thanks" };
        foreach (var w in coreWords) _validWords.Add(w);
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

        // Known typo table
        if (_words.TryGetValue(word, out var target))
        {
            correction = PreserveCase(word, target);
            return true;
        }

        // If not a known valid word, check repeated character typos:
        // e.g. "itt" -> "it", "issss" -> "is", "notttt" -> "not", "worrrking" -> "working", "helllo" -> "hello"
        if (!isAlreadyValid && Regex.IsMatch(word, @"(.)\1+"))
        {
            // 1. Try collapsing 3+ repeats to 2 (e.g. "helllo" -> "hello")
            string c2 = Regex.Replace(word, @"(.)\1{2,}", "$1$1");
            if (_validWords.Contains(c2))
            {
                correction = PreserveCase(word, c2);
                return true;
            }

            // 2. Try collapsing repeated characters to 1 (e.g. "itt" -> "it", "issss" -> "is", "notttt" -> "not", "worrrking" -> "working")
            string c1 = Regex.Replace(word, @"(.)\1+", "$1");
            if (_validWords.Contains(c1))
            {
                correction = PreserveCase(word, c1);
                return true;
            }

            // 3. Try collapsing only 3+ repeats to 1 while keeping doubles (e.g. "runnning" -> "running")
            string c3 = Regex.Replace(word, @"(.)\1{2,}", "$1");
            if (_validWords.Contains(c3))
            {
                correction = PreserveCase(word, c3);
                return true;
            }
        }

        return false;
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
            ("suposed", "supposed")
        };

        foreach (var (typo, fix) in list)
        {
            _words[typo] = fix;
            _validWords.Add(fix);
        }
    }
}
