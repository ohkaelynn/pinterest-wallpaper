using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _savePinItem;
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
        Action openSettings,
        Action openPinterestLogin,
        Action syncNow,
        Action signOut,
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

        _savePinItem = new ToolStripMenuItem("Save / Pin to Pinterest", null, (_, _) => executeCommand(AppCommand.SaveToPinterest));
        menu.Items.Add(_savePinItem);
        menu.Items.Add("Open Current Pin in Browser", null, (_, _) => executeCommand(AppCommand.OpenPin));
        menu.Items.Add("Skip & Hide Current", null, (_, _) => executeCommand(AppCommand.SkipAndHide));

        _pauseItem = new ToolStripMenuItem("Pause Rotation", null, (_, _) => executeCommand(AppCommand.TogglePause));
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings...", null, (_, _) => openSettings());
        menu.Items.Add("Sign in to Pinterest...", null, (_, _) => openPinterestLogin());
        menu.Items.Add("Sync Now (if cooldown allows)", null, (_, _) => syncNow());
        menu.Items.Add("Sign Out & Clear Session", null, (_, _) => signOut());

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
        _savePinItem.Enabled = _rotation.CurrentPins.Count > 0;

        _pauseItem.Checked = _rotation.IsPaused;
        _pauseItem.Text = _rotation.IsPaused ? "Resume Rotation" : "Pause Rotation";

        _contextMenuItem.Checked = ContextMenuRegistrar.IsInstalled();
    }

    public void ShowNotification(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(3500, title, message, ToolTipIcon.Info);
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
            ShowNotification("Pinterest Wallpaper", $"Imported {added} valid wallpaper(s) into local cache.");
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
