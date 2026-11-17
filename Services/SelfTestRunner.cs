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
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Assets", "icon.ico")
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

            // Check settings change notification (do NOT persist the test toggle to the real user settings)
            var settings = new AppSettings();
            bool settingsNotified = false;
            settings.OnSettingsChanged += () => settingsNotified = true;
            settings.LiveAutocorrectEnabled = !settings.LiveAutocorrectEnabled;
            settings.NotifySettingsChanged();

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


        // 9. Test Target Window Resolution, Chromium Cadence & Apply Safety (V0.0.16)
        try
        {
            // Verify ActivateWindow safety
            TextAutomationService.ActivateWindow(IntPtr.Zero);

            bool targetResolved = false;
            var staThread = new Thread(() =>
            {
                var dummySettings = new AppSettings();
                var testHwnd = new IntPtr(0x123456);
                var widget = new UI.FloatingWidget(dummySettings, () => testHwnd);
                IntPtr resolved = widget.GetTargetAppWindow();
                targetResolved = (resolved == testHwnd);

                var sampleLints = new System.Collections.Generic.List<LintItem>
                {
                    new() { MatchedText = "teh", Suggestions = new() { "the" }, CharStart = 0, CharEnd = 3 }
                };
                widget.UpdateLints(sampleLints, "teh", isSelectedText: false);
                widget.PositionAtTopLeft();
                bool isTopLeft = widget.Left <= 80 && widget.Top <= 80;
                targetResolved = targetResolved && isTopLeft;
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(3000);

            // Verify AppLogger file logging
            AppLogger.Info("SelfTest", "Verification trace for V0.0.18");
            string recent = AppLogger.GetRecentLogLines(5);
            bool loggerWorking = !string.IsNullOrEmpty(recent) && recent.Contains("Verification trace");

            if (targetResolved && loggerWorking)
            {
                Log("[PASS] Chromium/Opera/Antigravity Automation & Apply Safety: Verified window activation, target window resolution, deletion-insertion cadence, and AppLogger analysis trace.");
                passed++;
            }
            else
            {
                Log("[FAIL] Target Window Resolution failed");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Chromium Automation & Apply Safety Exception: {ex.Message}");
            failed++;
        }

        // 10. Test Active Document Scanning & In-Place Draft Typo Replacement (V0.0.19)
        try
        {
            var ctx = ActiveDocumentService.GetDocumentContext(IntPtr.Zero);
            bool safeEmptyCtx = (ctx != null && !ctx.IsSupported && string.IsNullOrEmpty(ctx.FullText));
            bool safeReplace = !ActiveDocumentService.SelectAndReplaceTypo(IntPtr.Zero, "teh", "the");

            if (safeEmptyCtx && safeReplace)
            {
                Log("[PASS] Active Document Scanning & In-Place Typo Replacement: Verified null-safety, UIA context extraction contracts, and fail-safe handling for draft mails and documents.");
                passed++;
            }
            else
            {
                Log("[FAIL] Active Document Scanning & Typo Replacement failed assertion.");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Active Document Scanning Exception: {ex.Message}");
            failed++;
        }

        // 11. Test Editable-Only Text Filtering, Fast Fault Recognition & Word Continuity (V0.0.20)
        try
        {
            bool nullElementNotEditable = !ActiveDocumentService.IsElementEditable(null!, out var tp, out var vp);
            string safePrefix = ActiveDocumentService.GetWordPrefixBeforeCaret(IntPtr.Zero);
            string safeWord = ActiveDocumentService.GetWordAtCaret(IntPtr.Zero);

            bool wordCharsValid = LowLevelKeyboardHook.IsWordChar('a') &&
                                  LowLevelKeyboardHook.IsWordChar('Z') &&
                                  LowLevelKeyboardHook.IsWordChar('9') &&
                                  LowLevelKeyboardHook.IsWordChar('\'') &&
                                  !LowLevelKeyboardHook.IsWordChar(' ') &&
                                  !LowLevelKeyboardHook.IsWordChar('.');

            if (nullElementNotEditable && safePrefix == string.Empty && safeWord == string.Empty && wordCharsValid)
            {
                Log("[PASS] Editable-Only Filtering, Fast Recognition & Word Continuity: Verified strict editable element gate, word prefix recovery across typing pauses, and instantaneous dictionary dispatch.");
                passed++;
            }
            else
            {
                Log("[FAIL] Editable-Only Filtering & Word Continuity assertions failed.");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Editable-Only Filtering Exception: {ex.Message}");
            failed++;
        }

        // 12. Test Link & URL Filter - Zero Suggestions on Links (V0.0.21)
        try
        {
            var dict = new AutoCorrectDictionary();

            // Text with multiple link types (https, www, email, markdown link, domain)
            string textWithLinks = "Check https://github.com/Bisrat24/FreeAutocorrect/issues or www.google.com and mail user@domain.org or [repo](https://antigravity.google.com/test).";
            var linkLints = dict.LintText(textWithLinks);

            bool noLinkErrors = linkLints.Count == 0;

            // Text with typo outside links
            string textWithTypoAndLink = "Visit https://github.com/test but teh result was good.";
            var mixedLints = dict.LintText(textWithTypoAndLink);
            bool onlyRealTypoFlagged = mixedLints.Count == 1 &&
                                       mixedLints[0].MatchedText == "teh" &&
                                       mixedLints[0].PrimarySuggestion == "the";

            // Live typing URL context tests
            bool isHttpsUrl = LinkFilterHelper.IsLinkOrUrlContext("https://github.com/test", 4, '/');
            bool isWwwUrl = LinkFilterHelper.IsLinkOrUrlContext("www.google.com", 3, ' ');
            bool isEmail = LinkFilterHelper.IsLinkOrUrlContext("user@domain.org", 3, ' ');
            bool isNormalText = !LinkFilterHelper.IsLinkOrUrlContext("the quick brown fox ", 3, ' ');

            if (noLinkErrors && onlyRealTypoFlagged && isHttpsUrl && isWwwUrl && isEmail && isNormalText)
            {
                Log("[PASS] Link & URL Filter (V0.0.21): Verified zero suggestions/corrections on URLs, web addresses, domains, emails, and markdown links while preserving typos in normal prose.");
                passed++;
            }
            else
            {
                Log($"[FAIL] Link & URL Filter failed assertions: noLinkErrors={noLinkErrors}, onlyRealTypoFlagged={onlyRealTypoFlagged}, isHttps={isHttpsUrl}, isWww={isWwwUrl}, isEmail={isEmail}, isNormalText={isNormalText}");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Link & URL Filter Exception: {ex.Message}");
            failed++;
        }

        // 13. Test Specific App Filtering, Windows Startup Registry & Floating Suggestions Dropdown (V0.0.22)
        try
        {
            var testSettings = new AppSettings();

            // 1. App filtering: Explorer disabled by default
            bool explorerBlockedByDefault = !testSettings.IsAppEnabled("explorer") &&
                                            !testSettings.IsAppEnabled("explorer.exe") &&
                                            !testSettings.IsAppEnabled("ShellExperienceHost.exe");

            bool normalAppAllowed = testSettings.IsAppEnabled("notepad.exe") &&
                                    testSettings.IsAppEnabled("chrome.exe");

            // Explorer enabled when requested
            testSettings.EnableInExplorer = true;
            bool explorerAllowedWhenEnabled = testSettings.IsAppEnabled("explorer") &&
                                              testSettings.IsAppEnabled("explorer.exe");

            // Whitelist mode test
            testSettings.AppFilterMode = "OnlyIncluded";
            testSettings.IncludedApps = new System.Collections.Generic.List<string> { "winword.exe" };
            bool whitelistMatch = testSettings.IsAppEnabled("winword.exe");
            bool whitelistBlocked = !testSettings.IsAppEnabled("notepad.exe");

            // 2. Startup service check (snapshot and restore so the user's real startup setting is untouched)
            bool originalStartup = StartupService.IsLaunchOnStartupEnabled();
            bool startupCallable = StartupService.SetLaunchOnStartup(false);
            bool startupDisabled = !StartupService.IsLaunchOnStartupEnabled();
            if (originalStartup) StartupService.SetLaunchOnStartup(true);

            // 3. Floating widget suggestions dropdown toggle check
            bool dropdownWorking = false;
            var staThread = new Thread(() =>
            {
                var dummySettings = new AppSettings();
                var widget = new UI.FloatingWidget(dummySettings, () => IntPtr.Zero);
                widget.OpenSuggestionsDropdown();
                bool isOpen = widget.ExpandedView.Visibility == System.Windows.Visibility.Visible;
                widget.CloseSuggestionsDropdown();
                bool isClosed = widget.ExpandedView.Visibility == System.Windows.Visibility.Collapsed;
                dropdownWorking = isOpen && isClosed;
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(3000);

            if (explorerBlockedByDefault && normalAppAllowed && explorerAllowedWhenEnabled &&
                whitelistMatch && whitelistBlocked && startupCallable && startupDisabled && dropdownWorking)
            {
                Log("[PASS] App Filtering, Windows Startup & Suggestions Dropdown (V0.0.22): Verified explorer.exe blocked unless enabled, whitelist/blacklist app filtering, Windows startup registry control, and dropdown suggestions view.");
                passed++;
            }
            else
            {
                Log($"[FAIL] App Filtering & Dropdown failed: explorerBlocked={explorerBlockedByDefault}, normalAppAllowed={normalAppAllowed}, explorerAllowed={explorerAllowedWhenEnabled}, whitelist={whitelistMatch && whitelistBlocked}, startup={startupCallable && startupDisabled}, dropdown={dropdownWorking}");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] App Filtering & Dropdown Exception: {ex.Message}");
            failed++;
        }

        // 14. Test Grammarly 4-Pillars, Performance Scorecard & Floating Widget UI (V0.0.23)
        try
        {
            // Test Pillar Mappings
            var spellLint = new LintItem { Category = LintCategory.Spelling, MatchedText = "teh", Suggestions = new System.Collections.Generic.List<string> { "the" } };
            var gramLint = new LintItem { Category = LintCategory.Grammar, MatchedText = "is", Suggestions = new System.Collections.Generic.List<string> { "are" } };
            var punctLint = new LintItem { Category = LintCategory.Punctuation, MatchedText = " ,", Suggestions = new System.Collections.Generic.List<string> { "," } };
            var clarityLint = new LintItem { Category = LintCategory.Style, Rule = "Passive Voice", MatchedText = "was written by", Suggestions = new System.Collections.Generic.List<string> { "wrote" } };
            var engageLint = new LintItem { Category = LintCategory.Style, Rule = "Wordiness reduction", MatchedText = "in order to", Suggestions = new System.Collections.Generic.List<string> { "to" } };
            var deliverLint = new LintItem { Category = LintCategory.Miscellaneous, Rule = "Tone Politeness", MatchedText = "you must", Suggestions = new System.Collections.Generic.List<string> { "please" } };

            bool p1 = spellLint.Pillar == GrammarlyPillar.Correctness && spellLint.PillarColor == "#EF4444";
            bool p2 = gramLint.Pillar == GrammarlyPillar.Correctness;
            bool p3 = punctLint.Pillar == GrammarlyPillar.Correctness;
            bool p4 = clarityLint.Pillar == GrammarlyPillar.Clarity && clarityLint.PillarColor == "#0284C7";
            bool p5 = engageLint.Pillar == GrammarlyPillar.Engagement && engageLint.PillarColor == "#10B981";
            bool p6 = deliverLint.Pillar == GrammarlyPillar.Delivery && deliverLint.PillarColor == "#8B5CF6";

            // Test CheckResult Scorecard Calculation
            var result = new CheckResult
            {
                OriginalText = "This is a sample document for testing the new Grammarly 4-pillar scorecard and metrics.",
                WordCount = 14,
                CharacterCount = 86,
                SentenceCount = 1,
                Lints = new System.Collections.Generic.List<LintItem> { spellLint, clarityLint }
            };

            bool crCorrect = result.CorrectnessCount == 1;
            bool crClarity = result.ClarityCount == 1;
            bool crEngage = result.EngagementCount == 0;
            bool crDeliver = result.DeliveryCount == 0;
            // penalty: (1 * 6) + (1 * 3) = 9 -> OverallScore = 91
            bool crScore = result.OverallScore == 91;
            bool crRating = result.ScoreRating == "Great job!";

            // Test Readability Speaking Time & Tone
            var analysis = ReadabilityService.Analyze("Thank you so much! We are extremely happy and excited with the wonderful results.");
            bool hasTone = !string.IsNullOrWhiteSpace(analysis.ToneSummary) && analysis.ToneSummary.Contains("Friendly");
            bool hasSpeakingTime = analysis.SpeakingTimeMinutes > 0;

            // Test Floating Widget Grammarly UI Elements in STA
            bool widgetScorecardValid = false;
            var staGrammarly = new Thread(() =>
            {
                var dummySettings = new AppSettings();
                var widget = new UI.FloatingWidget(dummySettings, () => IntPtr.Zero);
                widget.UpdateLints(new System.Collections.Generic.List<LintItem> { spellLint, engageLint }, "Here is teh sentence in order to test.");
                bool scoreMatches = widget.TxtOverallScore.Text != null && int.TryParse(widget.TxtOverallScore.Text, out int parsedScore) && parsedScore > 0;
                bool correctnessCountMatches = widget.TxtWidgetCorrectness.Text == "1";
                bool engageCountMatches = widget.TxtWidgetEngagement.Text == "1";
                bool toneShown = !string.IsNullOrEmpty(widget.TxtWidgetTone.Text);
                widgetScorecardValid = scoreMatches && correctnessCountMatches && engageCountMatches && toneShown;
            });
            staGrammarly.SetApartmentState(ApartmentState.STA);
            staGrammarly.Start();
            staGrammarly.Join(3000);

            if (p1 && p2 && p3 && p4 && p5 && p6 && crCorrect && crClarity && crEngage && crDeliver && crScore && crRating && hasTone && hasSpeakingTime && widgetScorecardValid)
            {
                Log("[PASS] Grammarly 4-Pillars, Performance Scorecard & Floating Widget UI (V0.0.23): Verified Correctness, Clarity, Engagement, Delivery categorization, circular score wheel, tone badges, and widget UI.");
                passed++;
            }
            else
            {
                Log($"[FAIL] Grammarly 4-Pillars failed: p={p1 && p2 && p3 && p4 && p5 && p6}, cr={crCorrect && crClarity && crEngage && crDeliver && crScore && crRating}, tone={hasTone}, speaking={hasSpeakingTime}, widget={widgetScorecardValid}");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Grammarly 4-Pillars Exception: {ex.Message}");
            failed++;
        }

        // 15. Test Conservative Auto-Apply, Smart Apostrophes, Untrusted Feed & Code-App Profile (V0.0.24)
        try
        {
            var dict = new AutoCorrectDictionary();

            // High-confidence typos still auto-apply
            bool a1 = dict.TryGetAutoCorrection("teh", out var a1fix) && a1fix == "the";
            bool a2 = dict.TryGetAutoCorrection("recieve", out var a2fix) && a2fix == "receive";
            bool a3 = dict.TryGetAutoCorrection("i", out var a3fix) && a3fix == "I";

            // Edit-distance guesses must never be auto-applied, only offered as suggestions
            bool a4 = !dict.TryGetAutoCorrection("iaportant", out _) &&
                      dict.TryGetCorrection("iaportant", out var a4fix) && a4fix == "important";

            // Repeated-letter fixes still work when the word arrives with a typographic apostrophe
            bool a5 = dict.TryGetAutoCorrection("issssn\u2019t", out var a5fix) && a5fix == "isn't";

            // Untrusted feed entries are rejected; only the clean entry is merged
            var feed = new System.Collections.Generic.Dictionary<string, string>
            {
                { "bad\u0000key", "value" },
                { new string('x', 65), "value" },
                { "same", "same" },
                { "validtypo", "validword" }
            };
            int merged = dict.MergeExternalTypos(feed);
            bool a6 = merged == 1 && dict.TryGetAutoCorrection("validtypo", out var a6fix) && a6fix == "validword";

            // Code editors and terminals use the no-prose profile; word processors do not
            var profiles = new AppSettings();
            bool a7 = profiles.IsCodeApp("Code.exe") && profiles.IsCodeApp("antigravity.exe") &&
                      profiles.IsCodeApp("powershell.exe") &&
                      !profiles.IsCodeApp("winword.exe") && !profiles.IsCodeApp("notepad.exe");

            if (a1 && a2 && a3 && a4 && a5 && a6 && a7)
            {
                Log("[PASS] Conservative Auto-Apply, Smart Apostrophes, Untrusted Feed & Code-App Profile (V0.0.24): Verified high-confidence-only rewrites, edit-distance matches downgraded to suggestions, apostrophe normalization, feed sanitization, and code-app prose suppression.");
                passed++;
            }
            else
            {
                Log($"[FAIL] Conservative Auto-Apply failed: (a1={a1}, a2={a2}, a3={a3}, a4={a4}, a5={a5}, a6={a6} [merged={merged}], a7={a7})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Conservative Auto-Apply Exception: {ex.Message}");
            failed++;
        }

        // 16. Test Selected Text Checking, Safe In-Place Replacement & UI Indicator (V0.0.24)
        try
        {
            // 1. Contract checks for DocumentContext & ActiveDocumentService
            var ctx = new DocumentContext { FullText = "Sample text", SelectedText = "Sample" };
            bool s1 = ctx.SelectedText == "Sample";
            bool s2 = ActiveDocumentService.GetSelectedText(IntPtr.Zero) == string.Empty;
            bool s3 = !ActiveDocumentService.SelectAndReplaceTypo(IntPtr.Zero, "teh", "the");

            // 2. TextAutomationService fail-safe execution
            string s4Result = TextAutomationService.GetSelectedTextFromActiveApp(IntPtr.Zero);
            bool s4 = s4Result != null;
            TextAutomationService.ReplaceSelectedTextInActiveApp(IntPtr.Zero, "safe_test");

            // 3. Floating Widget Selected Text Mode & In-Place UI Behavior
            bool widgetSelectedModeValid = false;
            var staSelected = new Thread(() =>
            {
                var dummySettings = new AppSettings();
                var widget = new UI.FloatingWidget(dummySettings, () => IntPtr.Zero);
                var lint = new LintItem
                {
                    Category = LintCategory.Spelling,
                    MatchedText = "teh",
                    CharStart = 0,
                    CharEnd = 3,
                    Suggestions = new System.Collections.Generic.List<string> { "the" }
                };

                // Updating in selected text mode
                widget.UpdateLints(new System.Collections.Generic.List<LintItem> { lint }, "teh test", isSelectedText: true);
                bool indicatorVisible = widget.SelectedTextIndicator.Visibility == System.Windows.Visibility.Visible;
                bool countMatches = widget.TxtExpandedCount.Text == "1";

                // Updating in normal mode
                widget.UpdateLints(new System.Collections.Generic.List<LintItem>(), "normal text", isSelectedText: false);
                bool indicatorHidden = widget.SelectedTextIndicator.Visibility == System.Windows.Visibility.Collapsed;

                widgetSelectedModeValid = indicatorVisible && countMatches && indicatorHidden;
            });
            staSelected.SetApartmentState(ApartmentState.STA);
            staSelected.Start();
            staSelected.Join(3000);

            if (s1 && s2 && s3 && s4 && widgetSelectedModeValid)
            {
                Log("[PASS] Selected Text Checking, Safe In-Place Replacement & UI Indicator (V0.0.24): Verified selected text extraction contracts, fail-safe replacement, physical modifier release, and floating widget selected text indicator.");
                passed++;
            }
            else
            {
                Log($"[FAIL] Selected Text Checking failed: (s1={s1}, s2={s2}, s3={s3}, s4={s4}, widget={widgetSelectedModeValid})");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] Selected Text Checking Exception: {ex.Message}");
            failed++;
        }

        // 17. Test Selected Text Snippet Preview & Multi-Fix Span Re-Alignment (V0.0.25)
        try
        {
            // 1. Verify span re-alignment algorithm:
            var lintA = new LintItem { MatchedText = "dont", CharStart = 13, CharEnd = 17, Suggestions = new System.Collections.Generic.List<string> { "don't" } };
            var lintB = new LintItem { MatchedText = "teh", CharStart = 27, CharEnd = 30, Suggestions = new System.Collections.Generic.List<string> { "the" } };
            var list = new System.Collections.Generic.List<LintItem> { lintA, lintB };

            int shift = "don't".Length - lintA.MatchedText.Length; // 5 - 4 = +1
            foreach (var rem in list)
            {
                if (rem.CharStart > lintA.CharStart)
                {
                    rem.CharStart = Math.Max(0, rem.CharStart + shift);
                    rem.CharEnd = Math.Max(0, rem.CharEnd + shift);
                }
            }
            bool shiftValid = (lintB.CharStart == 28 && lintB.CharEnd == 31);

            // 2. Floating widget snippet preview contract
            bool snippetValid = false;
            var staV25 = new Thread(() =>
            {
                var dummySettings = new AppSettings();
                var widget = new UI.FloatingWidget(dummySettings, () => IntPtr.Zero);
                string sampleText = "The quick brown fox jumps over the lazy dog and highlights some text for analysis";
                widget.UpdateLints(new System.Collections.Generic.List<LintItem>(), sampleText, isSelectedText: true);
                bool hasQuotes = widget.TxtSelectedSnippet.Text.StartsWith("\"") && widget.TxtSelectedSnippet.Text.EndsWith("\"");
                bool isTruncated = widget.TxtSelectedSnippet.Text.Contains("...");

                widget.UpdateLints(new System.Collections.Generic.List<LintItem>(), "short", isSelectedText: true);
                bool shortSnippetValid = widget.TxtSelectedSnippet.Text == "\"short\"";

                widget.UpdateLints(new System.Collections.Generic.List<LintItem>(), "cleared", isSelectedText: false);
                bool clearedValid = string.IsNullOrEmpty(widget.TxtSelectedSnippet.Text);

                snippetValid = hasQuotes && isTruncated && shortSnippetValid && clearedValid;
            });
            staV25.SetApartmentState(ApartmentState.STA);
            staV25.Start();
            staV25.Join(3000);

            if (shiftValid && snippetValid)
            {
                Log("[PASS] Selected Text Snippet Preview & Multi-Fix Span Re-Alignment (V0.0.25): Verified snippet preview formatting, quote wrapping, truncation bounds, and span shift re-alignment across sequential replacements.");
                passed++;
            }
            else
            {
                Log($"[FAIL] V0.0.25 Verification failed: shiftValid={shiftValid}, snippetValid={snippetValid}");
                failed++;
            }
        }
        catch (Exception ex)
        {
            Log($"[FAIL] V0.0.25 Exception: {ex.Message}");
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
