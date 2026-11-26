using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PinterestWallpaper.App.Pinterest;
using PinterestWallpaper.App.Windows;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Ipc;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Rotation;
using PinterestWallpaper.Core.Storage;
using Application = System.Windows.Application;

namespace PinterestWallpaper.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\PinterestWallpaper_SingleInstance";

    private Mutex? _instanceMutex;
    private NamedPipeCommandServer? _ipcServer;
    private AppPaths? _paths;
    private ConfigStore? _configStore;
    private AppConfig? _config;
    private StateDatabase? _db;
    private RateLimitGovernor? _governor;
    private PinterestSyncService? _syncService;
    private CancellationTokenSource? _syncCts;
    private RotationService? _rotation;
    private TrayIconHost? _tray;
    private DispatcherTimer? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var startupCommand = CommandParser.Parse(e.Args);

        _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            if (startupCommand != AppCommand.None)
            {
                NamedPipeCommandServer.TrySendCommand(startupCommand);
            }

            Shutdown();
            return;
        }

        _paths = new AppPaths();
        _paths.EnsureDirectoriesExist();

        _configStore = new ConfigStore(_paths.ConfigFile);
        _config = _configStore.LoadOrCreate();

        SyncContextMenuRegistration(_config.Shell.DesktopContextMenu);

        _db = new StateDatabase(_paths.DatabaseFile);
        _governor = new RateLimitGovernor(_db);
        _syncCts = new CancellationTokenSource();
        _syncService = new PinterestSyncService(_paths, _db, _governor);

        var applier = new DesktopWallpaperApplier();
        var systemState = new WindowsSystemStateProvider();
        _rotation = new RotationService(_db, applier, systemState);

        _tray = new TrayIconHost(
            _paths,
            () => _config,
            _rotation,
            ExecuteCommand,
            OpenPinterestLogin,
            () => _ = RunSyncAsync(isManual: true),
            ToggleContextMenu,
            ReloadConfig,
            Shutdown);

        _ipcServer = new NamedPipeCommandServer(cmd => Dispatcher.BeginInvoke(() => ExecuteCommand(cmd)));
        _ipcServer.Start();

        _timer = new DispatcherTimer();
        _timer.Tick += OnRotationTick;
        ScheduleNextTick();

        if (startupCommand != AppCommand.None)
        {
            ExecuteCommand(startupCommand);
        }
    }

    private void OpenPinterestLogin()
    {
        if (_paths is null)
        {
            return;
        }

        var loginWindow = new PinterestLoginWindow(_paths.WebView2Directory);
        loginWindow.Show();
    }

    private async Task RunSyncAsync(bool isManual)
    {
        if (_syncService is null || _config is null || _syncCts is null)
        {
            return;
        }

        try
        {
            var result = await _syncService.TrySyncAsync(_config, isManual, _syncCts.Token);
            if (isManual || result.PinsAdded > 0 || result.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
            {
                _tray?.ShowNotification("Pinterest Wallpaper", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // App shutting down.
        }
        catch (Exception ex)
        {
            if (isManual)
            {
                _tray?.ShowNotification("Pinterest Sync Error", ex.Message);
            }
        }
    }

    private void ExecuteCommand(AppCommand command)
    {
        if (_rotation is null || _config is null)
        {
            return;
        }

        switch (command)
        {
            case AppCommand.Next:
                _rotation.NextWallpaper(_config, isManual: true);
                break;
            case AppCommand.Previous:
                _rotation.PreviousWallpaper(_config);
                break;
            case AppCommand.SkipAndHide:
                _rotation.SkipAndBlockCurrent(_config);
                break;
            case AppCommand.TogglePause:
                _rotation.IsPaused = !_rotation.IsPaused;
                break;
            case AppCommand.SaveToPinterest:
                SaveCurrentToPinterest();
                break;
            case AppCommand.OpenPin:
                OpenCurrentPin();
                break;
        }

        _tray?.RefreshState();
    }

    private void SaveCurrentToPinterest()
    {
        var saved = _rotation?.MarkCurrentFavorite();
        if (saved is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(saved.PinUrl))
        {
            Process.Start(new ProcessStartInfo(saved.PinUrl) { UseShellExecute = true });
        }
    }

    private void OpenCurrentPin()
    {
        var current = _rotation?.CurrentPins.FirstOrDefault();
        if (current is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(current.PinUrl))
        {
            Process.Start(new ProcessStartInfo(current.PinUrl) { UseShellExecute = true });
        }
        else if (File.Exists(current.LocalPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{current.LocalPath}\"")
            {
                UseShellExecute = true
            });
        }
    }

    private void ToggleContextMenu()
    {
        if (_config is null || _configStore is null)
        {
            return;
        }

        var enable = !ContextMenuRegistrar.IsInstalled();
        SyncContextMenuRegistration(enable);
        _config.Shell.DesktopContextMenu = enable;
        _configStore.Save(_config);
        _tray?.RefreshState();
    }

    private static void SyncContextMenuRegistration(bool enabled)
    {
        try
        {
            if (enabled)
            {
                ContextMenuRegistrar.Install();
            }
            else
            {
                ContextMenuRegistrar.Uninstall();
            }
        }
        catch
        {
            // Keep app running even if registry access is restricted.
        }
    }

    private void OnRotationTick(object? sender, EventArgs e)
    {
        if (_rotation is not null && _config is not null)
        {
            _rotation.NextWallpaper(_config, isManual: false);
            _tray?.RefreshState();
            ScheduleNextTick();

            // Check in the background whether the local pool is low (governor enforces cooldowns).
            _ = RunSyncAsync(isManual: false);
        }
    }

    private void ScheduleNextTick()
    {
        if (_timer is null || _rotation is null || _config is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Interval = _rotation.GetNextInterval(_config.Rotation);
        _timer.Start();
    }

    private void ReloadConfig()
    {
        if (_configStore is null)
        {
            return;
        }

        _config = _configStore.LoadOrCreate();
        SyncContextMenuRegistration(_config.Shell.DesktopContextMenu);
        _tray?.RefreshState();
        ScheduleNextTick();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _syncCts?.Cancel();
        _syncService?.Dispose();
        _syncCts?.Dispose();
        _ipcServer?.Dispose();
        _tray?.Dispose();
        _db?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
