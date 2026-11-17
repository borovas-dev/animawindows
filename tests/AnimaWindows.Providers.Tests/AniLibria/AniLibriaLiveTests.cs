using AnimaWindows.Core.Models;
using AnimaWindows.Providers.AniLibria;

namespace AnimaWindows.Providers.Tests.AniLibria;

/// <summary>Hits the real AniLibria API. Excluded from CI by the Category filter.</summary>
public sealed class AniLibriaLiveTests : IDisposable
{
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Live")]
    public async Task SearchEpisodesStreams()
    {
        var provider = new AniLibriaProvider(_http, new StubConfig());
        var anime = (await provider.SearchAsync("naruto", TranslationMode.Sub, Ct))[0];
        var episodes = await provider.EpisodesAsync(anime, TranslationMode.Sub, Ct);
        Assert.NotEmpty(episodes);
        var streams = await provider.StreamsAsync(anime, episodes[0], TranslationMode.Sub, Ct);
        Assert.NotEmpty(streams.Variants);
    }
}
