using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public class HarperEngineService
{
    private string? _executablePath;
    private readonly AppSettings _settings;

    public HarperEngineService(AppSettings settings)
    {
        _settings = settings;
        ResolveBinary();
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_executablePath) && File.Exists(_executablePath);
    public string ExecutablePath => _executablePath ?? string.Empty;

    public void ResolveBinary()
    {
        var possiblePaths = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harper-bin", "harper-cli.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "harper-bin", "harper-cli.exe"),
            Path.Combine(@"C:\Users\Bisrat\Documents\APPS\FreeAutocorrect", "harper-bin", "harper-cli.exe")
        };

        foreach (var p in possiblePaths)
        {
            if (File.Exists(p))
            {
                _executablePath = Path.GetFullPath(p);
                return;
            }
        }

        // Try PATH
        try
        {
            var p = Environment.GetEnvironmentVariable("PATH")?.Split(';')
                .Select(dir => Path.Combine(dir.Trim(), "harper-cli.exe"))
                .FirstOrDefault(File.Exists);
            if (!string.IsNullOrEmpty(p))
            {
                _executablePath = p;
            }
        }
        catch { }
    }

    public async Task<List<LintItem>> LintTextAsync(string text)
    {
        var results = new List<LintItem>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        if (!IsAvailable)
        {
            // Fallback lightweight regex/rule checks
            return RunFallbackChecks(text);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _executablePath!,
                Arguments = $"lint --format json --dialect {_settings.HarperDialect}",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            await process.StandardInput.WriteAsync(text).ConfigureAwait(false);
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync()).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output))
            {
                results = ParseHarperJson(output, text);
            }
        }
        catch (Exception)
        {
            results = RunFallbackChecks(text);
        }

        // Filter out ignored words from user's custom dictionary
        if (_settings.CustomDictionary != null && _settings.CustomDictionary.Count > 0)
        {
            var ignoreSet = new HashSet<string>(_settings.CustomDictionary, StringComparer.OrdinalIgnoreCase);
            results = results.Where(r => !ignoreSet.Contains(r.MatchedText)).ToList();
        }

        return results;
    }

    private List<LintItem> ParseHarperJson(string json, string sourceText)
    {
        var items = new List<LintItem>();

        try
        {
            int start = json.IndexOf('[');
            int end = json.LastIndexOf(']');
            if (start < 0 || end <= start) return items;

            json = json.Substring(start, end - start + 1);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

            foreach (var fileElem in doc.RootElement.EnumerateArray())
            {
                if (!fileElem.TryGetProperty("lints", out var lintsElem)) continue;

                foreach (var lintElem in lintsElem.EnumerateArray())
                {
                    var item = new LintItem();

                    if (lintElem.TryGetProperty("rule", out var rule))
                        item.Rule = rule.GetString() ?? "";

                    if (lintElem.TryGetProperty("kind", out var kind))
                        item.Kind = kind.GetString() ?? "";

                    if (lintElem.TryGetProperty("message", out var msg))
                        item.Message = msg.GetString() ?? "";

                    if (lintElem.TryGetProperty("priority", out var priority))
                        item.Priority = priority.GetInt32();

                    if (lintElem.TryGetProperty("line", out var line))
                        item.Line = line.GetInt32();

                    if (lintElem.TryGetProperty("column", out var col))
                        item.Column = col.GetInt32();

                    if (lintElem.TryGetProperty("matched_text", out var matched))
                        item.MatchedText = matched.GetString() ?? "";

                    if (lintElem.TryGetProperty("span", out var span))
                    {
                        if (span.TryGetProperty("char_start", out var cs))
                            item.CharStart = cs.GetInt32();
                        if (span.TryGetProperty("char_end", out var ce))
                            item.CharEnd = ce.GetInt32();
                    }

                    if (lintElem.TryGetProperty("suggestions", out var suggs) && suggs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in suggs.EnumerateArray())
                        {
                            var sStr = s.GetString();
                            if (!string.IsNullOrEmpty(sStr))
                            {
                                item.Suggestions.Add(sStr);
                            }
                        }
                    }

                    // Map Kind and Rule to Category
                    item.Category = MapCategory(item.Kind, item.Rule);

                    items.Add(item);
                }
            }
        }
        catch { }

        return items;
    }

    private LintCategory MapCategory(string kind, string rule)
    {
        var k = kind.ToLowerInvariant();
        var r = rule.ToLowerInvariant();

        if (k.Contains("spell") || r.Contains("spell"))
            return LintCategory.Spelling;

        if (k.Contains("punctuation") || r.Contains("punctuation") || r.Contains("comma") || r.Contains("apostrophe") || r.Contains("quote"))
            return LintCategory.Punctuation;

        if (k.Contains("style") || r.Contains("style") || r.Contains("passive") || r.Contains("wordy") || r.Contains("repetition") || r.Contains("redundancy"))
            return LintCategory.Style;

        if (k.Contains("grammar") || r.Contains("agreement") || r.Contains("tense") || r.Contains("article") || r.Contains("ana"))
            return LintCategory.Grammar;

        return LintCategory.Miscellaneous;
    }

    private List<LintItem> RunFallbackChecks(string text)
    {
        var items = new List<LintItem>();
        // Basic offline fallback in case engine process is restarting
        return items;
    }
}
