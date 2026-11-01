using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FreeAutocorrect.Models;

public class AppSettings
{
    public bool LiveAutocorrectEnabled { get; set; } = true;
    public bool AutoCapitalizeSentence { get; set; } = true;
    public bool FixDoubleCapitals { get; set; } = true;
    public bool PlaySoundOnCorrection { get; set; } = false;
    public bool ShowNotificationOnCorrection { get; set; } = false;
    public bool ShowFloatingWidget { get; set; } = true;
    public bool LaunchOnStartup { get; set; } = false;
    public bool EnableInExplorer { get; set; } = false;
    public string AppFilterMode { get; set; } = "AllExceptExcluded"; // "AllExceptExcluded" or "OnlyIncluded"
    public List<string> ExcludedApps { get; set; } = new() { "explorer.exe", "shellexperiencehost.exe", "searchhost.exe" };
    public List<string> IncludedApps { get; set; } = new();
    public bool TabCompletionEnabled { get; set; } = true;
    public string AutoFinishHotkey { get; set; } = "Tab+Right";
    public bool DailyDictionaryUpdateEnabled { get; set; } = true;
    public DateTime? LastDictionaryUpdateDate { get; set; } = null;
    public string HarperDialect { get; set; } = "us";
    public string GlobalHotkey { get; set; } = "Ctrl+Shift+G";
    public int TotalWordsCorrected { get; set; } = 0;
    public int TotalWordsCompleted { get; set; } = 0;
    public int TotalGrammarFixes { get; set; } = 0;

    public bool IsAppEnabled(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return true;
        string cleanName = processName.Trim().ToLowerInvariant();
        if (cleanName.EndsWith(".exe")) cleanName = cleanName.Substring(0, cleanName.Length - 4);

        // Windows Explorer (explorer.exe, shellexperiencehost, searchhost)
        if (cleanName == "explorer" || cleanName == "shellexperiencehost" || cleanName == "searchhost")
        {
            return EnableInExplorer;
        }

        if (string.Equals(AppFilterMode, "OnlyIncluded", StringComparison.OrdinalIgnoreCase))
        {
            // Whitelist mode: only apps explicitly in IncludedApps
            if (IncludedApps == null || IncludedApps.Count == 0) return true;
            foreach (var a in IncludedApps)
            {
                if (MatchAppName(a, cleanName)) return true;
            }
            return false;
        }
        else
        {
            // Blacklist mode: all apps except ExcludedApps
            if (ExcludedApps != null)
            {
                foreach (var a in ExcludedApps)
                {
                    if (MatchAppName(a, cleanName)) return false;
                }
            }
            return true;
        }
    }

    private static bool MatchAppName(string pattern, string cleanName)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string p = pattern.Trim().ToLowerInvariant();
        if (p.EndsWith(".exe")) p = p.Substring(0, p.Length - 4);
        return string.Equals(p, cleanName, StringComparison.OrdinalIgnoreCase);
    }

    public string GetAutoFinishShortcutDisplayName()
    {
        return AutoFinishHotkey switch
        {
            "Tab" => "Tab",
            "Ctrl+Tab" => "Ctrl+Tab",
            "Right" => "➔",
            "Shift+Tab" => "Shift+Tab",
            "Alt+Right" => "Alt+➔",
            _ => "Tab + ➔"
        };
    }

    public List<string> CustomDictionary { get; set; } = new();
    public Dictionary<string, string> CustomSnippets { get; set; } = new()
    {
        { "omw", "on my way" },
        { "tyvm", "thank you very much" },
        { "brb", "be right back" },
        { "idk", "I don't know" },
        { "tbh", "to be honest" },
        { "lmk", "let me know" }
    };

    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FreeAutocorrect"
    );

    private static readonly string SettingsFilePath = Path.Combine(SettingsDir, "settings.json");

    [System.Text.Json.Serialization.JsonIgnore]
    public Action? OnSettingsChanged { get; set; }

    public void NotifySettingsChanged()
    {
        OnSettingsChanged?.Invoke();
    }

    public static AppSettings Load()
    {
        AppSettings settings = new();
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    settings = loaded;
                }
            }
        }
        catch { }

        try
        {
            bool regEnabled = FreeAutocorrect.Services.StartupService.IsLaunchOnStartupEnabled();
            if (regEnabled)
            {
                settings.LaunchOnStartup = true;
            }
        }
        catch { }

        return settings;
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsDir))
            {
                Directory.CreateDirectory(SettingsDir);
            }

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }

        try
        {
            FreeAutocorrect.Services.StartupService.SetLaunchOnStartup(LaunchOnStartup);
        }
        catch { }

        try
        {
            OnSettingsChanged?.Invoke();
        }
        catch { }
    }
}
