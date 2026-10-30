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
        var harperEngine = new HarperEngineService(settings);
        var typoDictionary = new AutoCorrectDictionary();

        // Add custom words from user settings to dictionary
        foreach (var word in settings.CustomDictionary)
        {
            typoDictionary.AddCustomWord(word, word);
        }

        _keyboardHook = new LowLevelKeyboardHook(typoDictionary, settings);
        _keyboardHook.Start();

        _trayIconService = new TrayIconService(settings, harperEngine);
        _hotkeyService = new GlobalHotkeyService();

        var floatingWidget = new FloatingWidget(settings, () => _keyboardHook?.LastActiveWindow ?? IntPtr.Zero);

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
                var dictLints = typoDictionary.LintText(rollingText);

                floatingWidget.Dispatcher.Invoke(() =>
                {
                    floatingWidget.UpdateLints(new List<LintItem>(dictLints), rollingText, false);
                });

                // 2. Offline Harper grammar engine runs asynchronously in background without blocking UI
                if (harperEngine.IsAvailable && rollingText.Length >= 4)
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try
                        {
                            var harperLints = await harperEngine.LintTextAsync(rollingText);
                            if (harperLints != null && harperLints.Count > 0)
                            {
                                var mergedLints = new List<LintItem>(dictLints);
                                foreach (var hl in harperLints)
                                {
                                    if (!mergedLints.Any(dl => dl.MatchedText.Equals(hl.MatchedText, StringComparison.OrdinalIgnoreCase) ||
                                                           (dl.CharStart == hl.CharStart && dl.CharEnd == hl.CharEnd)))
                                    {
                                        mergedLints.Add(hl);
                                    }
                                }

                                floatingWidget.Dispatcher.Invoke(() =>
                                {
                                    floatingWidget.UpdateLints(mergedLints, rollingText, false);
                                });
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
        };

        _dictionaryUpdateService = new DictionaryUpdateService(settings, typoDictionary);

        _mainWindow = new MainWindow(
            settings,
            harperEngine,
            typoDictionary,
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

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Stop();
        _keyboardHook?.Dispose();
        _trayIconService?.Dispose();
        _hotkeyService?.Dispose();
        _dictionaryUpdateService?.Dispose();
        _singleInstanceManager?.Dispose();
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
