using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly AppPaths _paths;
    private readonly Func<AppConfig> _getConfig;
    private readonly RotationService _rotation;

    public TrayIconHost(
        AppPaths paths,
        Func<AppConfig> getConfig,
        RotationService rotation,
        Action reloadConfig,
        Action exitApp)
    {
        _paths = paths;
        _getConfig = getConfig;
        _rotation = rotation;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Next Wallpaper", null, (_, _) => _rotation.NextWallpaper(_getConfig(), isManual: true));
        menu.Items.Add("Previous Wallpaper", null, (_, _) => _rotation.PreviousWallpaper(_getConfig()));
        menu.Items.Add("Skip & Hide Current", null, (_, _) => _rotation.SkipAndBlockCurrent(_getConfig()));

        _pauseItem = new ToolStripMenuItem("Pause Rotation", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new ToolStripSeparator());
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

        _notifyIcon.DoubleClick += (_, _) => _rotation.NextWallpaper(_getConfig(), isManual: true);
    }

    private void TogglePause()
    {
        _rotation.IsPaused = !_rotation.IsPaused;
        _pauseItem.Text = _rotation.IsPaused ? "Resume Rotation" : "Pause Rotation";
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
