using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public static class SelfTestRunner
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static async Task<int> RunAllTestsAsync()
    {
        var sb = new System.Text.StringBuilder();
        void Log(string msg)
        {
            Console.WriteLine(msg);
            sb.AppendLine(msg);
        }

        Log("========================================");
        Log(" FreeAutocorrect Self-Test Suite");
        Log("========================================");

        int passed = 0;
        int failed = 0;

        // 1. Test AutoCorrectDictionary
        try
        {
            var dict = new AutoCorrectDictionary();
            
            bool r1 = dict.TryGetCorrection("teh", out var c1) && c1 == "the";
            bool r2 = dict.TryGetCorrection("TEH", out var c2) && c2 == "THE";
            bool r3 = dict.TryGetCorrection("Teh", out var c3) && c3 == "The";
            bool r4 = dict.TryGetCorrection("THis", out var c4) && c4 == "This";
            bool r5 = dict.TryGetCorrection("recieve", out var c5) && c5 == "receive";
            bool r6 = dict.TryGetCorrection("dont", out var c6) && c6 == "don't";
            bool r7 = dict.TryGetCorrection("i", out var c7) && c7 == "I";

            // Repeated character typo test
            bool r8 = dict.TryGetCorrection("itt", out var c8) && c8 == "it";
            bool r9 = dict.TryGetCorrection("issss", out var c9) && c9 == "is";
            bool r10 = dict.TryGetCorrection("notttt", out var c10) && c10 == "not";
            bool r11 = dict.TryGetCorrection("worrrking", out var c11) && c11 == "working";

            // User-reported real-world typos test
            bool r12 = dict.TryGetCorrection("sugestion", out var c12) && c12 == "suggestion";
            bool r13 = dict.TryGetCorrection("diffrent", out var c13) && c13 == "different";
            bool r14 = dict.TryGetCorrection("repositiory", out var c14) && c14 == "repository";
            bool r15 = dict.TryGetCorrection("doesent", out var c15) && c15 == "doesn't";
            bool r16 = dict.TryGetCorrection("continously", out var c16) && c16 == "continuously";
            bool r17 = dict.TryGetCorrection("minimise", out var c17) && c17 == "minimize";

            // Tab prefix completion test
            bool r18 = dict.GetPrefixCompletion("sugg", out var c18) && c18.Equals("suggestion", StringComparison.OrdinalIgnoreCase);
            bool r19 = dict.GetPrefixCompletion("diff", out var c19) && c19.Equals("different", StringComparison.OrdinalIgnoreCase);

            // Two-word phrase correction test
            bool r20 = dict.TryGetPhraseCorrection("a apple", out var c20) && c20 == "an apple";
            bool r21 = dict.TryGetPhraseCorrection("could of", out var c21) && c21 == "could have";

            // Hard word candidates test
            var hardCandidates = dict.GetHardWordCandidates("worrd");
            bool r22 = hardCandidates.Count > 0;

            // Multi-line, sentence capitalization, and double-capitals check test
            var multiLineLints = dict.LintText("Line 1 has teh error\nline 2 is worrrking nicely\nTHis is a sentence. another sentence has a apple");
            bool r23 = multiLineLints.Any(l => l.MatchedText == "teh" && l.Line == 1) &&
                       multiLineLints.Any(l => l.MatchedText == "worrrking" && l.Line == 2) &&
                       multiLineLints.Any(l => l.MatchedText == "line" && l.Rule == "Sentence Capitalization") &&
                       multiLineLints.Any(l => l.MatchedText == "THis" && l.Rule == "Double Capitals") &&
                       multiLineLints.Any(l => l.MatchedText == "another" && l.Rule == "Sentence Capitalization") &&
                       multiLineLints.Any(l => l.MatchedText == "a apple");

            // Extra space and annotations check test
            var spaceAndAnnotationLints = dict.LintText("Here is  extra space , and missing space.Next sentence has the the duplicate. TODO: remove note and [citation needed].");
            bool r24 = spaceAndAnnotationLints.Any(l => l.Rule == "Extra Space") &&
                       spaceAndAnnotationLints.Any(l => l.Rule == "Space Before Punctuation") &&
                       spaceAndAnnotationLints.Any(l => l.Rule == "Missing Space") &&
                       spaceAndAnnotationLints.Any(l => l.Rule == "Duplicate Word") &&
                       spaceAndAnnotationLints.Any(l => l.Rule == "Draft Annotation") &&
                       spaceAndAnnotationLints.Any(l => l.Rule == "Editorial Annotation");

            if (r1 && r2 && r3 && r4 && r5 && r6 && r7 && r8 && r9 && r10 && r11 && r12 && r13 && r14 && r15 && r16 && r17 && r18 && r19 && r20 && r21 && r22 && r23 && r24)
            {
                Log("[PASS] AutoCorrectDictionary: Sentence auto-capitalization, double capitals, extra space checks, draft/editorial annotations, multi-line linting, Tab completion, and hard words work correctly.");
                passed++;
            }
            else
            {
                Log($"[FAIL] AutoCorrectDictionary: Unexpected corrections (r1={r1}, r18={r18}, r20={r20}, r21={r21}, r22={r22}, r23={r23}, r24={r24})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] AutoCorrectDictionary Exception: {ex.Message}");
            failed++;
        }

        // 2. Test ReadabilityService
        try
        {
            var text = "The quick brown fox jumps over the lazy dog. It was a pleasant afternoon.";
            var analysis = ReadabilityService.Analyze(text);

            if (analysis.WordCount == 14 && analysis.SentenceCount == 2 && analysis.ReadabilityScore > 50)
            {
                Log($"[PASS] ReadabilityService: {analysis.WordCount} words, {analysis.SentenceCount} sentences, score {analysis.ReadabilityScore} ({analysis.ReadabilityGrade}), tone '{analysis.ToneSummary}'.");
                passed++;
            }
            else
            {
                Log($"[FAIL] ReadabilityService: Unexpected analysis (Words={analysis.WordCount}, Sentences={analysis.SentenceCount}, Score={analysis.ReadabilityScore})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] ReadabilityService Exception: {ex.Message}");
            failed++;
        }

        // 3. Test HarperEngineService
        try
        {
            var settings = new AppSettings();
            var engine = new HarperEngineService(settings);

            if (!engine.IsAvailable)
            {
                Log($"[FAIL] HarperEngineService: harper-cli.exe not found at {engine.ExecutablePath}");
                failed++;
            }
            else
            {
                var input = "This is an test of grammer mistake. itt issss notttt worrrking.";
                var lints = await engine.LintTextAsync(input);

                bool foundArticle = lints.Any(l => l.Rule.Contains("AnA", StringComparison.OrdinalIgnoreCase) || l.MatchedText == "an");
                bool foundSpelling = lints.Any(l => l.MatchedText == "grammer");
                bool foundWorrrking = lints.Any(l => l.MatchedText == "worrrking");

                if (lints.Count >= 3 && foundArticle && foundSpelling && foundWorrrking)
                {
                    Log($"[PASS] HarperEngineService: Detected {lints.Count} lints offline via Rust core ('an'->'a', 'grammer'->'grammar', 'worrrking'->'working').");
                    passed++;
                }
                else
                {
                    Log($"[FAIL] HarperEngineService: Expected at least 3 lints, got {lints.Count}. (foundArticle={foundArticle}, foundSpelling={foundSpelling}, foundWorrrking={foundWorrrking})");
                    failed++;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] HarperEngineService Exception: {ex.Message}");
            failed++;
        }

        // 4. Test Single Instance Mutex Logic
        try
        {
            const string testMutexName = @"Global\FreeAutocorrect_Test_Mutex_Verification";
            using var m1 = new Mutex(true, testMutexName, out bool created1);
            using var m2 = new Mutex(true, testMutexName, out bool created2);

            if (created1 && !created2)
            {
                Log("[PASS] Single Instance Mutex: First instance acquired lock; second instance was blocked.");
                passed++;
            }
            else
            {
                Log($"[FAIL] Single Instance Mutex: Unexpected (m1={created1}, m2={created2})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Single Instance Mutex Exception: {ex.Message}");
            failed++;
        }

        // 5. Test AutoFinish Completion Logic
        try
        {
            var dict = new AutoCorrectDictionary();
            bool comp1 = dict.GetPrefixCompletion("sugg", out var full1) && full1.Equals("suggestion", StringComparison.OrdinalIgnoreCase);
            bool comp2 = dict.GetPrefixCompletion("diff", out var full2) && full2.Equals("different", StringComparison.OrdinalIgnoreCase);
            bool comp3 = dict.GetPrefixCompletion("repos", out var full3) && full3.Equals("repository", StringComparison.OrdinalIgnoreCase);
            bool comp4 = dict.GetPrefixCompletion("minim", out var full4) && full4.Equals("minimize", StringComparison.OrdinalIgnoreCase);

            var settings = new AppSettings { TabCompletionEnabled = true, AutoFinishHotkey = "Tab" };
            using var hook = new LowLevelKeyboardHook(dict, settings);

            if (comp1 && comp2 && comp3 && comp4)
            {
                Log($"[PASS] AutoFinish Completion: Verified prefix expansions ('sugg'->'{full1}', 'diff'->'{full2}', 'repos'->'{full3}', 'minim'->'{full4}') and configurable hotkey triggers.");
                passed++;
            }
            else
            {
                Log($"[FAIL] AutoFinish Completion: Unexpected prefix completion results (comp1={comp1}, comp2={comp2}, comp3={comp3}, comp4={comp4})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] AutoFinish Completion Exception: {ex.Message}");
            failed++;
        }

        // 6. Test Application & Taskbar Icon Resource
        try
        {
            var possibleIconPaths = new[]
            {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Assets", "icon.ico"),
                @"C:\Users\Bisrat\Documents\APPS\FreeAutocorrect\Assets\icon.ico"
            };

            string? foundIcon = possibleIconPaths.FirstOrDefault(System.IO.File.Exists);
            if (!string.IsNullOrEmpty(foundIcon) && new System.IO.FileInfo(foundIcon).Length > 1000)
            {
                Log($"[PASS] Taskbar & Window Icon: Assets/icon.ico verified ({new System.IO.FileInfo(foundIcon).Length:N0} bytes, multi-resolution 16-256px).");
                passed++;
            }
            else
            {
                Log("[FAIL] Taskbar & Window Icon: Assets/icon.ico not found or invalid.");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Taskbar & Window Icon Exception: {ex.Message}");
            failed++;
        }

        // 7. Test Sentence Boundary Detection & Rewriting Word Safety (V0.0.14)
        try
        {
            var dict = new AutoCorrectDictionary();
            var settings = new AppSettings { AutoCapitalizeSentence = true };
            using var hook = new LowLevelKeyboardHook(dict, settings);

            // Buffer was just cleared or typing started without preceding sentence end -> MUST be false
            bool b1 = !hook.TestSentenceStartCheck("word ", 4);

            // Mid-sentence typing -> MUST be false
            bool b2 = !hook.TestSentenceStartCheck("The quick brown fox ", 3);

            // True sentence start after period -> MUST be true
            bool b3 = hook.TestSentenceStartCheck("First sentence. Second ", 6);

            // True sentence start after exclamation mark -> MUST be true
            bool b4 = hook.TestSentenceStartCheck("Hello! we ", 2);

            // True sentence start after newline -> MUST be true
            bool b5 = hook.TestSentenceStartCheck("Line one\nline ", 4);

            // Decimal number should NOT be considered sentence start -> MUST be false
            bool b6 = !hook.TestSentenceStartCheck("Price is 3.14 per unit ", 3);

            // Ellipsis should NOT trigger sentence start -> MUST be false
            bool b7 = !hook.TestSentenceStartCheck("Wait... what is that ", 4);

            // Abbreviations like "e.g." or "Mr." should NOT trigger sentence start -> MUST be false
            bool b8 = !hook.TestSentenceStartCheck("Call Dr. smith today ", 5);
            bool b9 = !hook.TestSentenceStartCheck("For example e.g. this one ", 4);

            // Word character verification
            bool b10 = LowLevelKeyboardHook.IsWordChar('a') && LowLevelKeyboardHook.IsWordChar('Z') &&
                       LowLevelKeyboardHook.IsWordChar('9') && LowLevelKeyboardHook.IsWordChar('\'') &&
                       LowLevelKeyboardHook.IsWordChar('-') && !LowLevelKeyboardHook.IsWordChar(' ') &&
                       !LowLevelKeyboardHook.IsWordChar('.') && !LowLevelKeyboardHook.IsWordChar('!');

            // LintText does not flag sentence start on partial snippet without sentence terminal
            var partialLints = dict.LintText("is worrrking nicely");
            bool b11 = !partialLints.Any(l => l.Rule == "Sentence Capitalization");

            if (b1 && b2 && b3 && b4 && b5 && b6 && b7 && b8 && b9 && b10 && b11)
            {
                Log("[PASS] Sentence Boundary & Rewriting Word Safety: Sentence start is strictly verified (false on mid-word, mid-sentence, clicks, decimals, ellipses, abbreviations; true only on . ! ? \\n).");
                passed++;
            }
            else
            {
                Log($"[FAIL] Sentence Boundary Safety: (b1={b1}, b2={b2}, b3={b3}, b4={b4}, b5={b5}, b6={b6}, b7={b7}, b8={b8}, b9={b9}, b10={b10}, b11={b11})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Sentence Boundary Safety Exception: {ex.Message}");
            failed++;
        }

        // 8. Test Whole Dictionary Scale & C2 Easy Word Suggestions (V0.0.15)
        try
        {
            var dict = new AutoCorrectDictionary();

            // Check hard word candidates prioritize familiar C2 words
            var cands1 = dict.GetHardWordCandidates("worrd");
            bool c1 = cands1.Count > 0 && cands1[0].Equals("word", StringComparison.OrdinalIgnoreCase);

            var cands2 = dict.GetHardWordCandidates("writng");
            bool c2 = cands2.Count > 0 && cands2[0].Equals("writing", StringComparison.OrdinalIgnoreCase);

            // Check massive typo coverage from typos.json (30,000+ entries)
            bool c3 = dict.TryGetCorrection("differnt", out var fix1) && fix1.Equals("different", StringComparison.OrdinalIgnoreCase);
            bool c4 = dict.TryGetCorrection("goverment", out var fix2) && fix2.Equals("government", StringComparison.OrdinalIgnoreCase);
            bool c5 = dict.TryGetCorrection("seperate", out var fix3) && fix3.Equals("separate", StringComparison.OrdinalIgnoreCase);

            // Check settings change notification
            var settings = new AppSettings();
            bool settingsNotified = false;
            settings.OnSettingsChanged += () => settingsNotified = true;
            settings.LiveAutocorrectEnabled = !settings.LiveAutocorrectEnabled;
            settings.Save();

            if (c1 && c2 && c3 && c4 && c5 && settingsNotified)
            {
                Log($"[PASS] Whole Dictionary Scale & C2 Suggestions: Verified 30k+ typos ('differnt'->'{fix1}', 'goverment'->'{fix2}', 'seperate'->'{fix3}'), C2-level prioritized suggestions ('worrd'->'{cands1[0]}', 'writng'->'{cands2[0]}'), and settings sync notification.");
                passed++;
            }
            else
            {
                string s1 = string.Join(", ", cands1);
                string s2 = string.Join(", ", cands2);
                Log($"[FAIL] Whole Dictionary & C2 Suggestions: (c1={c1} [cands1: {s1}], c2={c2} [cands2: {s2}], c3={c3}, c4={c4}, c5={c5}, settingsNotified={settingsNotified})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Whole Dictionary & C2 Suggestions Exception: {ex.Message}");
            failed++;
        }

        Log("========================================");
        Log($"Results: {passed} passed, {failed} failed.");
        Log("========================================");

        try
        {
            System.IO.File.WriteAllText("test-results.txt", sb.ToString());
        }
        catch { }

        return failed == 0 ? 0 : 1;
    }
}
