using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using FreeAutocorrect.Models;

namespace FreeAutocorrect.Services;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private readonly HarperEngineService _harperEngine;

    public event Action? OnOpenAssistantRequested;
    public event Action? OnCheckSelectedRequested;
    public event Action? OnOpenSettingsRequested;
    public event Action? OnExitRequested;
    public event Action<bool>? OnToggleFloatingWidgetRequested;

    public TrayIconService(AppSettings settings, HarperEngineService harperEngine)
    {
        _settings = settings;
        _harperEngine = harperEngine;

        _notifyIcon = new NotifyIcon
        {
            Text = "FreeAutocorrect - Grammarly Alternative (Offline)",
            Visible = true,
            Icon = CreateAppIcon()
        };

        _notifyIcon.DoubleClick += (s, e) => OnOpenAssistantRequested?.Invoke();
        BuildContextMenu();
    }

    public void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("📝 Open Grammar Assistant") { Font = new Font(menu.Font, FontStyle.Bold) };
        openItem.Click += (s, e) => OnOpenAssistantRequested?.Invoke();
        menu.Items.Add(openItem);

        var checkItem = new ToolStripMenuItem("⚡ Check Selected Text (Ctrl+Shift+G)");
        checkItem.Click += (s, e) => OnCheckSelectedRequested?.Invoke();
        menu.Items.Add(checkItem);

        menu.Items.Add(new ToolStripSeparator());

        var liveAutocorrectItem = new ToolStripMenuItem("Live Autocorrect While Typing")
        {
            Checked = _settings.LiveAutocorrectEnabled,
            CheckOnClick = true
        };
        liveAutocorrectItem.CheckedChanged += (s, e) =>
        {
            _settings.LiveAutocorrectEnabled = liveAutocorrectItem.Checked;
            _settings.Save();
        };
        menu.Items.Add(liveAutocorrectItem);

        var floatingWidgetItem = new ToolStripMenuItem("Show Floating Suggestion Pill")
        {
            Checked = _settings.ShowFloatingWidget,
            CheckOnClick = true
        };
        floatingWidgetItem.CheckedChanged += (s, e) =>
        {
            _settings.ShowFloatingWidget = floatingWidgetItem.Checked;
            _settings.Save();
            OnToggleFloatingWidgetRequested?.Invoke(floatingWidgetItem.Checked);
        };
        menu.Items.Add(floatingWidgetItem);

        menu.Items.Add(new ToolStripSeparator());

        var engineStatusItem = new ToolStripMenuItem(_harperEngine.IsAvailable 
            ? "🟢 Harper Engine: Connected (Offline)" 
            : "🟡 Harper Engine: Checking...")
        {
            Enabled = false
        };
        menu.Items.Add(engineStatusItem);

        var settingsItem = new ToolStripMenuItem("⚙️ Settings");
        settingsItem.Click += (s, e) => OnOpenSettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("❌ Exit FreeAutocorrect");
        exitItem.Click += (s, e) => OnExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_settings.ShowNotificationOnCorrection)
        {
            _notifyIcon.ShowBalloonTip(2000, title, message, icon);
        }
    }

    private Icon CreateAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch { }

        // Programmatically generate a sleek tray icon (Dark cyan/emerald badge with white checkmark)
        int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // Circular gradient background
            using (var brush = new LinearGradientBrush(new System.Drawing.Point(0, 0), new System.Drawing.Point(size, size),
                System.Drawing.Color.FromArgb(16, 185, 129), System.Drawing.Color.FromArgb(5, 150, 105)))
            {
                g.FillEllipse(brush, 1, 1, size - 2, size - 2);
            }

            // Draw clean checkmark
            using var pen = new Pen(System.Drawing.Color.White, 3.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            var points = new[]
            {
                new PointF(8, 16),
                new PointF(14, 22),
                new PointF(24, 10)
            };
            g.DrawLines(pen, points);
        }

        IntPtr hIcon = bmp.GetHicon();
        var icon = Icon.FromHandle(hIcon);

        try
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string iconPath = Path.Combine(dir, "icon.ico");
            if (!File.Exists(iconPath))
            {
                using var fs = new FileStream(iconPath, FileMode.Create);
                icon.Save(fs);
            }
        }
        catch { }

        return icon;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
