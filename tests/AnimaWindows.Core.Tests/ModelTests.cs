using System.Text.Json;
using AnimaWindows.Core.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Tests;

public class ModelTests
{
    [Fact]
    public void AppSettings_RoundTrips_WithDefaults()
    {
        var settings = new AppSettings();
        var json = JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppSettings);

        Assert.NotNull(back);
        Assert.Equal("auto", back.ContentLanguage);
        Assert.Equal(PlaybackEngineChoice.Auto, back.Engine);
        Assert.Contains("\"mode\":\"SUB\"", json);
    }
}
