using System.Runtime.InteropServices;
using System.Windows.Forms;
using PinterestWallpaper.Core.Rotation;

namespace PinterestWallpaper.App.Windows;

public sealed class WindowsSystemStateProvider : ISystemStateProvider
{
    private const int QunsNotPresent = 1;
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    public bool IsOnBattery()
    {
        return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
    }

    public bool IsFullscreenAppActive()
    {
        if (SHQueryUserNotificationState(out var state) != 0)
        {
            return false;
        }

        return state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode or QunsNotPresent;
    }
}
