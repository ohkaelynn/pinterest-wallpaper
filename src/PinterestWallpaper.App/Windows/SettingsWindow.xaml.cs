using System.Globalization;
using System.Windows;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Storage;
using MessageBox = System.Windows.MessageBox;

namespace PinterestWallpaper.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly StateDatabase _db;
    private readonly RateLimitGovernor _governor;
    private readonly Action<AppConfig> _onSave;
    private readonly Action _onSignIn;
    private readonly Action _onSyncNow;
    private readonly Func<Task<bool>> _onSignOut;

    public SettingsWindow(
        AppConfig config,
        StateDatabase db,
        RateLimitGovernor governor,
        Action<AppConfig> onSave,
        Action onSignIn,
        Action onSyncNow,
        Func<Task<bool>> onSignOut)
    {
        _config = config;
        _db = db;
        _governor = governor;
        _onSave = onSave;
        _onSignIn = onSignIn;
        _onSyncNow = onSyncNow;
        _onSignOut = onSignOut;

        InitializeComponent();

        FitModeCombo.ItemsSource = Enum.GetValues<WallpaperFitMode>();
        OrientationCombo.ItemsSource = Enum.GetValues<ImageOrientation>();

        LoadIntoForm();
        RefreshStatus();
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PageSources is null)
        {
            return;
        }

        PageSources.Visibility = NavSources.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageRotation.Visibility = NavRotation.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageFilters.Visibility = NavFilters.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = NavAccount.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavAccount.IsChecked == true)
        {
            RefreshStatus();
        }
    }

    private void LoadIntoForm()
    {
        BoardsBox.Text = string.Join(Environment.NewLine, _config.Sources.Boards.Where(b => b.Enabled).Select(b => b.Url));
        SearchesBox.Text = string.Join(Environment.NewLine, _config.Sources.Searches.Where(s => s.Enabled).Select(s => s.Query));
        HomeFeedCheck.IsChecked = _config.Sources.HomeFeed.Enabled;

        BackgroundSaveCheck.IsChecked = _config.Actions.BackgroundSaveEnabled;
        DefaultBoardBox.Text = _config.Actions.DefaultSaveBoardName;

        RotationEnabledCheck.IsChecked = _config.Rotation.Enabled;
        IntervalBox.Text = _config.Rotation.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        JitterBox.Text = _config.Rotation.JitterPercent.ToString(CultureInfo.InvariantCulture);
        FitModeCombo.SelectedItem = _config.Display.FitMode;
        PerMonitorCheck.IsChecked = _config.Display.PerMonitorWallpapers;
        PauseBatteryCheck.IsChecked = _config.Rotation.PauseOnBattery;
        PauseFullscreenCheck.IsChecked = _config.Rotation.PauseOnFullscreen;
        ContextMenuCheck.IsChecked = _config.Shell.DesktopContextMenu;
        StartWithWindowsCheck.IsChecked = _config.Shell.StartWithWindows;

        MinWidthBox.Text = _config.Filters.MinWidth.ToString(CultureInfo.InvariantCulture);
        MinHeightBox.Text = _config.Filters.MinHeight.ToString(CultureInfo.InvariantCulture);
        OrientationCombo.SelectedItem = _config.Filters.Orientation;
        AspectMinBox.Text = _config.Filters.AspectRatioMin.ToString("F2", CultureInfo.InvariantCulture);
        AspectMaxBox.Text = _config.Filters.AspectRatioMax.ToString("F2", CultureInfo.InvariantCulture);
        DedupeCheck.IsChecked = _config.Filters.DeduplicateSimilar;

        MaxCacheBox.Text = _config.Fetching.MaxCacheMb.ToString(CultureInfo.InvariantCulture);
        LowWaterBox.Text = _config.Fetching.LowWaterMark.ToString(CultureInfo.InvariantCulture);
        TargetPoolBox.Text = _config.Fetching.TargetPoolSize.ToString(CultureInfo.InvariantCulture);
        CooldownBox.Text = _config.Fetching.MinSyncCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        MaxSyncsBox.Text = _config.Fetching.MaxSyncsPerDay.ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshStatus()
    {
        var unseen = _db.GetUnseenCount();
        var total = _db.GetAvailablePins().Count;
        var lastSync = _db.GetLastSyncTimeUtc();
        var syncsToday = _db.GetSyncCountSince(DateTimeOffset.UtcNow.AddHours(-24));
        var decision = _governor.CanSync(_config.Fetching, DateTimeOffset.UtcNow);

        StatTotalText.Text = total.ToString(CultureInfo.InvariantCulture);
        StatUnseenText.Text = unseen.ToString(CultureInfo.InvariantCulture);
        StatSyncsText.Text = $"{syncsToday} / {_config.Fetching.MaxSyncsPerDay}";
        SidebarStatusText.Text = $"{unseen} unseen ready · {total} cached";

        var lastSyncText = lastSync is null ? "Never" : lastSync.Value.ToLocalTime().ToString("g");
        StatusText.Text = $"Last sync: {lastSyncText}\nNext sync: {decision.Reason}";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var boardLines = BoardsBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var boards = new List<BoardSource>();
        foreach (var line in boardLines)
        {
            if (!PinterestUrlGuard.IsAllowedPinterestPageUrl(line))
            {
                MessageBox.Show(
                    $"Invalid Pinterest board URL:\n{line}\n\nURLs must use https:// and a pinterest.* domain.",
                    "Invalid Board URL",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            boards.Add(new BoardSource { Url = line, Enabled = true, Weight = 50 });
        }

        var searches = SearchesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(q => new SearchSource { Query = q, Enabled = true, Weight = 35 })
            .ToList();

        _config.Sources.Boards = boards;
        _config.Sources.Searches = searches;
        _config.Sources.HomeFeed.Enabled = HomeFeedCheck.IsChecked == true;

        _config.Actions.BackgroundSaveEnabled = BackgroundSaveCheck.IsChecked == true;
        _config.Actions.DefaultSaveBoardName = DefaultBoardBox.Text.Trim();

        _config.Rotation.Enabled = RotationEnabledCheck.IsChecked == true;
        if (int.TryParse(IntervalBox.Text, out var interval)) _config.Rotation.IntervalMinutes = interval;
        if (int.TryParse(JitterBox.Text, out var jitter)) _config.Rotation.JitterPercent = jitter;
        if (FitModeCombo.SelectedItem is WallpaperFitMode fit) _config.Display.FitMode = fit;
        _config.Display.PerMonitorWallpapers = PerMonitorCheck.IsChecked == true;
        _config.Rotation.PauseOnBattery = PauseBatteryCheck.IsChecked == true;
        _config.Rotation.PauseOnFullscreen = PauseFullscreenCheck.IsChecked == true;
        _config.Shell.DesktopContextMenu = ContextMenuCheck.IsChecked == true;
        _config.Shell.StartWithWindows = StartWithWindowsCheck.IsChecked == true;

        if (int.TryParse(MinWidthBox.Text, out var minW)) _config.Filters.MinWidth = Math.Max(320, minW);
        if (int.TryParse(MinHeightBox.Text, out var minH)) _config.Filters.MinHeight = Math.Max(240, minH);
        if (OrientationCombo.SelectedItem is ImageOrientation orient) _config.Filters.Orientation = orient;
        if (double.TryParse(AspectMinBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var aMin)) _config.Filters.AspectRatioMin = aMin;
        if (double.TryParse(AspectMaxBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var aMax)) _config.Filters.AspectRatioMax = aMax;
        _config.Filters.DeduplicateSimilar = DedupeCheck.IsChecked == true;

        if (int.TryParse(MaxCacheBox.Text, out var maxCache)) _config.Fetching.MaxCacheMb = maxCache;
        if (int.TryParse(LowWaterBox.Text, out var lowWater)) _config.Fetching.LowWaterMark = Math.Max(1, lowWater);
        if (int.TryParse(TargetPoolBox.Text, out var targetPool)) _config.Fetching.TargetPoolSize = Math.Max(5, targetPool);
        if (int.TryParse(CooldownBox.Text, out var cooldown)) _config.Fetching.MinSyncCooldownMinutes = cooldown;
        if (int.TryParse(MaxSyncsBox.Text, out var maxSyncs)) _config.Fetching.MaxSyncsPerDay = maxSyncs;

        _onSave(_config);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
    private void OnSignInClick(object sender, RoutedEventArgs e) => _onSignIn();
    private void OnSyncNowClick(object sender, RoutedEventArgs e) => _onSyncNow();

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Sign out and delete your local Pinterest WebView2 session data?",
            "Sign Out",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var ok = await _onSignOut();
        MessageBox.Show(
            ok ? "Local Pinterest session cleared." : "Could not clear session while a sync is running. Try again in a moment.",
            "Pinterest Wallpaper",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RefreshStatus();
    }
}
