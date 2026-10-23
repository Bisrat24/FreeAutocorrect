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

            if (r1 && r2 && r3 && r4 && r5 && r6 && r7 && r8 && r9 && r10 && r11)
            {
                Log("[PASS] AutoCorrectDictionary: Case-preservation, typos, double-caps & repeated characters ('itt'->'it', 'worrrking'->'working') work correctly.");
                passed++;
            }
            else
            {
                Log($"[FAIL] AutoCorrectDictionary: Unexpected corrections (c1={c1}, c5={c5}, c8={c8}, c9={c9}, c10={c10}, c11={c11})");
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
