using PinterestWallpaper.Core.Pinterest;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class PinterestUrlGuardTests
{
    [Theory]
    [InlineData("https://www.pinterest.com/pin/123456/", true)]
    [InlineData("https://pinterest.com/user/wallpapers/", true)]
    [InlineData("https://uk.pinterest.com/pin/123456/", true)]
    [InlineData("https://www.pinterest.co.uk/user/board/", true)]
    [InlineData("http://www.pinterest.com/pin/123456/", false)]
    [InlineData("https://evilpinterest.com/pin/123456/", false)]
    [InlineData("https://pinterest.com.evil.com/pin/123456/", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void IsAllowedPinterestPageUrl_ValidatesStrictly(string url, bool expected)
    {
        Assert.Equal(expected, PinterestUrlGuard.IsAllowedPinterestPageUrl(url));
    }

    [Theory]
    [InlineData("https://i.pinimg.com/originals/a/b/c.jpg", true)]
    [InlineData("https://s.pinimg.com/images/test.png", true)]
    [InlineData("http://i.pinimg.com/originals/a/b/c.jpg", false)]
    [InlineData("https://evilpinimg.com/a.jpg", false)]
    [InlineData("https://i.pinimg.com.attacker.com/a.jpg", false)]
    [InlineData("https://example.com/wallpaper.jpg", false)]
    public void IsAllowedPinterestImageUrl_ValidatesStrictly(string url, bool expected)
    {
        Assert.Equal(expected, PinterestUrlGuard.IsAllowedPinterestImageUrl(url));
    }
}
