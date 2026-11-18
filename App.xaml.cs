using System;
using System.Windows;
using FreeAutocorrect.Models;
using FreeAutocorrect.Services;
using FreeAutocorrect.UI;

namespace FreeAutocorrect;

public partial class App : System.Windows.Application
{
    private SingleInstanceManager? _singleInstanceManager;
    private TrayIconService? _trayIconService;
    private LowLevelKeyboardHook? _keyboardHook;
    private GlobalHotkeyService? _hotkeyService;
    private DictionaryUpdateService? _dictionaryUpdateService;
    private MainWindow? _mainWindow;
    private HarperEngineService? _harperEngine;
    private AutoCorrectDictionary? _typoDictionary;
    private FloatingWidget? _floatingWidget;

    // Serializes the Harper CLI so only one lint is in flight and the newest text always wins.
    private readonly object _harperLock = new();
    private string? _harperPendingText;
    private bool _harperWorkerRunning;
    private CancellationTokenSource? _harperCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global crash handling
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            LogCrash(ex);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash(args.Exception);
            System.Windows.MessageBox.Show(
                $"An error occurred:\n\n{args.Exception?.Message}\n\nDetails saved to crash.log",
                "FreeAutocorrect Notice",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            args.Handled = true;
        };

        if (Array.Exists(e.Args, a => a.Equals("--test", StringComparison.OrdinalIgnoreCase)))
        {
            int code = System.Threading.Tasks.Task.Run(async () => await SelfTestRunner.RunAllTestsAsync()).GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }

        _singleInstanceManager = new SingleInstanceManager();

        // Single instance check
        if (!_singleInstanceManager.CheckIsFirstInstance())
        {
            // Show friendly notification to user and exit
            _singleInstanceManager.ShowAlreadyRunningMessage();
            Shutdown();
            return;
        }

        // Initialize services
        var settings = AppSettings.Load();
        _harperEngine = new HarperEngineService(settings);
        _typoDictionary = new AutoCorrectDictionary();

        // Add custom words from user settings to dictionary
        foreach (var word in settings.CustomDictionary)
        {
            _typoDictionary.AddCustomWord(word, word);
        }

        _keyboardHook = new LowLevelKeyboardHook(_typoDictionary, settings);
        _keyboardHook.Start();

        _trayIconService = new TrayIconService(settings, _harperEngine);
        _hotkeyService = new GlobalHotkeyService();

        var floatingWidget = new FloatingWidget(settings, () => _keyboardHook?.LastActiveWindow ?? IntPtr.Zero);
        _floatingWidget = floatingWidget;

        _keyboardHook.OnSuggestionFound += (typo, fix) =>
        {
            floatingWidget.ShowLiveSuggestion(typo, fix);
        };

        _keyboardHook.OnSuggestionDismissed += () =>
        {
            floatingWidget.DismissAllBanners();
        };

        _keyboardHook.OnTabCompletionAvailable += (prefix, fullWord) =>
        {
            floatingWidget.ShowTabCompletion(prefix, fullWord, settings.GetAutoFinishShortcutDisplayName());
        };

        _keyboardHook.OnTabCompletionDismissed += () =>
        {
            floatingWidget.HideTabCompletion();
        };

        floatingWidget.OnAutoFinishRequested += () =>
        {
            _keyboardHook.TriggerAutoFinish();
        };

        _keyboardHook.OnHardWordFound += (word, candidates) =>
        {
            floatingWidget.ShowHardWord(word, candidates);
        };

        // Selected text checking when user highlights text in any document or app
        _keyboardHook.OnSelectedTextDetected += (selectedText, targetHwnd) =>
        {
            if (string.IsNullOrWhiteSpace(selectedText) || selectedText.Length < 2) return;

            try
            {
                var dictLints = _typoDictionary!.LintText(selectedText);
                floatingWidget.Dispatcher.BeginInvoke(new Action(() =>
                {
                    floatingWidget.UpdateLints(new List<LintItem>(dictLints), selectedText, isSelectedText: true);
                }));

                RequestHarperLint(selectedText);
            }
            catch { }
        };

        // Multi-line & rolling typing buffer check across previous words and lines
        _keyboardHook.OnTextBatchUpdated += (rollingText, targetHwnd) =>
        {
            if (string.IsNullOrWhiteSpace(rollingText))
            {
                floatingWidget.Dispatcher.Invoke(() =>
                {
                    floatingWidget.UpdateLints(new List<LintItem>(), string.Empty, false);
                    floatingWidget.DismissAllBanners();
                });
                return;
            }

            try
            {
                // 1. Instant dictionary check: recognize typos and fixes immediately (< 2ms)
                var dictLints = _typoDictionary!.LintText(rollingText);

                floatingWidget.Dispatcher.BeginInvoke(new Action(() =>
                {
                    floatingWidget.UpdateLints(new List<LintItem>(dictLints), rollingText, false);
                }));

                // 2. Offline Harper grammar engine: serialized, debounced and cancellable
                RequestHarperLint(rollingText);
            }
            catch { }
        };

        _dictionaryUpdateService = new DictionaryUpdateService(settings, _typoDictionary);

        _mainWindow = new MainWindow(
            settings,
            _harperEngine,
            _typoDictionary,
            _keyboardHook,
            _trayIconService,
            _hotkeyService,
            floatingWidget,
            _dictionaryUpdateService
        );

        // When second instance tries to launch, activate the main window
        _singleInstanceManager.OnActivateRequested += () =>
        {
            _mainWindow?.ShowAndActivate();
        };

        // Show main window on start
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    /// <summary>
    /// Queues the newest rolling text for Harper. Only one lint process runs at a time, results
    /// for stale text are dropped, and a newer request supersedes the pending one.
    /// </summary>
    private void RequestHarperLint(string rollingText)
    {
        if (_harperEngine?.IsAvailable != true || rollingText.Length < 4) return;

        lock (_harperLock)
        {
            _harperPendingText = rollingText;
            if (_harperWorkerRunning) return;
            _harperWorkerRunning = true;
        }

        _ = System.Threading.Tasks.Task.Run(HarperWorkerLoopAsync);
    }

    private async System.Threading.Tasks.Task HarperWorkerLoopAsync()
    {
        while (true)
        {
            string? text;
            lock (_harperLock)
            {
                text = _harperPendingText;
                _harperPendingText = null;
                if (string.IsNullOrEmpty(text))
                {
                    _harperWorkerRunning = false;
                    return;
                }
            }

            // Debounce: let the user finish the current word before spawning the engine.
            try { await System.Threading.Tasks.Task.Delay(350).ConfigureAwait(false); } catch { }

            // If newer text arrived while debouncing, lint that instead.
            lock (_harperLock)
            {
                if (!string.IsNullOrEmpty(_harperPendingText))
                {
                    text = _harperPendingText;
                    _harperPendingText = null;
                }
            }

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            lock (_harperLock)
            {
                _harperCts?.Dispose();
                _harperCts = cts;
            }

            try
            {
                var harperLints = await _harperEngine!.LintTextAsync(text, cts.Token).ConfigureAwait(false);
                if (harperLints.Count > 0)
                {
                    var dictLints = _typoDictionary!.LintText(text);
                    var ignoredSpans = PathAndFileFilter.GetIgnoredSpans(text);
                    var mergedLints = new List<LintItem>(dictLints);

                    foreach (var hl in harperLints)
                    {
                        // Never add Harper suggestions on links, directory paths, file names, or file types
                        int hlLen = Math.Max(1, hl.CharEnd - hl.CharStart);
                        if (PathAndFileFilter.IsSpanIgnored(hl.CharStart, hlLen, ignoredSpans) ||
                            PathAndFileFilter.ShouldIgnoreToken(hl.MatchedText))
                        {
                            continue;
                        }

                        if (!mergedLints.Any(dl => dl.MatchedText.Equals(hl.MatchedText, StringComparison.OrdinalIgnoreCase) ||
                                                   (dl.CharStart == hl.CharStart && dl.CharEnd == hl.CharEnd)))
                        {
                            mergedLints.Add(hl);
                        }
                    }

                    mergedLints = PathAndFileFilter.FilterLints(mergedLints, text);

                    var widget = _floatingWidget;
                    if (widget != null)
                    {
                        _ = widget.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { widget.UpdateLints(mergedLints, text, false); } catch { }
                        }));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Debug("Harper", $"Worker error: {ex.Message}");
            }
            finally
            {
                lock (_harperLock)
                {
                    if (_harperCts == cts) _harperCts = null;
                }
                cts.Dispose();
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Stop();
        _keyboardHook?.Dispose();
        _trayIconService?.Dispose();
        _hotkeyService?.Dispose();
        _dictionaryUpdateService?.Dispose();
        _singleInstanceManager?.Dispose();

        lock (_harperLock)
        {
            _harperCts?.Cancel();
            _harperCts?.Dispose();
            _harperCts = null;
        }

        base.OnExit(e);
    }

    private static void LogCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FreeAutocorrect"
            );
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            string logPath = System.IO.Path.Combine(dir, "crash.log");
            string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n\n";
            System.IO.File.AppendAllText(logPath, entry);
        }
        catch { }
    }
}
