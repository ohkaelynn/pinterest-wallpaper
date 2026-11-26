using System.Runtime.InteropServices;
using PinterestWallpaper.Core.Config;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public sealed class DesktopWallpaperApplier : IWallpaperApplier
{
    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendWinIniChange = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    public int GetMonitorCount()
    {
        try
        {
            var com = CreateComInstance();
            com.GetMonitorDevicePathCount(out var count);
            return Math.Max(1, (int)count);
        }
        catch
        {
            return Math.Max(1, System.Windows.Forms.Screen.AllScreens.Length);
        }
    }

    public void ApplyWallpapers(IReadOnlyList<string> imagePaths, DisplayConfig displayConfig)
    {
        if (imagePaths.Count == 0)
        {
            return;
        }

        try
        {
            var com = CreateComInstance();
            com.SetPosition(MapPosition(displayConfig.FitMode));

            if (displayConfig.PerMonitorWallpapers && imagePaths.Count > 1)
            {
                com.GetMonitorDevicePathCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    com.GetMonitorDevicePathAt(i, out var monitorId);
                    var path = imagePaths[(int)(i % imagePaths.Count)];
                    com.SetWallpaper(monitorId, path);
                }
            }
            else
            {
                com.SetWallpaper(null, imagePaths[0]);
            }
        }
        catch
        {
            SystemParametersInfoW(SpiSetDeskWallpaper, 0, imagePaths[0], SpifUpdateIniFile | SpifSendWinIniChange);
        }
    }

    private static IDesktopWallpaper CreateComInstance()
    {
        var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"))
            ?? throw new InvalidOperationException("IDesktopWallpaper COM class not available.");
        return (IDesktopWallpaper)(Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Failed to create IDesktopWallpaper instance."));
    }

    private static DesktopWallpaperPosition MapPosition(WallpaperFitMode mode) => mode switch
    {
        WallpaperFitMode.Center => DesktopWallpaperPosition.Center,
        WallpaperFitMode.Tile => DesktopWallpaperPosition.Tile,
        WallpaperFitMode.Stretch => DesktopWallpaperPosition.Stretch,
        WallpaperFitMode.Fit => DesktopWallpaperPosition.Fit,
        WallpaperFitMode.Span => DesktopWallpaperPosition.Span,
        _ => DesktopWallpaperPosition.Fill
    };

    private enum DesktopWallpaperPosition
    {
        Center = 0,
        Tile = 1,
        Stretch = 2,
        Fit = 3,
        Fill = 4,
        Span = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out Rect displayRect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(DesktopWallpaperPosition position);
        void GetPosition(out DesktopWallpaperPosition position);
        void SetSlideshow(IntPtr items);
        void GetSlideshow(out IntPtr items);
        void SetSlideshowOptions(uint options, uint slideshowTick);
        void GetSlideshowOptions(out uint options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, uint direction);
        void GetStatus(out uint state);
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }
}
