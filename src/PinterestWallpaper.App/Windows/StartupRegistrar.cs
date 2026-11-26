using Microsoft.Win32;

namespace PinterestWallpaper.App.Windows;

public static class StartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PinterestWallpaper";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string val && !string.IsNullOrWhiteSpace(val);
    }

    public static void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var exe = executablePath ?? Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                key.SetValue(ValueName, $"\"{exe}\"");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
