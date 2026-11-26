using System.Drawing;
using System.Windows.Forms;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public sealed class TrayIconHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly ToolStripMenuItem _likePinItem;
    private readonly ToolStripMenuItem _savePinItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly AppPaths _paths;
    private readonly Func<AppConfig> _getConfig;
    private readonly RotationService _rotation;

    public TrayIconHost(
        AppPaths paths,
        Func<AppConfig> getConfig,
        RotationService rotation,
        Action<AppCommand> executeCommand,
        Action openSettings,
        Action syncNow,
        Action exitApp)
    {
        _paths = paths;
        _getConfig = getConfig;
        _rotation = rotation;
        _appIcon = LoadAppIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Next Wallpaper", null, (_, _) => executeCommand(AppCommand.Next));
        menu.Items.Add("Previous Wallpaper", null, (_, _) => executeCommand(AppCommand.Previous));
        menu.Items.Add(new ToolStripSeparator());

        _likePinItem = new ToolStripMenuItem("Like on Pinterest", null, (_, _) => executeCommand(AppCommand.LikeOnPinterest));
        menu.Items.Add(_likePinItem);
        _savePinItem = new ToolStripMenuItem("Save to Board", null, (_, _) => executeCommand(AppCommand.SaveToPinterest));
        menu.Items.Add(_savePinItem);
        menu.Items.Add("Open in Browser", null, (_, _) => executeCommand(AppCommand.OpenPin));
        menu.Items.Add("Skip & Hide", null, (_, _) => executeCommand(AppCommand.SkipAndHide));
        menu.Items.Add(new ToolStripSeparator());

        _pauseItem = new ToolStripMenuItem("Pause Rotation", null, (_, _) => executeCommand(AppCommand.TogglePause));
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Sync Now", null, (_, _) => syncNow());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Settings...", null, (_, _) => openSettings());
        menu.Items.Add("Import Local Folder...", null, (_, _) => ImportLocalFolder());
        menu.Items.Add("Exit", null, (_, _) => exitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "Pinterest Wallpaper",
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => executeCommand(AppCommand.Next);
        RefreshState();
    }

    public void RefreshState()
    {
        var hasCurrent = _rotation.CurrentPins.Count > 0;
        _likePinItem.Enabled = hasCurrent;
        _savePinItem.Enabled = hasCurrent;

        _pauseItem.Checked = _rotation.IsPaused;
        _pauseItem.Text = _rotation.IsPaused ? "Resume Rotation" : "Pause Rotation";
    }

    public void ShowNotification(string title, string message)
    {
        _notifyIcon.ShowBalloonTip(3500, title, message, ToolTipIcon.Info);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Fallback below.
        }

        return (Icon)SystemIcons.Application.Clone();
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

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
    }
}
