using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FreeAutocorrect.Services;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FreeAutocorrect"
    );
    public static readonly string LogFilePath = Path.Combine(LogDir, "autocorrect.log");
    private const long MaxLogSizeBytes = 10 * 1024 * 1024; // 10 MB

    static AppLogger()
    {
        try
        {
            if (!Directory.Exists(LogDir))
            {
                Directory.CreateDirectory(LogDir);
            }
        }
        catch { }
    }

    public static void Info(string category, string message) => Write("INFO", category, message);
    public static void Debug(string category, string message) => Write("DEBUG", category, message);
    public static void Warn(string category, string message) => Write("WARN", category, message);
    public static void Error(string category, string message, Exception? ex = null)
    {
        string text = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Write("ERROR", category, text);
    }

    private static void Write(string level, string category, string message)
    {
        try
        {
            lock (_lock)
            {
                var fileInfo = new FileInfo(LogFilePath);
                if (fileInfo.Exists && fileInfo.Length > MaxLogSizeBytes)
                {
                    string backup = Path.Combine(LogDir, "autocorrect.old.log");
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(LogFilePath, backup);
                }

                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] [{category}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch { }
    }

    public static string GetRecentLogLines(int maxLines = 40)
    {
        try
        {
            lock (_lock)
            {
                if (!File.Exists(LogFilePath)) return "No log entries recorded yet.";
                var lines = File.ReadAllLines(LogFilePath, Encoding.UTF8);
                if (lines.Length <= maxLines) return string.Join(Environment.NewLine, lines);
                var subset = new string[maxLines];
                Array.Copy(lines, lines.Length - maxLines, subset, 0, maxLines);
                return string.Join(Environment.NewLine, subset);
            }
        }
        catch (Exception ex)
        {
            return $"Error reading log: {ex.Message}";
        }
    }

    public static void OpenLogFile()
    {
        try
        {
            if (!File.Exists(LogFilePath))
            {
                File.WriteAllText(LogFilePath, $"FreeAutocorrect Analysis Log initialized at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}");
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = LogFilePath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public static void ClearLog()
    {
        try
        {
            lock (_lock)
            {
                File.WriteAllText(LogFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO ] [System] Log file cleared by user.{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}
