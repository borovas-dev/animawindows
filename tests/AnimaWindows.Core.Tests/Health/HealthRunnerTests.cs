#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Health;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Tests.Health;

public class HealthRunnerTests
{
    private sealed class Fake : IAnimeProvider
    {
        public Func<CancellationToken, Task<IReadOnlyList<Anime>>> OnSearch = _ => Task.FromResult<IReadOnlyList<Anime>>([Naruto]);
        public Func<CancellationToken, Task<IReadOnlyList<Episode>>> OnEpisodes = _ => Task.FromResult<IReadOnlyList<Episode>>([new Episode("e1", "1")]);
        public Func<CancellationToken, Task<StreamSet>> OnStreams = _ => Task.FromResult(new StreamSet { MasterUrl = "https://x/m.m3u8" });
        public string Id => "fake";
        public string DisplayName => "Fake";
        public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string> { "en" };
        public Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken ct = default) => OnSearch(ct);
        public Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken ct = default) => OnEpisodes(ct);
        public Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken ct = default) => OnStreams(ct);
    }

    private static readonly Anime Naruto = new() { Id = "a", ProviderId = "fake", Title = "Naruto" };
    private static readonly ProviderHealth Init = new("fake", "Fake");

    [Fact]
    public async Task AllStepsOkWithLatency()
    {
        var f = new Fake { OnSearch = async ct => { await Task.Delay(120, ct); return [Naruto]; } };
        var h = await new HealthRunner(TimeSpan.FromSeconds(5)).CheckAsync(f, Init);
        Assert.True(h.AllOk);
        Assert.InRange(h.Search.Millis, 100, 2000);
    }

    [Fact]
    public async Task SearchFailureSkipsRest()
    {
        var f = new Fake { OnSearch = _ => throw new InvalidOperationException("boom") };
        var h = await new HealthRunner().CheckAsync(f, Init);
        Assert.Equal(StepStatus.Failed, h.Search.Status);
        Assert.Equal("boom", h.Search.Message);
        Assert.Equal(StepStatus.Skipped, h.Episodes.Status);
        Assert.Equal(StepStatus.Skipped, h.Streams.Status);
    }

    [Fact]
    public async Task EmptySearchIsFailure()
    {
        var f = new Fake { OnSearch = _ => Task.FromResult<IReadOnlyList<Anime>>([]) };
        Assert.Equal(StepStatus.Failed, (await new HealthRunner().CheckAsync(f, Init)).Search.Status);
    }

    [Fact]
    public async Task EpisodesFailureSkipsStreams()
    {
        var f = new Fake { OnEpisodes = _ => throw new IOException("net down") };
        var h = await new HealthRunner().CheckAsync(f, Init);
        Assert.Equal(StepStatus.Ok, h.Search.Status);
        Assert.Equal(StepStatus.Failed, h.Episodes.Status);
        Assert.Equal(StepStatus.Skipped, h.Streams.Status);
    }

    [Fact]
    public async Task StreamTimeoutReported()
    {
        var f = new Fake { OnStreams = async ct => { await Task.Delay(60_000, ct); return new StreamSet(); } };
        var h = await new HealthRunner(TimeSpan.FromMilliseconds(200)).CheckAsync(f, Init);
        Assert.Equal(StepStatus.Failed, h.Streams.Status);
        Assert.Contains("timed out", h.Streams.Message);
    }

    [Fact]
    public async Task NoPlayableStreamsIsFailure()
    {
        var f = new Fake { OnStreams = _ => Task.FromResult(new StreamSet()) };
        Assert.Equal(StepStatus.Failed, (await new HealthRunner().CheckAsync(f, Init)).Streams.Status);
    }

    [Fact]
    public async Task ReportsProgressUpdates()
    {
        var seen = new List<ProviderHealth>();
        await new HealthRunner().CheckAsync(new Fake(), Init, seen.Add);
        Assert.True(seen.Count >= 4);
        Assert.True(seen[^1].Finished);
    }
}
