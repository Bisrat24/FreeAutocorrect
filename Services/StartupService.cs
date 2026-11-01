using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FreeAutocorrect.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "FreeAutocorrect";

    /// <summary>
    /// Checks whether FreeAutocorrect is registered to launch on Windows startup.
    /// </summary>
    public static bool IsLaunchOnStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key == null) return false;
            var val = key.GetValue(AppValueName) as string;
            return !string.IsNullOrEmpty(val);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("Startup", $"IsLaunchOnStartupEnabled error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enables or disables FreeAutocorrect in the Windows Run registry key (HKCU).
    /// </summary>
    public static bool SetLaunchOnStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    key.SetValue(AppValueName, $"\"{exePath}\"");
                    AppLogger.Info("Startup", $"Enabled launch on Windows startup: {exePath}");
                    return true;
                }
                return false;
            }
            else
            {
                if (key.GetValue(AppValueName) != null)
                {
                    key.DeleteValue(AppValueName, false);
                    AppLogger.Info("Startup", "Disabled launch on Windows startup.");
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("Startup", $"SetLaunchOnStartup error: {ex.Message}");
            return false;
        }
    }
}
