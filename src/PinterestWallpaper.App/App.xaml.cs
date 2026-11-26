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
    private SettingsWindow? _settingsWindow;
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

        SyncShellIntegrations(_config.Shell);

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
            OpenSettings,
            () => _ = RunSyncAsync(isManual: true),
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
        else if (!Directory.Exists(_paths.WebView2Directory) && _db.GetAvailablePins().Count == 0)
        {
            // First launch: open Settings directly on Account & Status so the user can sign in right away.
            OpenSettings(selectAccountTab: true);
        }

        MemoryTrimmer.TrimIdleMemory();
    }

    private void OpenSettings() => OpenSettings(selectAccountTab: false);

    private void OpenSettings(bool selectAccountTab)
    {
        if (_config is null || _db is null || _governor is null)
        {
            return;
        }

        if (_settingsWindow is not null && _settingsWindow.IsLoaded)
        {
            if (selectAccountTab)
            {
                _settingsWindow.SelectAccountTab();
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _paths!,
            _config,
            _db,
            _governor,
            _syncService!,
            SaveConfigFromUi,
            OpenPinterestLogin,
            () => _ = RunSyncAsync(isManual: true),
            () => SignOutAsync(showNotification: false));
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            MemoryTrimmer.TrimIdleMemory();
        };

        if (selectAccountTab)
        {
            _settingsWindow.SelectAccountTab();
        }

        _settingsWindow.Show();
    }

    private void SaveConfigFromUi(AppConfig updated)
    {
        if (_configStore is null)
        {
            return;
        }

        _configStore.Save(updated);
        _config = _configStore.LoadOrCreate();
        SyncShellIntegrations(_config.Shell);
        _tray?.RefreshState();
        ScheduleNextTick();
    }

    private void OpenPinterestLogin()
    {
        if (_paths is null)
        {
            return;
        }

        var loginWindow = new PinterestLoginWindow(_paths.WebView2Directory);
        loginWindow.Closed += (_, _) =>
        {
            if (loginWindow.SignedInDetected && _db is not null)
            {
                _db.SetStateValue(PinterestSyncService.SessionStatusKey, "signed_in");
                _settingsWindow?.RefreshStatus();
            }

            MemoryTrimmer.TrimIdleMemory();
        };
        loginWindow.Show();
    }

    private async Task<bool> SignOutAsync(bool showNotification)
    {
        if (_syncService is null || _syncCts is null)
        {
            return false;
        }

        var ok = await _syncService.TryClearSessionAsync(_syncCts.Token);
        if (showNotification)
        {
            _tray?.ShowNotification(
                "Pinterest Wallpaper",
                ok ? "Signed out and cleared local Pinterest session." : "Could not clear session while a sync is running.");
        }

        return ok;
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
            _settingsWindow?.RefreshStatus();
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
        finally
        {
            MemoryTrimmer.TrimIdleMemory();
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
            case AppCommand.LikeOnPinterest:
                _ = LikeCurrentOnPinterestAsync();
                break;
            case AppCommand.SaveToPinterest:
                _ = SaveCurrentToPinterestAsync();
                break;
            case AppCommand.OpenPin:
                OpenCurrentPin();
                break;
        }

        _tray?.RefreshState();
    }

    private async Task LikeCurrentOnPinterestAsync()
    {
        var saved = _rotation?.MarkCurrentFavorite();
        if (saved is null || _config is null)
        {
            return;
        }

        if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(saved.PinUrl))
        {
            _tray?.ShowNotification("Pinterest Wallpaper", "Marked local wallpaper as favorite.");
            return;
        }

        if (!_config.Actions.BackgroundSaveEnabled || _syncService is null || _syncCts is null)
        {
            OpenAllowedPinterestUrl(saved.PinUrl);
            return;
        }

        var result = await _syncService.TryLikePinAsync(saved, _syncCts.Token);
        MemoryTrimmer.TrimIdleMemory();
        _tray?.ShowNotification("Pinterest Wallpaper", result.Message);

        if (result.FallbackToBrowser)
        {
            OpenAllowedPinterestUrl(saved.PinUrl);
        }
    }

    private async Task SaveCurrentToPinterestAsync()
    {
        var saved = _rotation?.MarkCurrentFavorite();
        if (saved is null || _config is null)
        {
            return;
        }

        if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(saved.PinUrl))
        {
            _tray?.ShowNotification("Pinterest Wallpaper", "Marked local wallpaper as favorite.");
            return;
        }

        if (!_config.Actions.BackgroundSaveEnabled || _syncService is null || _syncCts is null)
        {
            OpenAllowedPinterestUrl(saved.PinUrl);
            return;
        }

        var result = await _syncService.TrySavePinAsync(saved, _config.Actions, _syncCts.Token);
        MemoryTrimmer.TrimIdleMemory();
        _tray?.ShowNotification("Pinterest Wallpaper", result.Message);

        if (result.FallbackToBrowser)
        {
            OpenAllowedPinterestUrl(saved.PinUrl);
        }
    }

    private void OpenCurrentPin()
    {
        var current = _rotation?.CurrentPins.FirstOrDefault();
        if (current is null)
        {
            return;
        }

        if (PinterestUrlGuard.IsAllowedPinterestPageUrl(current.PinUrl))
        {
            OpenAllowedPinterestUrl(current.PinUrl);
        }
        else if (File.Exists(current.LocalPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{current.LocalPath}\"")
            {
                UseShellExecute = true
            });
        }
    }

    private static void OpenAllowedPinterestUrl(string url)
    {
        if (PinterestUrlGuard.IsAllowedPinterestPageUrl(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void ToggleContextMenu()
    {
        if (_config is null || _configStore is null)
        {
            return;
        }

        var enable = !ContextMenuRegistrar.IsInstalled();
        _config.Shell.DesktopContextMenu = enable;
        SyncShellIntegrations(_config.Shell);
        _configStore.Save(_config);
        _tray?.RefreshState();
    }

    private static void SyncShellIntegrations(ShellConfig shell)
    {
        try
        {
            if (shell.DesktopContextMenu)
            {
                ContextMenuRegistrar.Install();
            }
            else
            {
                ContextMenuRegistrar.Uninstall();
            }

            StartupRegistrar.SetEnabled(shell.StartWithWindows);
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
        SyncShellIntegrations(_config.Shell);
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
