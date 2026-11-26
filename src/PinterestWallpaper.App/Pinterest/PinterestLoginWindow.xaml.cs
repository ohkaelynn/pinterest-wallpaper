using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace PinterestWallpaper.App.Pinterest;

public partial class PinterestLoginWindow : Window
{
    private readonly string _webView2DataDir;

    public PinterestLoginWindow(string webView2DataDir)
    {
        _webView2DataDir = webView2DataDir;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _webView2DataDir);
            await WebView.EnsureCoreWebView2Async(env);
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
