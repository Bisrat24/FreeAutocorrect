using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeAutocorrect.Models;
using FreeAutocorrect.Services;
using FreeAutocorrect.UI;

namespace FreeAutocorrect;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HarperEngineService _harperEngine;
    private readonly AutoCorrectDictionary _typoDictionary;
    private readonly LowLevelKeyboardHook _keyboardHook;
    private readonly TrayIconService _trayService;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly FloatingWidget _floatingWidget;
    private readonly DictionaryUpdateService _updateService;

    private readonly DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _lintCts;
    private List<LintItem> _allLints = new();
    private string _currentFilter = "All";
    private bool _isClosingForReal = false;
    private bool _isLoaded = false;

    public MainWindow(
        AppSettings settings,
        HarperEngineService harperEngine,
        AutoCorrectDictionary typoDictionary,
        LowLevelKeyboardHook keyboardHook,
        TrayIconService trayService,
        GlobalHotkeyService hotkeyService,
        FloatingWidget floatingWidget,
        DictionaryUpdateService updateService)
    {
        _settings = settings;
        _harperEngine = harperEngine;
        _typoDictionary = typoDictionary;
        _keyboardHook = keyboardHook;
        _trayService = trayService;
        _hotkeyService = hotkeyService;
        _floatingWidget = floatingWidget;
        _updateService = updateService;

        InitializeComponent();

        // Debounce timer for live editor typing
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _debounceTimer.Tick += DebounceTimer_Tick;

        // Wire up keyboard hook events
        _keyboardHook.OnWordCorrected += HandleWordCorrected;

        // Wire up tray icon events
        _trayService.OnOpenAssistantRequested += ShowAndActivate;
        _trayService.OnCheckSelectedRequested += HandleCheckSelectedFromActiveApp;
        _trayService.OnOpenSettingsRequested += ShowSettingsTab;
        _trayService.OnExitRequested += QuitApplication;
        _trayService.OnToggleFloatingWidgetRequested += HandleToggleFloatingWidget;

        // Wire up floating widget events
        _floatingWidget.OnOpenAssistant += ShowAndActivate;
        _floatingWidget.OnTriggerCheck += HandleCheckSelectedFromActiveApp;
        _floatingWidget.OnReplaceTextInTarget += HandleReplaceTextFromWidget;

        _settings.OnSettingsChanged += () => Dispatcher.BeginInvoke(new Action(LoadSettingsToUI));

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void HandleToggleFloatingWidget(bool show)
    {
        if (show)
        {
            _floatingWidget.Show();
        }
        else
        {
            _floatingWidget.Hide();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Set window and taskbar icon explicitly
        try
        {
            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
            if (System.IO.File.Exists(iconPath))
            {
                var iconUri = new Uri(iconPath, UriKind.Absolute);
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
        }
        catch { }

        // Register Global Hotkey
        var helper = new WindowInteropHelper(this);
        _hotkeyService.Register(helper.Handle);
        _hotkeyService.OnHotkeyPressed += HandleGlobalHotkeyPressed;

        // Populate Settings UI
        LoadSettingsToUI();

        // Check if floating widget should be visible
        if (_settings.ShowFloatingWidget)
        {
            _floatingWidget.Show();
        }

        // Show engine status
        UpdateEngineStatus();

        TxtEditor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((s, ev) => RedrawUnderlines()));

        _isLoaded = true;

        // Run an initial sample check if editor has text
        if (string.IsNullOrWhiteSpace(TxtEditor.Text))
        {
            TxtPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            TxtPlaceholder.Visibility = Visibility.Collapsed;
            TriggerLintCheck();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isClosingForReal)
        {
            // Cancel close and hide to system tray instead!
            e.Cancel = true;
            Hide();
            _trayService.ShowNotification(
                "FreeAutocorrect Docked",
                "FreeAutocorrect is running in your system tray. Press Ctrl+Shift+G anytime to check grammar.",
                System.Windows.Forms.ToolTipIcon.Info
            );
        }
    }

    public void ShowAndActivate()
    {
        LoadSettingsToUI();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    public void QuitApplication()
    {
        _isClosingForReal = true;
        _keyboardHook.Stop();
        _hotkeyService.Dispose();
        _floatingWidget.Close();
        _trayService.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _trayService.ShowNotification("Minimized to Tray", "FreeAutocorrect is docked in your system tray.");
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _trayService.ShowNotification("Docked to System Tray", "FreeAutocorrect continues running in the background.");
    }

    #region Tab Navigation
    private void TabStudio_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewStudio == null) return;
        ViewStudio.Visibility = Visibility.Visible;
        ViewAutocorrect.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Collapsed;
        ViewAbout.Visibility = Visibility.Collapsed;
    }

    private void TabAutocorrect_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewStudio == null) return;
        ViewStudio.Visibility = Visibility.Collapsed;
        ViewAutocorrect.Visibility = Visibility.Visible;
        ViewSettings.Visibility = Visibility.Collapsed;
        ViewAbout.Visibility = Visibility.Collapsed;
        RefreshAutocorrectStats();
    }

    private void TabSettings_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewStudio == null) return;
        ViewStudio.Visibility = Visibility.Collapsed;
        ViewAutocorrect.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Visible;
        ViewAbout.Visibility = Visibility.Collapsed;
        if (TxtLogPreview != null)
        {
            TxtLogPreview.Text = AppLogger.GetRecentLogLines(50);
            TxtLogPreview.ScrollToEnd();
        }
    }

    private void TabAbout_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewStudio == null) return;
        ViewStudio.Visibility = Visibility.Collapsed;
        ViewAutocorrect.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Collapsed;
        ViewAbout.Visibility = Visibility.Visible;
    }

    private void ShowSettingsTab()
    {
        ShowAndActivate();
        TabSettings_Checked(this, new RoutedEventArgs());
    }
    #endregion

    #region Editor and Grammar Checking
    private void TxtEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtPlaceholder.Visibility = string.IsNullOrEmpty(TxtEditor.Text) ? Visibility.Visible : Visibility.Collapsed;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        TriggerLintCheck();
    }

    private void BtnCheckNow_Click(object sender, RoutedEventArgs e)
    {
        TriggerLintCheck();
    }

    private void BtnPaste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                TxtEditor.Text = System.Windows.Clipboard.GetText();
            }
        }
        catch { }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        TxtEditor.Text = string.Empty;
        _allLints.Clear();
        RenderSuggestions();
        UpdateMetrics(string.Empty);
    }

    private void BtnCopyCorrected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(TxtEditor.Text))
            {
                System.Windows.Clipboard.SetText(TxtEditor.Text);
                System.Windows.MessageBox.Show("Text copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch { }
    }

    private async void TriggerLintCheck()
    {
        string text = TxtEditor.Text;

        UpdateMetrics(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            _allLints.Clear();
            RenderSuggestions();
            return;
        }

        _lintCts?.Cancel();
        _lintCts = new CancellationTokenSource();

        try
        {
            var lints = await _harperEngine.LintTextAsync(text);
            _allLints = lints;
            RenderSuggestions();

            // Also update floating widget with current suggestions
            _floatingWidget.UpdateLints(lints, text);
        }
        catch { }
    }

    private void UpdateMetrics(string text)
    {
        var metrics = ReadabilityService.Analyze(text);
        LblEditorStats.Text = $"{metrics.WordCount} words | {metrics.CharacterCount} characters | {metrics.SentenceCount} sentences | ~{metrics.ReadingTimeMinutes}m read";
        LblReadabilityScore.Text = $"{metrics.ReadabilityScore} ({metrics.ReadabilityGrade})";
        LblTone.Text = metrics.ToneSummary;
    }

    private void RenderSuggestions()
    {
        PanelSuggestions.Children.Clear();

        var filtered = _currentFilter switch
        {
            "Spelling" => _allLints.Where(l => l.Category == LintCategory.Spelling).ToList(),
            "Grammar" => _allLints.Where(l => l.Category == LintCategory.Grammar).ToList(),
            "Style" => _allLints.Where(l => l.Category == LintCategory.Style).ToList(),
            "Punctuation" => _allLints.Where(l => l.Category == LintCategory.Punctuation).ToList(),
            _ => _allLints
        };

        // Update counts on filter buttons
        int spellingCount = _allLints.Count(l => l.Category == LintCategory.Spelling);
        int grammarCount = _allLints.Count(l => l.Category == LintCategory.Grammar);
        int styleCount = _allLints.Count(l => l.Category == LintCategory.Style);
        int punctCount = _allLints.Count(l => l.Category == LintCategory.Punctuation);

        FilterAll.Content = $"All ({_allLints.Count})";
        FilterSpelling.Content = $"🔴 Spelling ({spellingCount})";
        FilterGrammar.Content = $"🔵 Grammar ({grammarCount})";
        FilterStyle.Content = $"🟡 Style ({styleCount})";
        FilterPunctuation.Content = $"🟢 Spaces ({punctCount})";

        LblIssuesCount.Text = $"{_allLints.Count} issues found";

        if (filtered.Count == 0)
        {
            PanelSuggestions.Children.Add(PanelEmptyState);
            return;
        }

        foreach (var lint in filtered)
        {
            var card = CreateSuggestionCard(lint);
            PanelSuggestions.Children.Add(card);
        }

        RedrawUnderlines();
    }

    private void TxtEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        RedrawUnderlines();
    }

    private void TxtEditor_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawUnderlines();
    }

    private void RedrawUnderlines()
    {
        if (UnderlineCanvas == null || TxtEditor == null) return;
        UnderlineCanvas.Children.Clear();

        string text = TxtEditor.Text;
        if (string.IsNullOrEmpty(text) || _allLints.Count == 0) return;

        foreach (var lint in _allLints)
        {
            if (lint.CharStart < 0 || lint.CharEnd > text.Length || lint.CharStart >= lint.CharEnd)
                continue;

            try
            {
                var startRect = TxtEditor.GetRectFromCharacterIndex(lint.CharStart, false);
                var endRect = TxtEditor.GetRectFromCharacterIndex(lint.CharEnd - 1, true);

                if (startRect.IsEmpty || endRect.IsEmpty) continue;

                double y = startRect.Bottom - 1;
                double x1 = Math.Max(2, startRect.Left);
                double x2 = Math.Min(TxtEditor.ActualWidth - 4, endRect.Right);

                if (x2 <= x1) continue;

                var strokeBrush = (Brush)new BrushConverter().ConvertFromString(lint.FormattedBadgeColor)!;
                var pathGeom = CreateWavyLine(x1, x2, y, waveHeight: 1.25, waveLength: 2.7);

                var path = new System.Windows.Shapes.Path
                {
                    Data = pathGeom,
                    Stroke = strokeBrush,
                    StrokeThickness = 1.15,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Cursor = Cursors.Hand,
                    ToolTip = new ToolTip
                    {
                        Content = $"[{lint.CategoryDisplayName.ToUpper()}] {lint.Message}\nClick to replace with: \"{lint.PrimarySuggestion}\"",
                        Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!,
                        Foreground = Brushes.White,
                        BorderBrush = strokeBrush,
                        BorderThickness = new Thickness(1),
                        FontSize = 12,
                        Padding = new Thickness(8)
                    }
                };

                var captured = lint;
                path.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    ApplySuggestionInEditor(captured);
                };

                UnderlineCanvas.Children.Add(path);
            }
            catch { }
        }
    }

    private static PathGeometry CreateWavyLine(double x1, double x2, double y, double waveHeight, double waveLength)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new System.Windows.Point(x1, y) };

        double curX = x1;
        bool up = true;
        while (curX < x2)
        {
            double nextX = Math.Min(curX + waveLength, x2);
            double ctrlY = up ? y - waveHeight : y + waveHeight;
            figure.Segments.Add(new BezierSegment(
                new System.Windows.Point(curX + waveLength / 3, ctrlY),
                new System.Windows.Point(curX + (2 * waveLength) / 3, ctrlY),
                new System.Windows.Point(nextX, y),
                true));
            curX = nextX;
            up = !up;
        }

        geometry.Figures.Add(figure);
        return geometry;
    }

    private UIElement CreateSuggestionCard(LintItem lint)
    {
        var border = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString("#0F172A")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#334155")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();

        // Header: Category Badge + Rule Name
        var header = new DockPanel { LastChildFill = false };
        var badge = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(lint.FormattedBadgeColor)!,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2)
        };
        badge.Child = new TextBlock
        {
            Text = lint.CategoryDisplayName.ToUpper(),
            Foreground = Brushes.White,
            FontSize = 9,
            FontWeight = FontWeights.Bold
        };
        header.Children.Add(badge);

        var ruleLabel = new TextBlock
        {
            Text = lint.Rule,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#64748B")!,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(ruleLabel, Dock.Left);
        header.Children.Add(ruleLabel);

        stack.Children.Add(header);

        // Explanation Message
        var msg = new TextBlock
        {
            Text = lint.Message,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#E2E8F0")!,
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 8),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(msg);

        // Original word vs replacement snippet
        if (!string.IsNullOrEmpty(lint.MatchedText))
        {
            var diffPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var originalBlock = new TextBlock
            {
                Text = $"\"{lint.MatchedText}\"",
                TextDecorations = TextDecorations.Strikethrough,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            };
            diffPanel.Children.Add(originalBlock);

            if (!string.IsNullOrEmpty(lint.PrimarySuggestion))
            {
                diffPanel.Children.Add(new TextBlock { Text = "→", Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!, Margin = new Thickness(0, 0, 8, 0) });
                var fixBlock = new TextBlock
                {
                    Text = $"\"{lint.PrimarySuggestion}\"",
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#10B981")!,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold
                };
                diffPanel.Children.Add(fixBlock);
            }

            stack.Children.Add(diffPanel);
        }

        // Action Buttons Row
        var actionsPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        if (!string.IsNullOrEmpty(lint.PrimarySuggestion))
        {
            var acceptBtn = new Button
            {
                Content = $"✓ Accept: {lint.PrimarySuggestion}",
                Background = (Brush)new BrushConverter().ConvertFromString("#10B981")!,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 8, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            acceptBtn.Click += (s, e) => ApplySuggestionInEditor(lint);
            actionsPanel.Children.Add(acceptBtn);
        }

        // Alternative suggestions
        for (int i = 1; i < lint.Suggestions.Count && i < 3; i++)
        {
            var alt = lint.Suggestions[i];
            var altBtn = new Button
            {
                Content = alt,
                Background = (Brush)new BrushConverter().ConvertFromString("#334155")!,
                Foreground = Brushes.White,
                FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            altBtn.Click += (s, e) =>
            {
                lint.Suggestions[0] = alt;
                ApplySuggestionInEditor(lint);
            };
            actionsPanel.Children.Add(altBtn);
        }

        // Ignore button
        var ignoreBtn = new Button
        {
            Content = "Ignore",
            Background = Brushes.Transparent,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!,
            FontSize = 11,
            Padding = new Thickness(8, 5, 8, 5),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        ignoreBtn.Click += (s, e) =>
        {
            _allLints.Remove(lint);
            RenderSuggestions();
        };
        actionsPanel.Children.Add(ignoreBtn);

        // If spelling, add to custom dictionary
        if (lint.Category == LintCategory.Spelling && !string.IsNullOrEmpty(lint.MatchedText))
        {
            var addToDictBtn = new Button
            {
                Content = "+ Add to Dictionary",
                Background = Brushes.Transparent,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#60A5FA")!,
                FontSize = 11,
                Padding = new Thickness(8, 5, 8, 5),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            addToDictBtn.Click += (s, e) =>
            {
                _settings.CustomDictionary.Add(lint.MatchedText);
                _settings.Save();
                LoadSettingsToUI();
                _allLints.Remove(lint);
                RenderSuggestions();
            };
            actionsPanel.Children.Add(addToDictBtn);
        }

        stack.Children.Add(actionsPanel);
        border.Child = stack;
        return border;
    }

    private void ApplySuggestionInEditor(LintItem lint)
    {
        string text = TxtEditor.Text;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(lint.PrimarySuggestion)) return;

        try
        {
            string newText;
            if (lint.CharStart >= 0 && lint.CharEnd <= text.Length && lint.CharStart <= lint.CharEnd)
            {
                newText = text.Substring(0, lint.CharStart) + lint.PrimarySuggestion + text.Substring(lint.CharEnd);
            }
            else if (!string.IsNullOrEmpty(lint.MatchedText))
            {
                newText = text.Replace(lint.MatchedText, lint.PrimarySuggestion);
            }
            else
            {
                return;
            }

            TxtEditor.Text = newText;
            _settings.TotalGrammarFixes++;
            _settings.Save();
            TriggerLintCheck();
        }
        catch { }
    }

    private void BtnAcceptAll_Click(object sender, RoutedEventArgs e)
    {
        string text = TxtEditor.Text;
        if (string.IsNullOrEmpty(text) || _allLints.Count == 0) return;

        // Apply suggestions in reverse order of span position so indices remain valid
        var validLints = _allLints
            .Where(l => !string.IsNullOrEmpty(l.PrimarySuggestion) && l.CharStart >= 0 && l.CharEnd <= text.Length)
            .OrderByDescending(l => l.CharStart)
            .ToList();

        string updated = text;
        foreach (var l in validLints)
        {
            updated = updated.Substring(0, l.CharStart) + l.PrimarySuggestion + updated.Substring(l.CharEnd);
            _settings.TotalGrammarFixes++;
        }

        _settings.Save();
        TxtEditor.Text = updated;
        TriggerLintCheck();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn == FilterAll) _currentFilter = "All";
            else if (btn == FilterSpelling) _currentFilter = "Spelling";
            else if (btn == FilterGrammar) _currentFilter = "Grammar";
            else if (btn == FilterStyle) _currentFilter = "Style";
            else if (btn == FilterPunctuation) _currentFilter = "Punctuation";

            RenderSuggestions();
        }
    }
    #endregion

    #region Live Autocorrect and Hotkey Handlers
    private void HandleWordCorrected(string original, string fixedWord)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshAutocorrectStats();

            if (_settings.PlaySoundOnCorrection)
            {
                try { SystemSounds.Beep.Play(); } catch { }
            }

            if (_settings.ShowNotificationOnCorrection)
            {
                _trayService.ShowNotification(
                    "Auto-Corrected",
                    $"Fixed: \"{original}\" → \"{fixedWord}\"",
                    System.Windows.Forms.ToolTipIcon.Info
                );
            }
        });
    }

    private void HandleGlobalHotkeyPressed()
    {
        Dispatcher.Invoke(async () =>
        {
            string selected = TextAutomationService.GetSelectedTextFromActiveApp();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var lints = await _harperEngine.LintTextAsync(selected);
                _floatingWidget.Show();
                _floatingWidget.UpdateLints(lints, selected, isSelectedText: true);
            }
            else
            {
                ShowAndActivate();
            }
        });
    }

    private void HandleCheckSelectedFromActiveApp()
    {
        HandleGlobalHotkeyPressed();
    }

    private void HandleReplaceTextFromWidget(string newText)
    {
        TextAutomationService.ReplaceSelectedTextInActiveApp(newText);
    }

    private void RefreshAutocorrectStats()
    {
        LblStatWordsCorrected.Text = _settings.TotalWordsCorrected.ToString("N0");
        LblStatGrammarFixes.Text = _settings.TotalGrammarFixes.ToString("N0");
    }

    private bool _isUpdatingUI = false;

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUI || !_isLoaded || _settings == null || _floatingWidget == null || _trayService == null) return;
        if (ChkLiveAutocorrect == null || ChkAutoCapsSentence == null || ChkFixDoubleCaps == null || ChkShowFloating == null || ChkPlaySound == null) return;

        _settings.LiveAutocorrectEnabled = ChkLiveAutocorrect.IsChecked == true;
        _settings.AutoCapitalizeSentence = ChkAutoCapsSentence.IsChecked == true;
        _settings.FixDoubleCapitals = ChkFixDoubleCaps.IsChecked == true;
        _settings.ShowFloatingWidget = ChkShowFloating.IsChecked == true;
        _settings.PlaySoundOnCorrection = ChkPlaySound.IsChecked == true;
        if (ChkTabCompletion != null) _settings.TabCompletionEnabled = ChkTabCompletion.IsChecked == true;
        if (ChkDailyUpdate != null) _settings.DailyDictionaryUpdateEnabled = ChkDailyUpdate.IsChecked == true;
        _settings.Save();

        if (_settings.ShowFloatingWidget)
        {
            _floatingWidget.Show();
        }
        else
        {
            _floatingWidget.Hide();
        }

        _trayService.BuildContextMenu();
    }
    #endregion

    #region Settings Tab Handlers
    private void LoadSettingsToUI()
    {
        _isUpdatingUI = true;
        try
        {
            ChkLiveAutocorrect.IsChecked = _settings.LiveAutocorrectEnabled;
            ChkAutoCapsSentence.IsChecked = _settings.AutoCapitalizeSentence;
            ChkFixDoubleCaps.IsChecked = _settings.FixDoubleCapitals;
            ChkShowFloating.IsChecked = _settings.ShowFloatingWidget;
            ChkPlaySound.IsChecked = _settings.PlaySoundOnCorrection;
            if (ChkTabCompletion != null) ChkTabCompletion.IsChecked = _settings.TabCompletionEnabled;
            if (ChkDailyUpdate != null) ChkDailyUpdate.IsChecked = _settings.DailyDictionaryUpdateEnabled;

            if (TxtDictionaryUpdateStatus != null)
            {
                if (_settings.LastDictionaryUpdateDate.HasValue)
                {
                    TxtDictionaryUpdateStatus.Text = $"Last updated: {_settings.LastDictionaryUpdateDate.Value:MMM dd, yyyy HH:mm}";
                }
                else
                {
                    TxtDictionaryUpdateStatus.Text = "Daily auto-update active (100% offline fallback ready)";
                }
            }
        }
        finally
        {
            _isUpdatingUI = false;
        }

        // Auto-Finish Hotkey
        if (CmbAutoFinishKey != null)
        {
            foreach (ComboBoxItem item in CmbAutoFinishKey.Items)
            {
                if (item.Tag?.ToString() == _settings.AutoFinishHotkey)
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }

        // Dialect
        foreach (ComboBoxItem item in CmbDialect.Items)
        {
            if (item.Tag?.ToString() == _settings.HarperDialect)
            {
                item.IsSelected = true;
                break;
            }
        }

        // Snippets
        ListSnippets.Items.Clear();
        foreach (var kvp in _settings.CustomSnippets)
        {
            ListSnippets.Items.Add($"{kvp.Key}  →  {kvp.Value}");
        }

        // Dictionary
        ListDictWords.Items.Clear();
        foreach (var word in _settings.CustomDictionary)
        {
            ListDictWords.Items.Add(word);
        }

        RefreshAutocorrectStats();
    }

    private async void BtnUpdateDictionary_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdateDictionary.IsEnabled = false;
        TxtDictionaryUpdateStatus.Text = "Checking online wordlists for updates...";
        var res = await _updateService.CheckForUpdatesAsync();
        TxtDictionaryUpdateStatus.Text = res.Message;
        BtnUpdateDictionary.IsEnabled = true;
    }

    private void CmbAutoFinishKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null || CmbAutoFinishKey == null) return;

        if (CmbAutoFinishKey.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            _settings.AutoFinishHotkey = item.Tag.ToString()!;
            _settings.Save();
        }
    }

    private void CmbDialect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null || CmbDialect == null) return;

        if (CmbDialect.SelectedItem is ComboBoxItem item && item.Tag != null)
        {
            _settings.HarperDialect = item.Tag.ToString()!;
            _settings.Save();
        }
    }

    private void BtnAddSnippet_Click(object sender, RoutedEventArgs e)
    {
        string key = TxtSnippetKey.Text.Trim();
        string val = TxtSnippetValue.Text.Trim();

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
        {
            _settings.CustomSnippets[key] = val;
            _settings.Save();
            TxtSnippetKey.Clear();
            TxtSnippetValue.Clear();
            LoadSettingsToUI();
        }
    }

    private void BtnAddWord_Click(object sender, RoutedEventArgs e)
    {
        string word = TxtNewDictWord.Text.Trim();
        if (!string.IsNullOrEmpty(word) && !_settings.CustomDictionary.Contains(word))
        {
            _settings.CustomDictionary.Add(word);
            _settings.Save();
            TxtNewDictWord.Clear();
            LoadSettingsToUI();
        }
    }

    private void BtnRemoveWord_Click(object sender, RoutedEventArgs e)
    {
        if (ListDictWords.SelectedItem is string selected)
        {
            _settings.CustomDictionary.Remove(selected);
            _settings.Save();
            LoadSettingsToUI();
        }
    }

    private void BtnOpenAnalysisLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.OpenLogFile();
    }

    private void BtnClearAnalysisLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.ClearLog();
        if (TxtLogPreview != null)
        {
            TxtLogPreview.Text = AppLogger.GetRecentLogLines(50);
        }
    }

    private void BtnRefreshLogPreview_Click(object sender, RoutedEventArgs e)
    {
        if (TxtLogPreview != null)
        {
            TxtLogPreview.Text = AppLogger.GetRecentLogLines(50);
            TxtLogPreview.ScrollToEnd();
        }
    }

    private void UpdateEngineStatus()
    {
        if (_harperEngine.IsAvailable)
        {
            LblHarperStatus.Text = "Harper Rust Engine: Ready (Offline)";
            LblHarperStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#34D399")!;
            LblStatEngineStatus.Text = "Ready (Offline)";
            LblStatEngineStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#34D399")!;
        }
        else
        {
            LblHarperStatus.Text = "Harper Engine: Missing binary";
            LblHarperStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
            LblStatEngineStatus.Text = "Missing binary";
            LblStatEngineStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
        }
    }
    #endregion
}
