namespace PinterestWallpaper.Core.Rotation;

public enum AppCommand
{
    None,
    Next,
    Previous,
    OpenPin,
    SkipAndHide,
    TogglePause,
    TogglePin
}

public static class CommandParser
{
    public static AppCommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return AppCommand.None;
        }

        return Parse(args[0]);
    }

    public static AppCommand Parse(string? arg) => arg?.Trim().ToLowerInvariant() switch
    {
        "--next" or "next" => AppCommand.Next,
        "--prev" or "--previous" or "prev" => AppCommand.Previous,
        "--open-pin" or "open-pin" => AppCommand.OpenPin,
        "--skip" or "skip" => AppCommand.SkipAndHide,
        "--toggle-pause" or "toggle-pause" => AppCommand.TogglePause,
        "--toggle-pin" or "toggle-pin" => AppCommand.TogglePin,
        _ => AppCommand.None
    };

    public static string ToArgument(AppCommand command) => command switch
    {
        AppCommand.Next => "--next",
        AppCommand.Previous => "--prev",
        AppCommand.OpenPin => "--open-pin",
        AppCommand.SkipAndHide => "--skip",
        AppCommand.TogglePause => "--toggle-pause",
        AppCommand.TogglePin => "--toggle-pin",
        _ => string.Empty
    };
}
