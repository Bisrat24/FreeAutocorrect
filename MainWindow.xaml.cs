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
        _floatingWidget.OnLocateLintRequested += HandleLocateLintInStudio;
        _floatingWidget.OnWordAddedToDictionary += HandleWordAddedToDictionary;

        _settings.OnSettingsChanged += () => Dispatcher.BeginInvoke(new Action(LoadSettingsToUI));

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void HandleLocateLintInStudio(LintItem lint)
    {
        if (lint == null || TxtEditor == null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (lint.CharStart >= 0 && lint.CharEnd <= TxtEditor.Text.Length && lint.CharStart < lint.CharEnd)
                {
                    TxtEditor.Focus();
                    TxtEditor.Select(lint.CharStart, lint.CharEnd - lint.CharStart);
                }
            }
            catch { }
        }));
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

    private void BtnImportSelected_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IntPtr target = _floatingWidget.GetTargetAppWindow();
            string selected = ActiveDocumentService.GetSelectedText(target);
            if (string.IsNullOrWhiteSpace(selected))
            {
                selected = TextAutomationService.GetSelectedTextFromActiveApp(target);
            }

            if (!string.IsNullOrWhiteSpace(selected))
            {
                TxtEditor.Text = selected;
                TriggerLintCheck();
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "No highlighted text was found in your active application.\n\nTip: Highlight text in any application (Word, Chrome, Notepad) and click this button or press Ctrl+Shift+G.",
                    "Import Selected Text",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch { }
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
        LblEditorStats.Text = $"{metrics.WordCount} words | {metrics.CharacterCount} characters | {metrics.SentenceCount} sentences";
        LblDocMetrics.Text = $"{metrics.WordCount} words • ~{metrics.ReadingTimeMinutes}m read • ~{metrics.SpeakingTimeMinutes}m speak";
        LblToneBadge.Text = metrics.ToneSummary;
        LblReadabilityScore.Text = $"{metrics.ReadabilityScore} ({metrics.ReadabilityGrade})";
        LblTone.Text = metrics.ToneSummary;
    }

    private void RenderSuggestions()
    {
        PanelSuggestions.Children.Clear();

        // 4 Grammarly Pillars Breakdown
        int correctnessCount = _allLints.Count(l => l.Pillar == GrammarlyPillar.Correctness);
        int clarityCount = _allLints.Count(l => l.Pillar == GrammarlyPillar.Clarity);
        int engagementCount = _allLints.Count(l => l.Pillar == GrammarlyPillar.Engagement);
        int deliveryCount = _allLints.Count(l => l.Pillar == GrammarlyPillar.Delivery);

        LblScoreCorrectness.Text = correctnessCount.ToString();
        LblScoreClarity.Text = clarityCount.ToString();
        LblScoreEngagement.Text = engagementCount.ToString();
        LblScoreDelivery.Text = deliveryCount.ToString();

        // Overall Performance Score Dial calculation
        int score = (_allLints.Count > 0 || !string.IsNullOrWhiteSpace(TxtEditor.Text))
            ? Math.Clamp(100 - ((correctnessCount * 6) + (clarityCount * 3) + (engagementCount * 2) + (deliveryCount * 1)), 15, 100)
            : 100;

        LblOverallScore.Text = score.ToString();
        LblScoreRating.Text = score switch
        {
            >= 95 => "Exceptional",
            >= 85 => "Great job!",
            >= 70 => "Looking good",
            >= 50 => "Needs polish",
            _ => "Needs attention"
        };

        var scoreColor = (Brush)new BrushConverter().ConvertFromString(score switch
        {
            >= 85 => "#34D399",
            >= 70 => "#38BDF8",
            >= 50 => "#FBBF24",
            _ => "#F87171"
        })!;
        LblOverallScore.Foreground = scoreColor;
        LblScoreRating.Foreground = scoreColor;

        BorderScoreCircle.Background = (Brush)new BrushConverter().ConvertFromString(score switch
        {
            >= 85 => "#064E3B",
            >= 70 => "#0C4A6E",
            >= 50 => "#78350F",
            _ => "#450A0A"
        })!;

        BorderScoreCircle.BorderBrush = (Brush)new BrushConverter().ConvertFromString(score switch
        {
            >= 85 => "#10B981",
            >= 70 => "#0284C7",
            >= 50 => "#F59E0B",
            _ => "#EF4444"
        })!;

        // Update counts on Grammarly 4-pillar filter buttons
        FilterAll.Content = $"All ({_allLints.Count})";
        FilterCorrectness.Content = $"🔴 Correctness ({correctnessCount})";
        FilterClarity.Content = $"🔵 Clarity ({clarityCount})";
        FilterEngagement.Content = $"🟢 Engagement ({engagementCount})";
        FilterDelivery.Content = $"🟣 Delivery ({deliveryCount})";

        var filtered = _currentFilter switch
        {
            "Correctness" => _allLints.Where(l => l.Pillar == GrammarlyPillar.Correctness).ToList(),
            "Clarity" => _allLints.Where(l => l.Pillar == GrammarlyPillar.Clarity).ToList(),
            "Engagement" => _allLints.Where(l => l.Pillar == GrammarlyPillar.Engagement).ToList(),
            "Delivery" => _allLints.Where(l => l.Pillar == GrammarlyPillar.Delivery).ToList(),
            _ => _allLints
        };

        LblIssuesCount.Text = $"{_allLints.Count} issues found";

        // Display Ignored / Protected Technical Content Indicator in Studio
        string? protectedSummary = PathAndFileFilter.GetIgnoredSpansSummary(TxtEditor.Text);
        if (!string.IsNullOrEmpty(protectedSummary))
        {
            StudioProtectedIndicator.Visibility = Visibility.Visible;
            TxtStudioProtectedDetails.Text = protectedSummary;
        }
        else
        {
            StudioProtectedIndicator.Visibility = Visibility.Collapsed;
        }

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
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(lint.PillarColor)!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var stack = new StackPanel();

        // Header: Grammarly Pillar Badge + Rule Name
        var header = new DockPanel { LastChildFill = false };
        var badge = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(lint.PillarBadgeBg)!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(lint.PillarColor)!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2)
        };
        badge.Child = new TextBlock
        {
            Text = $"{lint.PillarIcon} {lint.PillarName.ToUpper()}",
            Foreground = (Brush)new BrushConverter().ConvertFromString(lint.PillarColor)!,
            FontSize = 9.5,
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
            FontSize = 12.5,
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

        // Context snippet preview panel with inline interactive quick replacement (toggled when clicking Show)
        var contextPanel = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString("#0F172A")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#38BDF8")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed
        };
        var contextStack = new StackPanel();
        var contextText = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)new BrushConverter().ConvertFromString("#93C5FD")!,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        contextStack.Children.Add(contextText);
        var quickActionsRow = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        contextStack.Children.Add(quickActionsRow);
        contextPanel.Child = contextStack;
        stack.Children.Add(contextPanel);

        // Action Buttons Row: Show icon button before fix button
        var actionsPanel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };

        if (!string.IsNullOrEmpty(lint.PrimarySuggestion))
        {
            var showBtn = new Button
            {
                Content = "👁️ Show",
                Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#0284C7")!,
                BorderThickness = new Thickness(1),
                Foreground = (Brush)new BrushConverter().ConvertFromString("#38BDF8")!,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 6),
                Cursor = Cursors.Hand,
                ToolTip = "Locate and highlight this error in the editor"
            };
            showBtn.Click += (s, e) =>
            {
                if (lint.CharStart >= 0 && lint.CharEnd <= TxtEditor.Text.Length && lint.CharStart < lint.CharEnd)
                {
                    TxtEditor.Focus();
                    TxtEditor.Select(lint.CharStart, lint.CharEnd - lint.CharStart);
                }

                if (contextPanel.Visibility == Visibility.Visible)
                {
                    contextPanel.Visibility = Visibility.Collapsed;
                    border.BorderBrush = (Brush)new BrushConverter().ConvertFromString(lint.PillarColor)!;
                }
                else
                {
                    string ctx = GetEditorSurroundingContext(lint);
                    contextText.Text = $"👁️ Context: {ctx}";
                    quickActionsRow.Children.Clear();

                    var candidates = lint.GetCleanSuggestions();
                    if (candidates.Count == 0 && !string.IsNullOrEmpty(lint.PrimarySuggestion))
                    {
                        candidates.Add(lint.PrimarySuggestion);
                    }

                    for (int i = 0; i < candidates.Count && i < 3; i++)
                    {
                        string candidate = candidates[i];
                        int num = i + 1;
                        var chip = new Button
                        {
                            Content = i == 0 ? $"[{num}] ✓ Replace: {candidate}" : $"[{num}] ✓ {candidate}",
                            Background = (Brush)new BrushConverter().ConvertFromString(i == 0 ? "#064E3B" : "#1E293B")!,
                            BorderBrush = (Brush)new BrushConverter().ConvertFromString(i == 0 ? "#10B981" : "#334155")!,
                            BorderThickness = new Thickness(1),
                            Foreground = (Brush)new BrushConverter().ConvertFromString(i == 0 ? "#6EE7B7" : "#E2E8F0")!,
                            FontSize = 10.5,
                            FontWeight = i == 0 ? FontWeights.Bold : FontWeights.SemiBold,
                            Padding = new Thickness(8, 3, 8, 3),
                            Margin = new Thickness(0, 0, 5, 4),
                            Cursor = Cursors.Hand,
                            ToolTip = $"Press [{num}] or click to quick-replace with '{candidate}'"
                        };
                        string chosen = candidate;
                        chip.Click += (cs, ce) => ApplySuggestionInEditor(lint, chosen);
                        quickActionsRow.Children.Add(chip);
                    }

                    // Add to Custom Dictionary button if spelling category
                    if (lint.Category == LintCategory.Spelling && !string.IsNullOrEmpty(lint.MatchedText))
                    {
                        var addDictChip = new Button
                        {
                            Content = $"+ Add \"{lint.MatchedText}\"",
                            Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!,
                            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#60A5FA")!,
                            BorderThickness = new Thickness(1),
                            Foreground = (Brush)new BrushConverter().ConvertFromString("#93C5FD")!,
                            FontSize = 10.5,
                            FontWeight = FontWeights.SemiBold,
                            Padding = new Thickness(8, 3, 8, 3),
                            Margin = new Thickness(0, 0, 5, 4),
                            Cursor = Cursors.Hand,
                            ToolTip = $"Add '{lint.MatchedText}' to custom dictionary"
                        };
                        string wordToAdd = lint.MatchedText;
                        addDictChip.Click += (cs, ce) => AddWordToCustomDictionary(wordToAdd);
                        quickActionsRow.Children.Add(addDictChip);
                    }

                    contextPanel.Visibility = Visibility.Visible;
                    border.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#38BDF8")!;
                }
            };
            actionsPanel.Children.Add(showBtn);

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
            addToDictBtn.Click += (s, e) => AddWordToCustomDictionary(lint.MatchedText);
            actionsPanel.Children.Add(addToDictBtn);
        }

        stack.Children.Add(actionsPanel);
        border.Child = stack;
        return border;
    }

    public string GetEditorSurroundingContext(LintItem lint)
    {
        string text = TxtEditor.Text;
        if (string.IsNullOrEmpty(text) || lint == null) return string.Empty;

        int start = lint.CharStart;
        int end = lint.CharEnd;

        if (start < 0 || end > text.Length || start >= end)
        {
            if (!string.IsNullOrEmpty(lint.MatchedText))
            {
                int idx = text.IndexOf(lint.MatchedText, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    start = idx;
                    end = idx + lint.MatchedText.Length;
                }
            }
        }

        if (start < 0 || end > text.Length || start >= end)
        {
            return lint.MatchedText;
        }

        int ctxStart = Math.Max(0, start - 25);
        int ctxEnd = Math.Min(text.Length, end + 25);

        string prefix = (ctxStart > 0 ? "..." : "") + text.Substring(ctxStart, start - ctxStart).TrimStart();
        string target = "[" + text.Substring(start, end - start) + "]";
        string suffix = text.Substring(end, ctxEnd - end).TrimEnd() + (ctxEnd < text.Length ? "..." : "");

        return $"{prefix} {target} {suffix}".Replace('\r', ' ').Replace('\n', ' ');
    }

    private void ApplySuggestionInEditor(LintItem lint, string? customReplacement = null)
    {
        string text = TxtEditor.Text;
        string fix = !string.IsNullOrEmpty(customReplacement) ? customReplacement : lint.PrimarySuggestion;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(fix)) return;

        try
        {
            string newText;
            if (lint.CharStart >= 0 && lint.CharEnd <= text.Length && lint.CharStart <= lint.CharEnd)
            {
                newText = text.Substring(0, lint.CharStart) + fix + text.Substring(lint.CharEnd);
            }
            else if (!string.IsNullOrEmpty(lint.MatchedText))
            {
                newText = text.Replace(lint.MatchedText, fix);
            }
            else
            {
                return;
            }

            TxtEditor.Text = newText;
            _settings.TotalGrammarFixes++;
            _settings.Save();
            SoundFeedbackService.PlaySuggestionSound(_settings.PlaySoundOnSuggestion);
            TriggerLintCheck();
        }
        catch { }
    }

    private void HandleWordAddedToDictionary(string word)
    {
        AddWordToCustomDictionary(word);
    }

    public void AddWordToCustomDictionary(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        word = word.Trim();
        if (!_settings.CustomDictionary.Contains(word, StringComparer.OrdinalIgnoreCase))
        {
            _settings.CustomDictionary.Add(word);
            _settings.Save();
        }
        _typoDictionary.AddCustomWord(word, word);
        _allLints.RemoveAll(l => string.Equals(l.MatchedText, word, StringComparison.OrdinalIgnoreCase));
        Dispatcher.BeginInvoke(new Action(() =>
        {
            LoadSettingsToUI();
            RenderSuggestions();
            TriggerLintCheck();
        }));
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
            else if (btn == FilterCorrectness) _currentFilter = "Correctness";
            else if (btn == FilterClarity) _currentFilter = "Clarity";
            else if (btn == FilterEngagement) _currentFilter = "Engagement";
            else if (btn == FilterDelivery) _currentFilter = "Delivery";

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

            SoundFeedbackService.PlayCorrectionSound(_settings.PlaySoundOnCorrection);

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
            IntPtr target = _floatingWidget.GetTargetAppWindow();
            string selected = ActiveDocumentService.GetSelectedText(target);
            if (string.IsNullOrWhiteSpace(selected))
            {
                selected = TextAutomationService.GetSelectedTextFromActiveApp(target);
            }

            if (!string.IsNullOrWhiteSpace(selected))
            {
                var lints = await _harperEngine.LintTextAsync(selected);
                _floatingWidget.Show();
                _floatingWidget.UpdateLints(lints, selected, isSelectedText: true);
                _floatingWidget.OpenSuggestionsDropdown();
            }
            else
            {
                var doc = ActiveDocumentService.GetDocumentContext(target);
                if (!string.IsNullOrWhiteSpace(doc.FullText))
                {
                    var lints = await _harperEngine.LintTextAsync(doc.FullText);
                    _floatingWidget.Show();
                    _floatingWidget.UpdateLints(lints, doc.FullText, isSelectedText: false);
                    _floatingWidget.OpenSuggestionsDropdown();
                }
                else
                {
                    _floatingWidget.Show();
                    _floatingWidget.OpenSuggestionsDropdown();
                }
            }
        });
    }

    private void HandleCheckSelectedFromActiveApp()
    {
        HandleGlobalHotkeyPressed();
    }

    private void HandleReplaceTextFromWidget(string newText)
    {
        IntPtr target = _floatingWidget.GetTargetAppWindow();
        TextAutomationService.ReplaceSelectedTextInActiveApp(target, newText);
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
        if (ChkPlaySoundSuggestion != null) _settings.PlaySoundOnSuggestion = ChkPlaySoundSuggestion.IsChecked == true;
        if (ChkTabCompletion != null) _settings.TabCompletionEnabled = ChkTabCompletion.IsChecked == true;
        if (ChkDailyUpdate != null) _settings.DailyDictionaryUpdateEnabled = ChkDailyUpdate.IsChecked == true;
        if (ChkLaunchOnStartup != null) _settings.LaunchOnStartup = ChkLaunchOnStartup.IsChecked == true;
        if (ChkEnableInExplorer != null)
        {
            _settings.EnableInExplorer = ChkEnableInExplorer.IsChecked == true;
            if (ChkSettingsEnableInExplorer != null) ChkSettingsEnableInExplorer.IsChecked = _settings.EnableInExplorer;
        }
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
            if (ChkPlaySoundSuggestion != null) ChkPlaySoundSuggestion.IsChecked = _settings.PlaySoundOnSuggestion;
            if (ChkTabCompletion != null) ChkTabCompletion.IsChecked = _settings.TabCompletionEnabled;
            if (ChkDailyUpdate != null) ChkDailyUpdate.IsChecked = _settings.DailyDictionaryUpdateEnabled;
            if (ChkLaunchOnStartup != null) ChkLaunchOnStartup.IsChecked = _settings.LaunchOnStartup;
            if (ChkEnableInExplorer != null) ChkEnableInExplorer.IsChecked = _settings.EnableInExplorer;
            if (ChkSettingsEnableInExplorer != null) ChkSettingsEnableInExplorer.IsChecked = _settings.EnableInExplorer;

            if (RadioFilterInclude != null && RadioFilterExclude != null)
            {
                if (string.Equals(_settings.AppFilterMode, "OnlyIncluded", StringComparison.OrdinalIgnoreCase))
                {
                    RadioFilterInclude.IsChecked = true;
                }
                else
                {
                    RadioFilterExclude.IsChecked = true;
                }
            }

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

        RefreshAppFiltersList();
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
        if (!string.IsNullOrEmpty(word))
        {
            AddWordToCustomDictionary(word);
            TxtNewDictWord.Clear();
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

    private void BtnExportDict_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                FileName = "custom_dictionary.txt",
                Title = "Export Custom Dictionary"
            };

            if (sfd.ShowDialog() == true)
            {
                string content;
                if (sfd.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    content = System.Text.Json.JsonSerializer.Serialize(_settings.CustomDictionary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                }
                else
                {
                    content = string.Join(Environment.NewLine, _settings.CustomDictionary);
                }

                System.IO.File.WriteAllText(sfd.FileName, content, System.Text.Encoding.UTF8);
                System.Windows.MessageBox.Show($"Exported {_settings.CustomDictionary.Count} custom word(s) to:\n{sfd.FileName}", "Dictionary Exported", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to export dictionary:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnImportDict_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Supported Files (*.txt;*.json)|*.txt;*.json|Text Files (*.txt)|*.txt|JSON Files (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Import Custom Dictionary"
            };

            if (ofd.ShowDialog() == true)
            {
                string fileText = System.IO.File.ReadAllText(ofd.FileName, System.Text.Encoding.UTF8);
                int added = ImportCustomWordsFromText(fileText, ofd.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

                LoadSettingsToUI();
                TriggerLintCheck();
                System.Windows.MessageBox.Show($"Successfully imported {added} new custom word(s) from:\n{ofd.FileName}", "Dictionary Imported", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to import dictionary:\n{ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public int ImportCustomWordsFromText(string rawContent, bool isJson)
    {
        if (string.IsNullOrWhiteSpace(rawContent)) return 0;
        int added = 0;

        List<string> candidateWords = new();
        if (isJson)
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(rawContent);
                if (parsed != null) candidateWords.AddRange(parsed);
            }
            catch
            {
                candidateWords.AddRange(rawContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }
        else
        {
            candidateWords.AddRange(rawContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var raw in candidateWords)
        {
            string clean = raw.Trim().Trim('"', '\'');
            if (clean.Length >= 2 && !clean.Contains(' ') && !_settings.CustomDictionary.Contains(clean, StringComparer.OrdinalIgnoreCase))
            {
                _settings.CustomDictionary.Add(clean);
                _typoDictionary.AddCustomWord(clean, clean);
                added++;
            }
        }

        if (added > 0)
        {
            _settings.Save();
        }

        return added;
    }

    private void RefreshAppFiltersList()
    {
        if (ListAppFilters == null) return;
        ListAppFilters.Items.Clear();

        bool isWhitelist = string.Equals(_settings.AppFilterMode, "OnlyIncluded", StringComparison.OrdinalIgnoreCase);
        var apps = isWhitelist ? _settings.IncludedApps : _settings.ExcludedApps;
        if (apps != null)
        {
            foreach (var app in apps)
            {
                ListAppFilters.Items.Add(app);
            }
        }
    }

    private void ChkSettingsEnableInExplorer_Changed(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUI || _settings == null) return;
        _settings.EnableInExplorer = ChkSettingsEnableInExplorer?.IsChecked == true;
        if (ChkEnableInExplorer != null) ChkEnableInExplorer.IsChecked = _settings.EnableInExplorer;
        _settings.Save();
    }

    private void RadioFilterMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingUI || _settings == null) return;
        _settings.AppFilterMode = RadioFilterInclude?.IsChecked == true ? "OnlyIncluded" : "AllExceptExcluded";
        _settings.Save();
        RefreshAppFiltersList();
    }

    private void BtnAddAppFilter_Click(object sender, RoutedEventArgs e)
    {
        string raw = TxtNewAppFilter.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (!raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            raw += ".exe";
        }

        bool isWhitelist = string.Equals(_settings.AppFilterMode, "OnlyIncluded", StringComparison.OrdinalIgnoreCase);
        var targetList = isWhitelist ? _settings.IncludedApps : _settings.ExcludedApps;

        if (!targetList.Any(a => a.Equals(raw, StringComparison.OrdinalIgnoreCase)))
        {
            targetList.Add(raw);
            _settings.Save();
            TxtNewAppFilter.Text = string.Empty;
            RefreshAppFiltersList();
        }
    }

    private void BtnRemoveAppFilter_Click(object sender, RoutedEventArgs e)
    {
        if (ListAppFilters.SelectedItem is string selected)
        {
            bool isWhitelist = string.Equals(_settings.AppFilterMode, "OnlyIncluded", StringComparison.OrdinalIgnoreCase);
            var targetList = isWhitelist ? _settings.IncludedApps : _settings.ExcludedApps;
            targetList.Remove(selected);
            _settings.Save();
            RefreshAppFiltersList();
        }
    }

    private void BtnPresetOffice_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApplyAppPreset(AppSettings.PresetOfficeApps);
        RefreshAppFiltersList();
    }

    private void BtnPresetBrowsers_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApplyAppPreset(AppSettings.PresetBrowserApps);
        RefreshAppFiltersList();
    }

    private void BtnPresetMessaging_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApplyAppPreset(AppSettings.PresetMessagingApps);
        RefreshAppFiltersList();
    }

    private void BtnPresetEditors_Click(object sender, RoutedEventArgs e)
    {
        _settings.ApplyAppPreset(AppSettings.PresetEditorApps);
        RefreshAppFiltersList();
    }

    private void BtnPresetReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.ResetAppFiltersToDefault();
        LoadSettingsToUI();
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
