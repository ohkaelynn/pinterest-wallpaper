using PinterestWallpaper.Core.Pinterest;
using Xunit;

namespace PinterestWallpaper.Tests;

public sealed class PinterestBoardExtractorTests
{
    [Fact]
    public void ExtractFromJson_ExtractsUniqueUserBoardsAndNormalizesUrls()
    {
        const string payload = """
        {
          "resource_response": {
            "data": [
              { "type": "board", "name": "Wallpapers", "url": "/someuser/wallpapers/", "pin_count": 42 },
              { "type": "board", "name": "Cats", "url": "https://www.pinterest.com/someuser/cats/", "pin_count": 12 },
              { "type": "pin", "name": "Not a board", "url": "/pin/123456/" },
              { "type": "board", "name": "Wallpapers", "url": "/someuser/wallpapers/", "pin_count": 42 }
            ]
          }
        }
        """;

        var boards = PinterestBoardExtractor.ExtractFromJson(payload);

        Assert.Equal(2, boards.Count);
        Assert.Equal("Cats", boards[0].Name);
        Assert.Equal("https://www.pinterest.com/someuser/cats/", boards[0].Url);
        Assert.Equal("Wallpapers", boards[1].Name);
        Assert.Equal("https://www.pinterest.com/someuser/wallpapers/", boards[1].Url);
    }
}
