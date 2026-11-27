using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Color = System.Windows.Media.Color;

namespace PinterestWallpaper.App.Pinterest;

public partial class PinterestLoginWindow : Window
{
    private readonly string _webView2DataDir;
    public bool SignedInDetected { get; private set; }

    public PinterestLoginWindow(string webView2DataDir)
    {
        _webView2DataDir = webView2DataDir;
        InitializeComponent();
        Windows.DarkTitleBarHelper.Enable(this);
        Loaded += OnLoaded;
        Closed += (_, _) => WebView2Cleanup.DisposeAndRelease(WebView);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _webView2DataDir);
            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.SourceChanged += (_, _) =>
            {
                var src = WebView.CoreWebView2.Source ?? string.Empty;
                AddressBar.Text = src;

                if (src.Contains("pinterest.", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("/login", StringComparison.OrdinalIgnoreCase) &&
                    !src.Contains("/signup", StringComparison.OrdinalIgnoreCase))
                {
                    SignedInDetected = true;
                    HintText.Text = "Signed in — you can close this window";
                    HintText.Foreground = new SolidColorBrush(Color.FromRgb(74, 222, 128));
                }
            };
            WebView.CoreWebView2.Navigate("https://www.pinterest.com/login/");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not initialize Microsoft Edge WebView2:\n{ex.Message}",
                "Pinterest Wallpaper Swapper",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
        }
    }
}
