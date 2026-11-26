using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PinterestWallpaper.App.Pinterest;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Media;
using PinterestWallpaper.Core.Pinterest;
using PinterestWallpaper.Core.Storage;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace PinterestWallpaper.App.Windows;

public partial class SettingsWindow : Window
{
    private const string DefaultMySavesOption = "(My Saves — Default)";

    private sealed record ResolutionPreset(string Label, int Width, int Height)
    {
        public override string ToString() => Label;
    }

    private static readonly ResolutionPreset[] ResolutionPresets =
    [
        new("Any (640×480+)", 640, 480),
        new("HD (1280×720)", 1280, 720),
        new("Full HD (1920×1080)", 1920, 1080),
        new("QHD / 2K (2560×1440)", 2560, 1440),
        new("Ultrawide (3440×1440)", 3440, 1440),
        new("4K UHD (3840×2160)", 3840, 2160),
        new("Custom", 0, 0)
    ];

    private readonly AppPaths _paths;
    private readonly AppConfig _config;
    private readonly StateDatabase _db;
    private readonly RateLimitGovernor _governor;
    private readonly PinterestSyncService _syncService;
    private readonly Action<AppConfig> _onSave;
    private readonly Action _onSignIn;
    private readonly Action _onSyncNow;
    private readonly Func<Task<bool>> _onSignOut;
    private bool _updatingResolutionUi;

    public SettingsWindow(
        AppPaths paths,
        AppConfig config,
        StateDatabase db,
        RateLimitGovernor governor,
        PinterestSyncService syncService,
        Action<AppConfig> onSave,
        Action onSignIn,
        Action onSyncNow,
        Func<Task<bool>> onSignOut)
    {
        _paths = paths;
        _config = config;
        _db = db;
        _governor = governor;
        _syncService = syncService;
        _onSave = onSave;
        _onSignIn = onSignIn;
        _onSyncNow = onSyncNow;
        _onSignOut = onSignOut;

        InitializeComponent();
        DarkTitleBarHelper.Enable(this);

        FitModeCombo.ItemsSource = Enum.GetValues<WallpaperFitMode>();
        OrientationCombo.ItemsSource = Enum.GetValues<ImageOrientation>();
        ResolutionPresetCombo.ItemsSource = ResolutionPresets;

        PopulateBoardDropdowns(_syncService.GetCachedUserBoards());
        LoadIntoForm();
        RefreshStatus();
    }

    private void PopulateBoardDropdowns(IReadOnlyList<UserBoard> boards)
    {
        ReadBoardCombo.ItemsSource = boards;
        if (boards.Count > 0 && ReadBoardCombo.SelectedIndex < 0)
        {
            ReadBoardCombo.SelectedIndex = 0;
        }

        var currentText = DefaultBoardCombo.Text;
        var saveOptions = new List<string> { DefaultMySavesOption };
        saveOptions.AddRange(boards.Select(b => b.Name));
        DefaultBoardCombo.ItemsSource = saveOptions;

        if (!string.IsNullOrWhiteSpace(currentText))
        {
            DefaultBoardCombo.Text = currentText;
        }
    }

    private async void OnLoadBoardsClick(object sender, RoutedEventArgs e)
    {
        LoadBoardsBtn.IsEnabled = false;
        LoadBoardsBtn.Content = "Loading...";
        try
        {
            var (success, boards, message) = await _syncService.TryFetchUserBoardsAsync();
            PopulateBoardDropdowns(boards);
            RefreshStatus();
            if (!success)
            {
                MessageBox.Show(message, "Pinterest Boards", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            LoadBoardsBtn.Content = "Load My Boards";
            LoadBoardsBtn.IsEnabled = true;
        }
    }

    private void OnAddSelectedBoardClick(object sender, RoutedEventArgs e)
    {
        if (ReadBoardCombo.SelectedItem is not UserBoard selected)
        {
            return;
        }

        var existing = BoardsBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!existing.Contains(selected.Url, StringComparer.OrdinalIgnoreCase))
        {
            existing.Add(selected.Url);
            BoardsBox.Text = string.Join(Environment.NewLine, existing);
        }
    }

    private void OnResolutionPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingResolutionUi || ResolutionPresetCombo.SelectedItem is not ResolutionPreset preset || preset.Width == 0)
        {
            return;
        }

        _updatingResolutionUi = true;
        MinWidthBox.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
        MinHeightBox.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
        _updatingResolutionUi = false;
    }

    private void OnCustomResolutionEdited(object sender, TextChangedEventArgs e)
    {
        if (_updatingResolutionUi)
        {
            return;
        }

        SyncResolutionPresetSelection();
    }

    private void SyncResolutionPresetSelection()
    {
        if (!int.TryParse(MinWidthBox.Text, out var w) || !int.TryParse(MinHeightBox.Text, out var h))
        {
            return;
        }

        _updatingResolutionUi = true;
        var match = ResolutionPresets.FirstOrDefault(p => p.Width == w && p.Height == h)
            ?? ResolutionPresets[^1];
        ResolutionPresetCombo.SelectedItem = match;
        _updatingResolutionUi = false;
    }

    public void SelectAccountTab()
    {
        NavAccount.IsChecked = true;
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
        DefaultBoardCombo.Text = string.IsNullOrWhiteSpace(_config.Actions.DefaultSaveBoardName)
            ? DefaultMySavesOption
            : _config.Actions.DefaultSaveBoardName;

        RotationEnabledCheck.IsChecked = _config.Rotation.Enabled;
        IntervalBox.Text = _config.Rotation.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
        JitterBox.Text = _config.Rotation.JitterPercent.ToString(CultureInfo.InvariantCulture);
        FitModeCombo.SelectedItem = _config.Display.FitMode;
        PerMonitorCheck.IsChecked = _config.Display.PerMonitorWallpapers;
        PauseBatteryCheck.IsChecked = _config.Rotation.PauseOnBattery;
        PauseFullscreenCheck.IsChecked = _config.Rotation.PauseOnFullscreen;
        ContextMenuCheck.IsChecked = _config.Shell.DesktopContextMenu;
        StartWithWindowsCheck.IsChecked = _config.Shell.StartWithWindows;

        _updatingResolutionUi = true;
        MinWidthBox.Text = _config.Filters.MinWidth.ToString(CultureInfo.InvariantCulture);
        MinHeightBox.Text = _config.Filters.MinHeight.ToString(CultureInfo.InvariantCulture);
        _updatingResolutionUi = false;
        SyncResolutionPresetSelection();

        OrientationCombo.SelectedItem = _config.Filters.Orientation;
        AspectMinBox.Text = _config.Filters.AspectRatioMin.ToString("F2", CultureInfo.InvariantCulture);
        AspectMaxBox.Text = _config.Filters.AspectRatioMax.ToString("F2", CultureInfo.InvariantCulture);
        DedupeCheck.IsChecked = _config.Filters.DeduplicateSimilar;

        PinsPerSyncBox.Text = _config.Fetching.MaxPinsPerSync.ToString(CultureInfo.InvariantCulture);
        MaxCacheBox.Text = _config.Fetching.MaxCacheMb.ToString(CultureInfo.InvariantCulture);
        LowWaterBox.Text = _config.Fetching.LowWaterMark.ToString(CultureInfo.InvariantCulture);
        TargetPoolBox.Text = _config.Fetching.TargetPoolSize.ToString(CultureInfo.InvariantCulture);
        CooldownBox.Text = _config.Fetching.MinSyncCooldownMinutes.ToString(CultureInfo.InvariantCulture);
        MaxSyncsBox.Text = _config.Fetching.MaxSyncsPerDay.ToString(CultureInfo.InvariantCulture);
    }

    public void RefreshStatus()
    {
        var unseen = _db.GetUnseenCount();
        var total = _db.GetAvailablePins().Count;
        var lastSync = _db.GetLastSyncTimeUtc();
        var syncsToday = _db.GetSyncCountSince(DateTimeOffset.UtcNow.AddHours(-24));

        StatTotalText.Text = total.ToString(CultureInfo.InvariantCulture);
        StatUnseenText.Text = unseen.ToString(CultureInfo.InvariantCulture);
        StatSyncsText.Text = syncsToday.ToString(CultureInfo.InvariantCulture);
        SidebarStatusText.Text = $"{unseen} unseen · {total} cached";

        var lastSyncText = lastSync is null ? "Never" : lastSync.Value.ToLocalTime().ToString("g");
        StatusText.Text = $"Last sync: {lastSyncText}";

        var (label, color) = GetLoginStatus();
        var brush = new SolidColorBrush(color);
        SidebarLoginDot.Fill = brush;
        SidebarLoginText.Text = label;
        LoginBadgeDot.Fill = brush;
        LoginBadgeText.Text = label;
    }

    private (string Label, Color DotColor) GetLoginStatus()
    {
        var state = _db.GetStateValue(PinterestSyncService.SessionStatusKey);
        if (state == "expired")
        {
            return ("Session expired", Color.FromRgb(239, 68, 68));
        }

        if (state == "signed_in" || Directory.Exists(_paths.WebView2Directory))
        {
            return ("Signed in", Color.FromRgb(74, 222, 128));
        }

        return ("Not signed in", Color.FromRgb(245, 158, 11));
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
        var chosenBoard = DefaultBoardCombo.Text.Trim();
        _config.Actions.DefaultSaveBoardName = chosenBoard.Equals(DefaultMySavesOption, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : chosenBoard;

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

        if (int.TryParse(PinsPerSyncBox.Text, out var pinsPerSync)) _config.Fetching.MaxPinsPerSync = pinsPerSync;
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

    private void OnOpenCacheClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.CacheDirectory);
        Process.Start(new ProcessStartInfo(_paths.CacheDirectory) { UseShellExecute = true });
    }

    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Delete all cached wallpaper files and reset the local pool? (Your Pinterest login and blocklist stay intact.)",
            "Clear Cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = CacheManager.ClearCache(_db, _paths.CacheDirectory);
        RefreshStatus();
        MessageBox.Show($"Cleared {deleted} cached wallpaper file(s).", "Pinterest Wallpaper", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Sign out and delete your local Pinterest session?",
            "Sign Out",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var ok = await _onSignOut();
        MessageBox.Show(
            ok ? "Signed out." : "Could not sign out while a sync is running. Try again in a moment.",
            "Pinterest Wallpaper",
            MessageBoxButton.OK,
            ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        RefreshStatus();
    }
}
