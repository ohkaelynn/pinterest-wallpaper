namespace PinterestWallpaper.Core.Rotation;

public interface ISystemStateProvider
{
    bool IsOnBattery();
    bool IsFullscreenAppActive();
}
