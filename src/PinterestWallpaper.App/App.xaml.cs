using System.Windows;
using System.Windows.Threading;
using PinterestWallpaper.App.Windows;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;
using PinterestWallpaper.Core.Storage;
using Application = System.Windows.Application;

namespace PinterestWallpaper.App;

public partial class App : Application
{
    private AppPaths? _paths;
    private ConfigStore? _configStore;
    private AppConfig? _config;
    private StateDatabase? _db;
    private RotationService? _rotation;
    private TrayIconHost? _tray;
    private DispatcherTimer? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _paths = new AppPaths();
        _paths.EnsureDirectoriesExist();

        _configStore = new ConfigStore(_paths.ConfigFile);
        _config = _configStore.LoadOrCreate();

        _db = new StateDatabase(_paths.DatabaseFile);
        var applier = new DesktopWallpaperApplier();
        var systemState = new WindowsSystemStateProvider();
        _rotation = new RotationService(_db, applier, systemState);

        _tray = new TrayIconHost(_paths, () => _config, _rotation, ReloadConfig, Shutdown);

        _timer = new DispatcherTimer();
        _timer.Tick += OnRotationTick;
        ScheduleNextTick();
    }

    private void OnRotationTick(object? sender, EventArgs e)
    {
        if (_rotation is not null && _config is not null)
        {
            _rotation.NextWallpaper(_config, isManual: false);
            ScheduleNextTick();
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
        ScheduleNextTick();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }
}
