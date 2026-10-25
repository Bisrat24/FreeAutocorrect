using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FreeAutocorrect.Models;
using FreeAutocorrect.Services;

namespace FreeAutocorrect.UI;

public partial class FloatingWidget : Window
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private List<LintItem> _currentLints = new();
    private string _activeText = string.Empty;
    private string _pendingTypo = string.Empty;
    private string _pendingFix = string.Empty;
    private string _pendingAutoFinishPrefix = string.Empty;
    private string _pendingAutoFinishWord = string.Empty;
    private IntPtr _targetHwnd = IntPtr.Zero;
    private double _savedLeft = -1;
    private double _savedTop = -1;
    private bool _isDragging = false;
    private System.Windows.Threading.DispatcherTimer? _liveSuggestionTimer;
    private System.Windows.Threading.DispatcherTimer? _contractTimer;
    private System.Windows.Threading.DispatcherTimer? _hardWordTimer;
    private System.Windows.Threading.DispatcherTimer? _autoDockTimer;

    public event Action? OnOpenAssistant;
    public event Action? OnTriggerCheck;
    public event Action<string>? OnReplaceTextInTarget;
    public event Action? OnAutoFinishRequested;

    private readonly AppSettings _settings;
    private readonly Func<IntPtr>? _targetWindowProvider;
    private bool _isTextSelectedFromApp = false;

    public FloatingWidget(AppSettings settings, Func<IntPtr>? targetWindowProvider = null)
    {
        _settings = settings;
        _targetWindowProvider = targetWindowProvider;
        InitializeComponent();
        PositionAtTopLeft();

        // Start in minimized transparent icon mode
        PillBorder.Padding = new Thickness(0);
        PillBorder.Background = Brushes.Transparent;
        PillBorder.BorderThickness = new Thickness(0);

        _contractTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _contractTimer.Tick += (s, e) =>
        {
            _contractTimer.Stop();
            ContractToIcon();
        };

        _autoDockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _autoDockTimer.Tick += (s, e) =>
        {
            _autoDockTimer.Stop();
            AutoDockToTopLeft();
        };

        if (!_settings.ShowFloatingWidget)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
        catch { }
        EnsureAnchoredPosition();
    }

    public void PositionAtTopLeft()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + 20;
        Top = workArea.Top + 20;
        _savedLeft = Left;
        _savedTop = Top;
    }

    public void AnimateDockToTopLeft()
    {
        var workArea = SystemParameters.WorkArea;
        double targetLeft = workArea.Left + 20;
        double targetTop = workArea.Top + 20;

        if (Math.Abs(Left - targetLeft) < 3 && Math.Abs(Top - targetTop) < 3)
        {
            Left = targetLeft;
            Top = targetTop;
            _savedLeft = targetLeft;
            _savedTop = targetTop;
            return;
        }

        var animLeft = new DoubleAnimation
        {
            To = targetLeft,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var animTop = new DoubleAnimation
        {
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        animLeft.Completed += (s, e) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = targetLeft;
            _savedLeft = targetLeft;
        };
        animTop.Completed += (s, e) =>
        {
            BeginAnimation(TopProperty, null);
            Top = targetTop;
            _savedTop = targetTop;
        };

        BeginAnimation(LeftProperty, animLeft);
        BeginAnimation(TopProperty, animTop);
    }

    public void AutoDockToTopLeft()
    {
        if (_isDragging) return;
        if (ExpandedView.Visibility == Visibility.Visible) return;
        if (IsMouseOver)
        {
            ScheduleAutoDockToTopLeft();
            return;
        }

        ContractToIcon();
        AnimateDockToTopLeft();
    }

    public void ScheduleAutoDockToTopLeft()
    {
        _autoDockTimer?.Stop();
        _autoDockTimer?.Start();
    }

    public void EnsureAnchoredPosition()
    {
        var workArea = SystemParameters.WorkArea;
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            PositionAtTopLeft();
            return;
        }

        // Clamp within screen work area
        double w = Math.Max(ActualWidth, 30);
        double h = Math.Max(ActualHeight, 30);
        if (Left < workArea.Left + 10) Left = workArea.Left + 20;
        if (Top < workArea.Top + 10) Top = workArea.Top + 20;
        if (Left + w > workArea.Right - 10) Left = workArea.Right - w - 20;
        if (Top + h > workArea.Bottom - 10) Top = workArea.Bottom - h - 20;
        _savedLeft = Left;
        _savedTop = Top;
    }

    public void SetStatus(string text, int errorCount = 0)
    {
        StatusText.Text = text;
        if (errorCount > 0)
        {
            ExpandButton.Content = $"⚡ Fix {errorCount} issues";
        }
        else
        {
            ExpandButton.Content = "🔍 Check / Expand";
        }
    }

    public IntPtr GetTargetAppWindow()
    {
        if (_targetWindowProvider != null)
        {
            try
            {
                IntPtr hwnd = _targetWindowProvider();
                if (hwnd != IntPtr.Zero) return hwnd;
            }
            catch { }
        }
        if (_targetHwnd != IntPtr.Zero) return _targetHwnd;
        return GetForegroundWindow();
    }

    public void UpdateLints(List<LintItem> lints, string originalText, bool isSelectedText = false)
    {
        _currentLints = lints;
        _activeText = originalText;
        _isTextSelectedFromApp = isSelectedText;
        SuggestionsList.Children.Clear();
        TxtExpandedCount.Text = lints.Count.ToString();

        // Dismiss live typo banner if user fixed the typo manually
        if (!string.IsNullOrEmpty(_pendingTypo) && !lints.Any(l => string.Equals(l.MatchedText, _pendingTypo, StringComparison.OrdinalIgnoreCase)))
        {
            DismissLiveSuggestion(_pendingTypo);
        }

        // Dismiss hard word banner if user fixed the word manually
        if (HardWordBanner.Visibility == Visibility.Visible && !string.IsNullOrEmpty(TxtHardWord.Text))
        {
            if (!lints.Any(l => string.Equals(l.MatchedText, TxtHardWord.Text, StringComparison.OrdinalIgnoreCase)))
            {
                DismissHardWord(TxtHardWord.Text);
            }
        }

        if (!_settings.ShowFloatingWidget)
        {
            Hide();
            return;
        }

        if (lints.Count == 0)
        {
            IconBadgeText.Text = "✓";
            IconOnlyView.Background = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            SetStatus("All good! No errors found.", 0);
            BadgeExpandedCount.Background = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            var emptyText = new TextBlock
            {
                Text = "✨ Your text looks clean and error-free across all lines!",
                Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!,
                Margin = new Thickness(0, 8, 0, 8),
                FontSize = 12,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };
            SuggestionsList.Children.Add(emptyText);
            return;
        }

        IconBadgeText.Text = $"⚡{lints.Count}";
        IconOnlyView.Background = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
        BadgeExpandedCount.Background = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
        SetStatus($"{lints.Count} suggestions found across lines", lints.Count);

        foreach (var lint in lints)
        {
            var card = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString("#0F172A")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#334155")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var stack = new StackPanel();

            // Header: Category badge + dismiss button
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

            var dismissBtn = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#64748B")!,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Ignore this suggestion"
            };
            DockPanel.SetDock(dismissBtn, Dock.Right);
            var capturedLint = lint;
            dismissBtn.Click += (s, e) =>
            {
                _currentLints.Remove(capturedLint);
                UpdateLints(_currentLints, _activeText);
            };
            header.Children.Add(dismissBtn);

            stack.Children.Add(header);

            // Visual Diff Row: typo (strikethrough) -> fix (green)
            if (!string.IsNullOrEmpty(lint.MatchedText) && !string.IsNullOrEmpty(lint.PrimarySuggestion))
            {
                var diffRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
                var typoBorder = new Border
                {
                    Background = (Brush)new BrushConverter().ConvertFromString("#3F1D24")!,
                    BorderBrush = (Brush)new BrushConverter().ConvertFromString("#EF4444")!,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 6, 0)
                };
                var typoStack = new StackPanel { Orientation = Orientation.Vertical };
                typoStack.Children.Add(new TextBlock
                {
                    Text = lint.MatchedText,
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#FCA5A5")!,
                    TextDecorations = TextDecorations.Strikethrough,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 12
                });
                typoStack.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M 0,1 Q 2,0 4,1 T 8,1 T 12,1 T 16,1 T 20,1 T 24,1 T 28,1 T 32,1 T 36,1 T 40,1 T 44,1 T 48,1 T 52,1 T 56,1 T 60,1 T 64,1 T 68,1 T 72,1 T 76,1 T 80,1"),
                    Stroke = (Brush)new BrushConverter().ConvertFromString("#EF4444")!,
                    StrokeThickness = 1.2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Margin = new Thickness(0, 1, 0, 0)
                });
                typoBorder.Child = typoStack;
                diffRow.Children.Add(typoBorder);

                var arrow = new TextBlock
                {
                    Text = "➔",
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!,
                    FontSize = 12,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                diffRow.Children.Add(arrow);

                var fixBorder = new Border
                {
                    Background = (Brush)new BrushConverter().ConvertFromString("#064E3B")!,
                    BorderBrush = (Brush)new BrushConverter().ConvertFromString("#10B981")!,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2)
                };
                fixBorder.Child = new TextBlock
                {
                    Text = lint.PrimarySuggestion,
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#6EE7B7")!,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12
                };
                diffRow.Children.Add(fixBorder);

                stack.Children.Add(diffRow);
            }

            // Explanation Message
            var msg = new TextBlock
            {
                Text = lint.Message,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#CBD5E1")!,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(msg);

            // Replace action button
            if (!string.IsNullOrEmpty(lint.PrimarySuggestion))
            {
                var fixBtn = new Button
                {
                    Content = $"✓ Replace with \"{lint.PrimarySuggestion}\"",
                    Background = (Brush)new BrushConverter().ConvertFromString("#10B981")!,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                fixBtn.Resources.Add(typeof(Border), new Style(typeof(Border))
                {
                    Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) }
                });
                fixBtn.Click += (s, e) => ApplySingleFix(lint);
                stack.Children.Add(fixBtn);
            }

            card.Child = stack;
            SuggestionsList.Children.Add(card);
        }
    }

    private void ApplySingleFix(LintItem lint)
    {
        if (string.IsNullOrEmpty(_activeText) || string.IsNullOrEmpty(lint.PrimarySuggestion)) return;

        try
        {
            string typo = lint.MatchedText;
            string fix = lint.PrimarySuggestion;
            IntPtr target = GetTargetAppWindow();

            int dist = 0;
            if (lint.CharEnd > 0 && _activeText.Length >= lint.CharEnd)
            {
                dist = _activeText.Length - lint.CharEnd;
            }

            int typoLen = !string.IsNullOrEmpty(typo) ? typo.Length : Math.Max(1, lint.CharEnd - lint.CharStart);

            // Fix in the target active window
            System.Threading.Tasks.Task.Run(() =>
            {
                TextAutomationService.ReplacePreviousWord(dist, typoLen, fix, '\0', target);
            });

            // Update active text and remove fixed lint
            string newText;
            if (lint.CharStart >= 0 && lint.CharEnd <= _activeText.Length && lint.CharStart <= lint.CharEnd)
            {
                newText = _activeText.Substring(0, lint.CharStart) + fix + _activeText.Substring(lint.CharEnd);
            }
            else if (!string.IsNullOrEmpty(lint.MatchedText))
            {
                newText = _activeText.Replace(lint.MatchedText, fix);
            }
            else
            {
                newText = _activeText;
            }

            _activeText = newText;
            _currentLints.Remove(lint);
            UpdateLints(_currentLints, _activeText, _isTextSelectedFromApp);
        }
        catch { }
    }

    protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _autoDockTimer?.Stop();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ScheduleAutoDockToTopLeft();
    }

    private void PillBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
        {
            return; // Clicked a button (Apply, Dismiss, Finish, etc.), do not drag!
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);
                _autoDockTimer?.Stop();
                _isDragging = true;
                DragMove();
                _savedLeft = Left;
                _savedTop = Top;
            }
            catch { }
            finally
            {
                _isDragging = false;
                EnsureAnchoredPosition();
                ScheduleAutoDockToTopLeft();
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExpandedView.Visibility == Visibility.Visible)
        {
            ExpandedView.Visibility = Visibility.Collapsed;
        }
        else
        {
            ExpandedView.Visibility = Visibility.Visible;
            OnTriggerCheck?.Invoke();
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        ExpandedView.Visibility = Visibility.Collapsed;
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OpenAssistant_Click(object sender, RoutedEventArgs e)
    {
        OnOpenAssistant?.Invoke();
        ExpandedView.Visibility = Visibility.Collapsed;
    }

    private void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_currentLints.Count == 0 || string.IsNullOrEmpty(_activeText)) return;

        // Apply fixes in reverse order of span
        var sortedLints = _currentLints
            .Where(l => !string.IsNullOrEmpty(l.PrimarySuggestion))
            .OrderByDescending(l => l.CharStart)
            .ToList();

        if (sortedLints.Count == 0) return;

        IntPtr target = GetTargetAppWindow();

        if (_isTextSelectedFromApp)
        {
            // Text was explicitly selected by user via hotkey check; replace selection via clipboard Ctrl+V
            string updated = _activeText;
            foreach (var l in sortedLints)
            {
                if (l.CharStart >= 0 && l.CharEnd <= updated.Length && l.CharStart <= l.CharEnd)
                {
                    updated = updated.Substring(0, l.CharStart) + l.PrimarySuggestion + updated.Substring(l.CharEnd);
                }
            }

            _activeText = updated;
            _currentLints.Clear();
            OnReplaceTextInTarget?.Invoke(updated);
            UpdateLints(_currentLints, _activeText, true);
        }
        else
        {
            // User was actively typing in active app; replace each typo individually from nearest to furthest
            // NEVER dump the rolling buffer via Ctrl+V (which adds a bunch of words)!
            var lintsToFix = new List<LintItem>(sortedLints);
            System.Threading.Tasks.Task.Run(() =>
            {
                int runningLength = _activeText.Length;
                foreach (var l in lintsToFix)
                {
                    string typo = l.MatchedText;
                    string fix = l.PrimarySuggestion!;
                    int dist = 0;
                    if (l.CharEnd > 0 && runningLength >= l.CharEnd)
                    {
                        dist = runningLength - l.CharEnd;
                    }

                    int typoLen = !string.IsNullOrEmpty(typo) ? typo.Length : Math.Max(1, l.CharEnd - l.CharStart);
                    TextAutomationService.ReplacePreviousWord(dist, typoLen, fix, '\0', target);
                    runningLength += (fix.Length - typoLen);
                    System.Threading.Thread.Sleep(35);
                }
            });

            _currentLints.Clear();
            UpdateLints(_currentLints, _activeText, false);
        }
    }

    public void ShowLiveSuggestion(string typo, string fix)
    {
        if (!_settings.ShowFloatingWidget) return;
        _targetHwnd = GetForegroundWindow();

        Dispatcher.Invoke(() =>
        {
            _pendingTypo = typo;
            _pendingFix = fix;

            TxtLiveTypo.Text = typo;
            TxtLiveFix.Text = fix;

            PillBorder.Padding = new Thickness(6);
            PillBorder.Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
            PillBorder.BorderThickness = new Thickness(1.5);
            LiveSuggestionBanner.Visibility = Visibility.Visible;
            Show();
            Topmost = true;
            EnsureAnchoredPosition();

            _liveSuggestionTimer?.Stop();
            _liveSuggestionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _liveSuggestionTimer.Tick += (s, e) =>
            {
                _liveSuggestionTimer?.Stop();
                LiveSuggestionBanner.Visibility = Visibility.Collapsed;
                ScheduleContractIfIdle();
            };
            _liveSuggestionTimer.Start();
        });
    }

    private void BtnDismissLive_Click(object sender, RoutedEventArgs e)
    {
        DismissLiveSuggestion();
    }

    public void DismissLiveSuggestion(string? typo = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(typo) || string.Equals(_pendingTypo, typo, StringComparison.OrdinalIgnoreCase))
            {
                _liveSuggestionTimer?.Stop();
                _pendingTypo = string.Empty;
                _pendingFix = string.Empty;
                if (LiveSuggestionBanner.Visibility != Visibility.Collapsed)
                {
                    LiveSuggestionBanner.Visibility = Visibility.Collapsed;
                    ScheduleContractIfIdle();
                }
            }
        });
    }

    public void DismissHardWord(string? word = null)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(word) || string.Equals(TxtHardWord.Text, word, StringComparison.OrdinalIgnoreCase))
            {
                _hardWordTimer?.Stop();
                if (HardWordBanner.Visibility != Visibility.Collapsed)
                {
                    HardWordBanner.Visibility = Visibility.Collapsed;
                    ScheduleContractIfIdle();
                }
            }
        });
    }

    public void DismissAllBanners()
    {
        Dispatcher.Invoke(() =>
        {
            _liveSuggestionTimer?.Stop();
            _hardWordTimer?.Stop();
            _pendingTypo = string.Empty;
            _pendingFix = string.Empty;
            LiveSuggestionBanner.Visibility = Visibility.Collapsed;
            HardWordBanner.Visibility = Visibility.Collapsed;
            TabCompletionBanner.Visibility = Visibility.Collapsed;
            ScheduleContractIfIdle();
        });
    }

    private void BtnApplyLive_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingTypo) && !string.IsNullOrEmpty(_pendingFix))
        {
            string typo = _pendingTypo;
            string fix = _pendingFix;
            IntPtr target = GetTargetAppWindow();

            LiveSuggestionBanner.Visibility = Visibility.Collapsed;
            _liveSuggestionTimer?.Stop();
            ScheduleContractIfIdle();

            // Calculate distance in case user continued typing other words
            int dist = 0;
            if (!string.IsNullOrEmpty(_activeText))
            {
                int idx = _activeText.LastIndexOf(typo, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    dist = _activeText.Length - (idx + typo.Length);
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                TextAutomationService.ReplacePreviousWord(dist, typo.Length, fix, ' ', target);
            });

            var matching = _currentLints.FirstOrDefault(l => l.MatchedText.Equals(typo, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
            {
                _currentLints.Remove(matching);
                UpdateLints(_currentLints, _activeText, _isTextSelectedFromApp);
            }
        }
    }

    public void ShowTabCompletion(string prefix, string fullWord, string? keyBadge = null)
    {
        if (!_settings.ShowFloatingWidget || !_settings.TabCompletionEnabled) return;
        _targetHwnd = GetForegroundWindow();

        Dispatcher.Invoke(() =>
        {
            _pendingAutoFinishPrefix = prefix;
            _pendingAutoFinishWord = fullWord;

            TxtTabCompletion.Text = $"✨ {prefix} ➔ {fullWord}";
            if (!string.IsNullOrEmpty(keyBadge))
            {
                TxtAutoFinishKeyBadge.Text = $"Press {keyBadge}";
            }
            PillBorder.Padding = new Thickness(6);
            PillBorder.Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
            PillBorder.BorderThickness = new Thickness(1.5);
            TabCompletionBanner.Visibility = Visibility.Visible;
            Show();
            Topmost = true;
            EnsureAnchoredPosition();
        });
    }

    private void BtnApplyAutoFinish_Click(object sender, RoutedEventArgs e)
    {
        TabCompletionBanner.Visibility = Visibility.Collapsed;
        ScheduleContractIfIdle();
        OnAutoFinishRequested?.Invoke();
    }

    public void HideTabCompletion()
    {
        Dispatcher.Invoke(() =>
        {
            if (TabCompletionBanner.Visibility != Visibility.Collapsed)
            {
                TabCompletionBanner.Visibility = Visibility.Collapsed;
                ScheduleContractIfIdle();
            }
        });
    }

    public void ShowHardWord(string word, List<string> candidates)
    {
        if (!_settings.ShowFloatingWidget) return;
        _targetHwnd = GetForegroundWindow();

        Dispatcher.Invoke(() =>
        {
            TxtHardWord.Text = word;
            HardWordCandidatesPanel.Children.Clear();

            if (candidates != null && candidates.Count > 0)
            {
                foreach (var cand in candidates)
                {
                    string candidateFix = cand;
                    var btn = new Button
                    {
                        Content = $"➜ {cand}",
                        Background = (Brush)new BrushConverter().ConvertFromString("#DC2626")!,
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.SemiBold,
                        FontSize = 11,
                        Padding = new Thickness(8, 2, 8, 2),
                        Margin = new Thickness(0, 2, 6, 2),
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand
                    };
                    btn.Resources.Add(typeof(Border), new Style(typeof(Border))
                    {
                        Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(6)) }
                    });

                    btn.Click += (s, e) =>
                    {
                        HardWordBanner.Visibility = Visibility.Collapsed;
                        _hardWordTimer?.Stop();
                        IntPtr target = GetTargetAppWindow();

                        System.Threading.Tasks.Task.Run(() =>
                        {
                            TextAutomationService.ReplaceText(word.Length + 1, candidateFix, ' ', target);
                        });
                        ScheduleContractIfIdle();
                    };

                    HardWordCandidatesPanel.Children.Add(btn);
                }
            }
            else
            {
                var noCandText = new TextBlock
                {
                    Text = "No direct suggestions. Press Ctrl+Shift+G to check in Assistant.",
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#FCA5A5")!,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0)
                };
                HardWordCandidatesPanel.Children.Add(noCandText);
            }

            HardWordBanner.Visibility = Visibility.Visible;
            PillBorder.Padding = new Thickness(6);
            PillBorder.Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
            PillBorder.BorderThickness = new Thickness(1.5);
            Show();
            Topmost = true;
            EnsureAnchoredPosition();

            _hardWordTimer?.Stop();
            _hardWordTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _hardWordTimer.Tick += (s, e) =>
            {
                _hardWordTimer.Stop();
                HardWordBanner.Visibility = Visibility.Collapsed;
                ScheduleContractIfIdle();
            };
            _hardWordTimer.Start();
        });
    }

    private void BtnDismissHardWord_Click(object sender, RoutedEventArgs e)
    {
        _hardWordTimer?.Stop();
        HardWordBanner.Visibility = Visibility.Collapsed;
        ScheduleContractIfIdle();
    }

    private void PillBorder_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _contractTimer?.Stop();
        ExpandToFull();
    }

    private void PillBorder_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ScheduleContractIfIdle();
    }

    private void IconOnlyView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contractTimer?.Stop();
        ExpandToFull();
    }

    private void ExpandToFull()
    {
        IconOnlyView.Visibility = Visibility.Collapsed;
        CollapsedView.Visibility = Visibility.Visible;
        PillBorder.Padding = new Thickness(6);
        PillBorder.Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
        PillBorder.BorderThickness = new Thickness(1.5);
    }

    private void ContractToIcon()
    {
        if (ExpandedView.Visibility != Visibility.Visible &&
            LiveSuggestionBanner.Visibility != Visibility.Visible &&
            TabCompletionBanner.Visibility != Visibility.Visible &&
            HardWordBanner.Visibility != Visibility.Visible)
        {
            CollapsedView.Visibility = Visibility.Collapsed;
            IconOnlyView.Visibility = Visibility.Visible;
            PillBorder.Padding = new Thickness(0);
            PillBorder.Background = Brushes.Transparent;
            PillBorder.BorderThickness = new Thickness(0);
            EnsureAnchoredPosition();
        }
    }

    private void ScheduleContractIfIdle()
    {
        _contractTimer?.Stop();
        _contractTimer?.Start();
        ScheduleAutoDockToTopLeft();
    }

    private void ContextOpenAssistant_Click(object sender, RoutedEventArgs e)
    {
        OnOpenAssistant?.Invoke();
    }

    private void ContextCheckText_Click(object sender, RoutedEventArgs e)
    {
        ExpandToFull();
        ExpandedView.Visibility = Visibility.Visible;
        OnTriggerCheck?.Invoke();
    }

    private void ContextMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void MinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
