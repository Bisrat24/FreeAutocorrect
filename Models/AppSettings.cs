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
    public string HarperDialect { get; set; } = "us";
    public string GlobalHotkey { get; set; } = "Ctrl+Shift+G";
    public int TotalWordsCorrected { get; set; } = 0;
    public int TotalGrammarFixes { get; set; } = 0;

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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null)
                {
                    return loaded;
                }
            }
        }
        catch { }

        return new AppSettings();
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
    }
}
