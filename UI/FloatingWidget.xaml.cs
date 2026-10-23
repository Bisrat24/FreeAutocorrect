using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FreeAutocorrect.Models;
using FreeAutocorrect.Services;

namespace FreeAutocorrect.UI;

public partial class FloatingWidget : Window
{
    private List<LintItem> _currentLints = new();
    private string _activeText = string.Empty;
    private string _pendingTypo = string.Empty;
    private string _pendingFix = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _liveSuggestionTimer;

    public event Action? OnOpenAssistant;
    public event Action? OnTriggerCheck;
    public event Action<string>? OnReplaceTextInTarget;

    public FloatingWidget()
    {
        InitializeComponent();
        PositionAtBottomRight();
    }

    public void PositionAtBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 380;
        Top = workArea.Bottom - 140;
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
            SetStatus("All good! No errors found.", 0);
            var emptyText = new TextBlock
            {
                Text = "✨ Your text looks clean and error-free!",
                Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!,
                Margin = new Thickness(0, 8, 0, 8),
                FontSize = 12
            };
            SuggestionsList.Children.Add(emptyText);
            return;
        }

        SetStatus($"{lints.Count} suggestions found", lints.Count);

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
            string newText;
            if (lint.CharStart >= 0 && lint.CharEnd <= _activeText.Length && lint.CharStart <= lint.CharEnd)
            {
                newText = _activeText.Substring(0, lint.CharStart) + lint.PrimarySuggestion + _activeText.Substring(lint.CharEnd);
            }
            else if (!string.IsNullOrEmpty(lint.MatchedText))
            {
                newText = _activeText.Replace(lint.MatchedText, lint.PrimarySuggestion);
            }
            else
            {
                return;
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
        Dispatcher.Invoke(() =>
        {
            _pendingTypo = typo;
            _pendingFix = fix;

            TxtLiveSuggestion.Text = $"💡 Suggestion: '{typo}' → '{fix}'";
            LiveSuggestionBanner.Visibility = Visibility.Visible;
            Show();
            Topmost = true;

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
            TextAutomationService.ReplaceText(_pendingTypo.Length + 1, _pendingFix, ' ');
            LiveSuggestionBanner.Visibility = Visibility.Collapsed;
            _liveSuggestionTimer?.Stop();
        }
    }
}
