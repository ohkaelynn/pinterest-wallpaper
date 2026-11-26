using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pinItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _contextMenuItem;
    private readonly AppPaths _paths;
    private readonly Func<AppConfig> _getConfig;
    private readonly RotationService _rotation;

    public TrayIconHost(
        AppPaths paths,
        Func<AppConfig> getConfig,
        RotationService rotation,
        Action<AppCommand> executeCommand,
        Action toggleContextMenu,
        Action reloadConfig,
        Action exitApp)
    {
        _paths = paths;
        _getConfig = getConfig;
        _rotation = rotation;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Next Wallpaper", null, (_, _) => executeCommand(AppCommand.Next));
        menu.Items.Add("Previous Wallpaper", null, (_, _) => executeCommand(AppCommand.Previous));

        _pinItem = new ToolStripMenuItem("Pin Current Wallpaper", null, (_, _) => executeCommand(AppCommand.TogglePin));
        menu.Items.Add(_pinItem);
        menu.Items.Add("Open Current Pin", null, (_, _) => executeCommand(AppCommand.OpenPin));
        menu.Items.Add("Skip & Hide Current", null, (_, _) => executeCommand(AppCommand.SkipAndHide));

        _pauseItem = new ToolStripMenuItem("Pause Rotation", null, (_, _) => executeCommand(AppCommand.TogglePause));
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new ToolStripSeparator());
        _contextMenuItem = new ToolStripMenuItem("Desktop Right-Click Menu", null, (_, _) => toggleContextMenu());
        menu.Items.Add(_contextMenuItem);
        menu.Items.Add("Import Local Folder...", null, (_, _) => ImportLocalFolder());
        menu.Items.Add("Open Config File", null, (_, _) => OpenPath(_paths.ConfigFile));
        menu.Items.Add("Reload Config", null, (_, _) => reloadConfig());
        menu.Items.Add("Open Cache Folder", null, (_, _) => OpenPath(_paths.CacheDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "Pinterest Wallpaper Swapper",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => executeCommand(AppCommand.Next);
        RefreshState();
    }

    public void RefreshState()
    {
        _pinItem.Checked = _rotation.IsCurrentPinned;
        _pinItem.Text = _rotation.IsCurrentPinned ? "Unpin Current Wallpaper" : "Pin Current Wallpaper";
        _pinItem.Enabled = _rotation.CurrentPins.Count > 0;

        _pauseItem.Checked = _rotation.IsPaused;
        _pauseItem.Text = _rotation.IsPaused ? "Resume Rotation" : "Pause Rotation";

        _contextMenuItem.Checked = ContextMenuRegistrar.IsInstalled();
    }

    private void ImportLocalFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select a folder of local wallpaper images to import into the cache"
        };

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            var added = _rotation.ImportLocalFolder(dialog.SelectedPath, _getConfig().Filters, _paths.CacheDirectory);
            _notifyIcon.ShowBalloonTip(
                3000,
                "Pinterest Wallpaper",
                $"Imported {added} valid wallpaper(s) into local cache.",
                ToolTipIcon.Info);
        }
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
