using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private System.Windows.Threading.DispatcherTimer? _liveSuggestionTimer;
    private System.Windows.Threading.DispatcherTimer? _contractTimer;
    private System.Windows.Threading.DispatcherTimer? _hardWordTimer;

    public event Action? OnOpenAssistant;
    public event Action? OnTriggerCheck;
    public event Action<string>? OnReplaceTextInTarget;
    public event Action? OnAutoFinishRequested;

    public FloatingWidget()
    {
        InitializeComponent();
        PositionAtBottomRight();

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
    }

    public void PositionAtBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 320;
        Top = workArea.Bottom - 65;
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

    public void UpdateLints(List<LintItem> lints, string originalText)
    {
        _currentLints = lints;
        _activeText = originalText;
        SuggestionsList.Children.Clear();

        if (lints.Count == 0)
        {
            IconBadgeText.Text = "✓";
            IconOnlyView.Background = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            SetStatus("All good! No errors found.", 0);
            var emptyText = new TextBlock
            {
                Text = "✨ Your text looks clean and error-free across all lines!",
                Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!,
                Margin = new Thickness(0, 8, 0, 8),
                FontSize = 12
            };
            SuggestionsList.Children.Add(emptyText);
            return;
        }

        IconBadgeText.Text = $"⚡{lints.Count}";
        IconOnlyView.Background = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
        SetStatus($"{lints.Count} suggestions found across lines", lints.Count);

        foreach (var lint in lints)
        {
            var card = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString("#0F172A")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString(lint.FormattedBadgeColor)!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 4)
            };

            var stack = new StackPanel();

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

            var ruleName = new TextBlock
            {
                Text = lint.Rule,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#64748B")!,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(ruleName, Dock.Left);
            header.Children.Add(ruleName);

            stack.Children.Add(header);

            // Message
            var msg = new TextBlock
            {
                Text = lint.Message,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#E2E8F0")!,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(msg);

            // Suggestions
            var suggWrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
            if (!string.IsNullOrEmpty(lint.PrimarySuggestion))
            {
                var fixBtn = new Button
                {
                    Content = $"✓ {lint.PrimarySuggestion}",
                    Background = (Brush)new BrushConverter().ConvertFromString("#10B981")!,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 6, 4),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                fixBtn.Click += (s, e) => ApplySingleFix(lint);
                suggWrap.Children.Add(fixBtn);
            }

            stack.Children.Add(suggWrap);
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
            IntPtr target = _targetHwnd != IntPtr.Zero ? _targetHwnd : GetForegroundWindow();

            int dist = 0;
            if (lint.CharEnd > 0 && _activeText.Length >= lint.CharEnd)
            {
                dist = _activeText.Length - lint.CharEnd;
            }

            // Fix in the target active window
            System.Threading.Tasks.Task.Run(() =>
            {
                if (target != IntPtr.Zero)
                {
                    SetForegroundWindow(target);
                    System.Threading.Thread.Sleep(35);
                }

                int typoLen = !string.IsNullOrEmpty(typo) ? typo.Length : Math.Max(1, lint.CharEnd - lint.CharStart);
                TextAutomationService.ReplacePreviousWord(dist, typoLen, fix, '\0');
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
            OnReplaceTextInTarget?.Invoke(newText);
            UpdateLints(_currentLints, _activeText);
        }
        catch { }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
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
        UpdateLints(_currentLints, _activeText);
    }

    public void ShowLiveSuggestion(string typo, string fix)
    {
        _targetHwnd = GetForegroundWindow();

        Dispatcher.Invoke(() =>
        {
            _pendingTypo = typo;
            _pendingFix = fix;

            TxtLiveSuggestion.Text = $"💡 Suggestion: '{typo}' → '{fix}'";
            PillBorder.Padding = new Thickness(6);
            PillBorder.Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
            PillBorder.BorderThickness = new Thickness(1.5);
            LiveSuggestionBanner.Visibility = Visibility.Visible;
            Show();
            Topmost = true;

            // Retain suggestion in persistent issues list so it is NOT forgotten if user continues typing!
            if (!_currentLints.Any(l => l.MatchedText.Equals(typo, StringComparison.OrdinalIgnoreCase)))
            {
                int start = Math.Max(0, _activeText.Length - typo.Length);
                _currentLints.Add(new LintItem
                {
                    MatchedText = typo,
                    Suggestions = new List<string> { $"Replace with: \"{fix}\"" },
                    Message = $"Typo suggestion: '{typo}' → '{fix}'",
                    Category = LintCategory.Spelling,
                    Rule = "Live Typo",
                    CharStart = start,
                    CharEnd = _activeText.Length
                });
                UpdateLints(_currentLints, _activeText);
            }

            _liveSuggestionTimer?.Stop();
            _liveSuggestionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _liveSuggestionTimer.Tick += (s, e) =>
            {
                _liveSuggestionTimer?.Stop();
                LiveSuggestionBanner.Visibility = Visibility.Collapsed;
            };
            _liveSuggestionTimer.Start();
        });
    }

    private void BtnApplyLive_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingTypo) && !string.IsNullOrEmpty(_pendingFix))
        {
            string typo = _pendingTypo;
            string fix = _pendingFix;
            IntPtr target = _targetHwnd;

            LiveSuggestionBanner.Visibility = Visibility.Collapsed;
            _liveSuggestionTimer?.Stop();

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
                if (target != IntPtr.Zero)
                {
                    SetForegroundWindow(target);
                    System.Threading.Thread.Sleep(35);
                }
                TextAutomationService.ReplacePreviousWord(dist, typo.Length, fix, ' ');
            });

            var matching = _currentLints.FirstOrDefault(l => l.MatchedText.Equals(typo, StringComparison.OrdinalIgnoreCase));
            if (matching != null)
            {
                _currentLints.Remove(matching);
                UpdateLints(_currentLints, _activeText);
            }
        }
    }

    public void ShowTabCompletion(string prefix, string fullWord, string? keyBadge = null)
    {
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
                        IntPtr target = _targetHwnd;

                        System.Threading.Tasks.Task.Run(() =>
                        {
                            if (target != IntPtr.Zero)
                            {
                                SetForegroundWindow(target);
                                System.Threading.Thread.Sleep(35);
                            }
                            TextAutomationService.ReplaceText(word.Length + 1, candidateFix, ' ');
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
        }
    }

    private void ScheduleContractIfIdle()
    {
        _contractTimer?.Stop();
        _contractTimer?.Start();
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
