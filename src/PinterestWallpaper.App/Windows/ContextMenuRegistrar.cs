using Microsoft.Win32;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public static class ContextMenuRegistrar
{
    private const string RootKeyPath = @"Software\Classes\DesktopBackground\Shell\PinterestWallpaper";

    private static readonly (string KeyName, string Label, AppCommand Command, bool SeparatorBefore)[] MenuItems =
    [
        ("01_Next", "Next Wallpaper", AppCommand.Next, false),
        ("02_Prev", "Previous Wallpaper", AppCommand.Previous, false),
        ("03_Pin", "Pin / Unpin Current Wallpaper", AppCommand.TogglePin, true),
        ("04_Open", "Open Pin in Browser", AppCommand.OpenPin, false),
        ("05_Skip", "Skip && Hide This Wallpaper", AppCommand.SkipAndHide, true),
        ("06_Pause", "Pause / Resume Rotation", AppCommand.TogglePause, false)
    ];

    public static bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RootKeyPath);
        return key is not null;
    }

    public static void Install(string? executablePath = null)
    {
        var exe = executablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        // Clean any stale subkeys first so menu order stays exact.
        Uninstall();

        using var root = Registry.CurrentUser.CreateSubKey(RootKeyPath);
        root.SetValue("MUIVerb", "Pinterest Wallpaper");
        root.SetValue("SubCommands", string.Empty);
        root.SetValue("Icon", $"\"{exe}\",0");

        using var shellKey = root.CreateSubKey("shell");
        foreach (var (keyName, label, command, separatorBefore) in MenuItems)
        {
            using var itemKey = shellKey.CreateSubKey(keyName);
            itemKey.SetValue("MUIVerb", label);
            if (separatorBefore)
            {
                itemKey.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord); // ECF_SEPARATORBEFORE
            }

            using var cmdKey = itemKey.CreateSubKey("command");
            var arg = CommandParser.ToArgument(command);
            cmdKey.SetValue(string.Empty, $"\"{exe}\" {arg}");
        }
    }

    public static void Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(RootKeyPath, throwOnMissingSubKey: false);
    }
}
