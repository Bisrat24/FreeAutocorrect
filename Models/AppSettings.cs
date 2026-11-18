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
    public bool PlaySoundOnSuggestion { get; set; } = false;
    public string SoundScheme { get; set; } = "SubtlePop"; // "SubtlePop", "Typewriter", "SoftChime", "SystemDefault"
    public bool ShowNotificationOnCorrection { get; set; } = false;
    public bool ShowFloatingWidget { get; set; } = true;
    public bool LaunchOnStartup { get; set; } = false;
    public bool EnableInExplorer { get; set; } = false;
    public string AppFilterMode { get; set; } = "AllExceptExcluded"; // "AllExceptExcluded" or "OnlyIncluded"
    public List<string> ExcludedApps { get; set; } = new() { "explorer.exe", "shellexperiencehost.exe", "searchhost.exe" };
    public List<string> IncludedApps { get; set; } = new();
    public bool TabCompletionEnabled { get; set; } = true;
    public string AutoFinishHotkey { get; set; } = "Tab+Right";
    /// <summary>When true (default) file types, file names, and directory paths are ignored.</summary>
    public bool IgnoreFileNamesAndPaths { get; set; } = true;
    /// <summary>When true (default) CamelCase, snake_case, SemVer, and code identifiers are ignored.</summary>
    public bool IgnoreCodeAndTechnicalIdentifiers { get; set; } = true;
    /// <summary>When true (default) markdown fenced code blocks and inline code spans are ignored.</summary>
    public bool IgnoreMarkdownCodeBlocks { get; set; } = true;
    /// <summary>When true (default) only high-confidence typos are rewritten automatically; edit-distance matches are shown as suggestions.</summary>
    public bool ConservativeAutocorrect { get; set; } = true;
    /// <summary>Logs corrected words in plain text. Disabled by default so user content never reaches disk.</summary>
    public bool VerboseLogging { get; set; } = false;
    public List<string> CodeApps { get; set; } = new()
    {
        "code", "code - insiders", "cursor", "windsurf", "devenv", "idea64", "pycharm64",
        "webstorm64", "goland64", "clion64", "rider64", "androidstudio", "sublime_text",
        "notepad++", "windowsterminal", "wt", "conhost", "cmd", "powershell", "pwsh",
        "wsl", "mintty", "bash", "zsh", "fish", "nvim", "vim", "emacs", "zed", "fleet",
        "antigravity"
    };
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
        string cleanName = NormalizeProcessName(processName);

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

    /// <summary>Code editors, terminals and IDEs where prose rules (auto-capitalization, snippets, typo rewrites) are unwanted.</summary>
    public bool IsCodeApp(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName) || CodeApps == null) return false;
        string cleanName = NormalizeProcessName(processName);
        foreach (var a in CodeApps)
        {
            if (MatchAppName(a, cleanName)) return true;
        }
        return false;
    }

    private static string NormalizeProcessName(string processName)
    {
        string cleanName = processName.Trim().ToLowerInvariant();
        if (cleanName.EndsWith(".exe")) cleanName = cleanName.Substring(0, cleanName.Length - 4);
        return cleanName;
    }

    private static bool MatchAppName(string pattern, string cleanName)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        string p = pattern.Trim().ToLowerInvariant();
        if (p.EndsWith(".exe")) p = p.Substring(0, p.Length - 4);
        return string.Equals(p, cleanName, StringComparison.OrdinalIgnoreCase);
    }

    public static readonly string[] PresetOfficeApps = new[]
    {
        "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onenote.exe"
    };

    public static readonly string[] PresetBrowserApps = new[]
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe"
    };

    public static readonly string[] PresetMessagingApps = new[]
    {
        "slack.exe", "discord.exe", "teams.exe", "telegram.exe", "whatsapp.exe"
    };

    public static readonly string[] PresetEditorApps = new[]
    {
        "notepad.exe", "obsidian.exe", "onenote.exe", "wordpad.exe"
    };

    public static readonly string[] DefaultExcludedApps = new[]
    {
        "explorer.exe", "shellexperiencehost.exe", "searchhost.exe"
    };

    public void ApplyAppPreset(IEnumerable<string> apps, bool replace = false)
    {
        bool isWhitelist = string.Equals(AppFilterMode, "OnlyIncluded", StringComparison.OrdinalIgnoreCase);
        var targetList = isWhitelist ? IncludedApps : ExcludedApps;
        if (replace)
        {
            targetList.Clear();
        }
        foreach (var app in apps)
        {
            string clean = app.Trim();
            if (!clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                clean += ".exe";
            }
            if (!targetList.Exists(a => a.Equals(clean, StringComparison.OrdinalIgnoreCase)))
            {
                targetList.Add(clean);
            }
        }
        Save();
    }

    public void ResetAppFiltersToDefault()
    {
        AppFilterMode = "AllExceptExcluded";
        EnableInExplorer = false;
        ExcludedApps = new List<string>(DefaultExcludedApps);
        IncludedApps = new List<string>();
        Save();
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

    private static readonly object SaveLock = new();

    [System.Text.Json.Serialization.JsonIgnore]
    private System.Threading.Timer? _saveTimer;

    [System.Text.Json.Serialization.JsonIgnore]
    private readonly object _saveTimerLock = new();

    /// <summary>
    /// Persists after a quiet period. Safe to call from the keyboard hook / hot paths.
    /// </summary>
    public void SaveDebounced()
    {
        lock (_saveTimerLock)
        {
            _saveTimer ??= new System.Threading.Timer(_ => Save(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _saveTimer.Change(1500, System.Threading.Timeout.Infinite);
        }
    }

    public void Save()
    {
        try
        {
            lock (SaveLock)
            {
                if (!Directory.Exists(SettingsDir))
                {
                    Directory.CreateDirectory(SettingsDir);
                }

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = SettingsFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsFilePath, true);
            }
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
