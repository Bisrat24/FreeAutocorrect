using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public class UpdateResult
{
    public bool Success { get; set; }
    public int WordsAdded { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class DictionaryUpdateService : IDisposable
{
    private static readonly string[] Sources =
    {
        "https://raw.githubusercontent.com/Bisrat24/FreeAutocorrect/main/harper-bin/typos.json",
        "https://raw.githubusercontent.com/derekchu98/Common-Misspellings/master/misspellings.json"
    };

    private readonly AppSettings _settings;
    private readonly AutoCorrectDictionary _dictionary;
    private readonly System.Threading.Timer? _dailyTimer;
    private readonly HttpClient _httpClient;

    public event Action<UpdateResult>? OnDictionaryUpdated;

    public DictionaryUpdateService(AppSettings settings, AutoCorrectDictionary dictionary)
    {
        _settings = settings;
        _dictionary = dictionary;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Check if daily update is due on startup (runs 10 seconds after start so app boots instantly)
        Task.Delay(10000).ContinueWith(_ =>
        {
            if (_settings.DailyDictionaryUpdateEnabled)
            {
                CheckIfDailyUpdateDueAsync().ConfigureAwait(false);
            }
        });

        // Recurring check every 6 hours
        _dailyTimer = new System.Threading.Timer(async _ =>
        {
            if (_settings.DailyDictionaryUpdateEnabled)
            {
                await CheckIfDailyUpdateDueAsync();
            }
        }, null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    private async Task CheckIfDailyUpdateDueAsync()
    {
        if (_settings.LastDictionaryUpdateDate == null ||
            (DateTime.Now - _settings.LastDictionaryUpdateDate.Value) >= TimeSpan.FromDays(1))
        {
            await CheckForUpdatesAsync();
        }
    }

    public async Task<UpdateResult> CheckForUpdatesAsync()
    {
        var result = new UpdateResult();
        try
        {
            foreach (var url in Sources)
            {
                try
                {
                    using var response = await _httpClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{"))
                        {
                            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                            if (parsed != null && parsed.Count > 0)
                            {
                                int added = _dictionary.MergeExternalTypos(parsed);

                                // Save locally to AppData for persistent offline availability
                                string appDataDir = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "FreeAutocorrect"
                                );
                                if (!Directory.Exists(appDataDir)) Directory.CreateDirectory(appDataDir);
                                string localCache = Path.Combine(appDataDir, "custom_typos.json");
                                await File.WriteAllTextAsync(localCache, json);

                                _settings.LastDictionaryUpdateDate = DateTime.Now;
                                _settings.Save();

                                result.Success = true;
                                result.WordsAdded = added;
                                result.Message = $"Successfully updated dictionary (+{added} entries).";
                                OnDictionaryUpdated?.Invoke(result);
                                return result;
                            }
                        }
                    }
                }
                catch { }
            }

            result.Success = false;
            result.Message = "No online dictionary updates found or offline. Using current rich offline dictionary.";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Update check notice: {ex.Message}";
            return result;
        }
    }

    public void Dispose()
    {
        _dailyTimer?.Dispose();
        _httpClient.Dispose();
    }
}
