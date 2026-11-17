#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using System.Collections.Immutable;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Chain;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Tests.Chain;

internal sealed class FakeSettings(AppSettings initial) : ISettingsStore
{
    public AppSettings Current { get; private set; } = initial;
    public event EventHandler<AppSettings>? Changed;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Func<AppSettings, AppSettings> transform, CancellationToken cancellationToken = default)
    {
        Current = transform(Current);
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => Task.FromResult("{}");
    public Task ImportJsonAsync(string json, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeProvider(string id, params string[] languages) : IAnimeProvider
{
    public string Id { get; } = id;
    public string DisplayName => Id;
    public IReadOnlySet<string> ContentLanguages { get; } = languages.ToHashSet();
    public bool GfwBlocked { get; init; }
    public int Priority { get; init; } = 100;
    public IReadOnlyList<Anime> Results { get; set; } = [];
    public TimeSpan StreamDelay { get; set; }
    public Exception? Failure { get; set; }
    public int? Height { get; set; } = 720;
    public IReadOnlyList<SubtitleTrack> Subs { get; set; } = [];
    public string? Hardsub { get; set; }
    public int StreamCalls;

    public Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (Failure is not null) throw Failure;
        return Task.FromResult(Results);
    }

    public Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Episode>>([new Episode($"{Id}-ep1", "1"), new Episode($"{Id}-ep2", "2")]);

    public async Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StreamCalls);
        if (StreamDelay > TimeSpan.Zero) await Task.Delay(StreamDelay, cancellationToken);
        if (Failure is not null) throw Failure;
        return new StreamSet
        {
            Variants = Height is { } h ? [new StreamVariant(h, $"https://{Id}/{episode.Id}.m3u8")] : [],
            Subtitles = Subs,
            HardsubLanguage = Hardsub,
        };
    }
}

public class ProviderChainTests
{
    private static Anime A(string provider, string id = "a", string title = "Naruto", int? mal = null, IReadOnlyDictionary<string, string>? alts = null) =>
        new() { Id = id, ProviderId = provider, Title = title, MalId = mal, Alternates = alts ?? ImmutableDictionary<string, string>.Empty };

    private static IReadOnlyDictionary<string, string> Alt(string k, string v) => new Dictionary<string, string> { [k] = v };

    private static ProviderChain Chain(IEnumerable<IAnimeProvider> p, AppSettings? settings = null, TimeSpan? timeout = null, string device = "en", IProviderAvailability? availability = null) =>
        new(p, new FakeSettings(settings ?? new AppSettings()), new ProviderChainOptions(timeout ?? TimeSpan.FromSeconds(20)), availability, () => device);

    private static IEnumerable<string> Ids(IEnumerable<IAnimeProvider> l) => l.Select(p => p.Id);

    [Fact]
    public void ChainOrdersByLanguageThenEnglishFallback()
    {
        var en1 = new FakeProvider("hianime", "en") { Priority = 0 };
        var en2 = new FakeProvider("anidb", "en") { Priority = 1 };
        var pl = new FakeProvider("shinden", "pl");
        var c = Chain([en2, pl, en1]);
        Assert.Equal(["hianime", "anidb"], Ids(c.ChainFor(new AppSettings { ContentLanguage = "en" })));
        Assert.Equal(["shinden", "hianime", "anidb"], Ids(c.ChainFor(new AppSettings { ContentLanguage = "pl" })));
        Assert.Equal(["shinden", "hianime", "anidb"], Ids(c.ChainFor(new AppSettings { SubtitleLanguage = "pl" })));
        Assert.Equal(["hianime", "anidb"], Ids(c.ChainFor(new AppSettings())));
    }

    private sealed class Availability(Func<string, bool> f) : IProviderAvailability
    {
        public bool IsEnabled(string id) => f(id);
    }

    [Fact]
    public void RemotelyDisabledProvidersAreSkipped()
    {
        var a = new FakeProvider("a", "en") { Priority = 0 };
        var b = new FakeProvider("b", "en") { Priority = 1 };
        var c = Chain([a, b], availability: new Availability(id => id != "a"));
        Assert.Equal(["b"], Ids(c.ChainFor(new AppSettings { ContentLanguage = "en" })));
    }

    [Fact]
    public void ManualOrderAndDisabled()
    {
        var a = new FakeProvider("a", "en") { Priority = 0 };
        var b = new FakeProvider("b", "en") { Priority = 1 };
        var c = Chain([a, b]);
        var order = new Dictionary<string, IReadOnlyList<string>> { ["en"] = ["b"] };
        Assert.Equal(["b", "a"], Ids(c.ChainFor(new AppSettings { ContentLanguage = "en", ProviderOrder = order })));
        Assert.Equal(["b"], Ids(c.ChainFor(new AppSettings { ContentLanguage = "en", DisabledProviders = ["a"] })));
    }

    [Fact]
    public void ZhExcludesGfwBlockedEvenInManualOrder()
    {
        var xifan = new FakeProvider("xifan", "zh");
        var hi = new FakeProvider("hianime", "en") { GfwBlocked = true };
        var anidb = new FakeProvider("anidb", "en");
        var c = Chain([xifan, hi, anidb]);
        var s = new AppSettings
        {
            ContentLanguage = "zh",
            ProviderOrder = new Dictionary<string, IReadOnlyList<string>> { ["zh"] = ["hianime", "xifan"] },
        };
        Assert.Equal(["xifan", "anidb"], Ids(c.ChainFor(s)));
        Assert.Contains("hianime", Ids(c.ChainFor(new AppSettings { ContentLanguage = "en" })));
    }

    [Fact]
    public async Task SearchMergesByMalIdThenTitleAndFillsAlternates()
    {
        var p1 = new FakeProvider("p1", "en") { Priority = 0, Results = [A("p1", "x1", "Naruto", 20), A("p1", "x2", "Only P1")] };
        var p2 = new FakeProvider("p2", "en")
        {
            Priority = 1,
            Results = [A("p2", "y1", "NARUTO (TV)", 20), A("p2", "y2", "only p1"), A("p2", "y3", "Only P2")],
        };
        var r = await Chain([p1, p2]).SearchAsync("q", TranslationMode.Sub);
        Assert.Equal(["Naruto", "Only P1", "Only P2"], r.Select(a => a.Title));
        Assert.Equal(Alt("p2", "y1"), r[0].Alternates);
        Assert.Equal(Alt("p2", "y2"), r[1].Alternates);
        Assert.Equal("p1", r[0].ProviderId);
        Assert.Equal("p2", r[2].ProviderId);
    }

    [Fact]
    public async Task SearchToleratesFailureButThrowsWhenAllFail()
    {
        var ok = new FakeProvider("ok", "en") { Priority = 0, Results = [A("ok")] };
        var bad = new FakeProvider("bad", "en") { Priority = 1, Failure = new ProviderException("down") };
        Assert.Single(await Chain([ok, bad]).SearchAsync("q", TranslationMode.Sub));
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Chain([bad]).SearchAsync("q", TranslationMode.Sub));
        Assert.Equal("down", ex.Message);
    }

    [Fact]
    public async Task EmptyChainSearchReturnsEmpty()
    {
        Assert.Empty(await Chain([]).SearchAsync("q", TranslationMode.Sub));
    }

    [Fact]
    public async Task StreamsFailOverToAlternateMatchingEpisodeNumber()
    {
        var p1 = new FakeProvider("p1", "en") { Priority = 0, Failure = new ProviderException("boom") };
        var p2 = new FakeProvider("p2", "en") { Priority = 1 };
        var r = await Chain([p1, p2]).StreamsAsync(A("p1", alts: Alt("p2", "alt")), new Episode("p1-ep2", "2"), TranslationMode.Sub);
        Assert.Equal("p2", r.ProviderId);
        Assert.True(r.IsFallback);
        Assert.Equal("p2-ep2", r.Episode.Id);
        Assert.Equal("alt", r.Anime.Id);
    }

    [Fact]
    public async Task PrimaryIsUsedWhenHealthy()
    {
        var p1 = new FakeProvider("p1", "en") { Priority = 0 };
        var p2 = new FakeProvider("p2", "en") { Priority = 1 };
        var r = await Chain([p1, p2]).StreamsAsync(A("p1", alts: Alt("p2", "alt")), new Episode("e", "1"), TranslationMode.Sub);
        Assert.Equal("p1", r.ProviderId);
        Assert.False(r.IsFallback);
        Assert.Equal(0, p2.StreamCalls);
    }

    [Fact]
    public async Task TimeoutFallsBack()
    {
        var slow = new FakeProvider("slow", "en") { Priority = 0, StreamDelay = TimeSpan.FromSeconds(60) };
        var fast = new FakeProvider("fast", "en") { Priority = 1 };
        var r = await Chain([slow, fast], timeout: TimeSpan.FromMilliseconds(150))
            .StreamsAsync(A("slow", alts: Alt("fast", "f")), new Episode("e", "1"), TranslationMode.Sub);
        Assert.Equal("fast", r.ProviderId);
        Assert.True(r.IsFallback);
    }

    [Fact]
    public async Task EmptyVariantsFallBackAndAllFailThrows()
    {
        var empty = new FakeProvider("empty", "en") { Priority = 0, Height = null };
        var good = new FakeProvider("good", "en") { Priority = 1 };
        var r = await Chain([empty, good]).StreamsAsync(A("empty", alts: Alt("good", "g")), new Episode("e", "1"), TranslationMode.Sub);
        Assert.Equal("good", r.ProviderId);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Chain([empty]).StreamsAsync(A("empty"), new Episode("e", "1"), TranslationMode.Sub));
        Assert.Contains("no playable", ex.Message);
    }

    [Fact]
    public async Task UnknownProviderWithoutAlternatesThrowsNoProviderAvailable()
    {
        await Assert.ThrowsAsync<NoProviderAvailableException>(() => Chain([]).StreamsAsync(A("nope"), new Episode("e", "1"), TranslationMode.Sub));
    }

    [Fact]
    public async Task CloudflareIsRethrownWhenEverythingFails()
    {
        var p = new FakeProvider("p", "en") { Failure = new CloudflareChallengeException("https://x") };
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => Chain([p]).StreamsAsync(A("p"), new Episode("e", "1"), TranslationMode.Sub));
    }

    [Fact]
    public async Task BestQualityPicksHigherResolution()
    {
        var low = new FakeProvider("low", "en") { Priority = 0, Height = 480 };
        var high = new FakeProvider("high", "en") { Priority = 1, Height = 1080 };
        var s = new AppSettings { SourceStrategy = SourceStrategy.BestQuality };
        var a = A("low", alts: Alt("high", "h"));
        var r = await Chain([low, high], s).StreamsAsync(a, new Episode("e", "1"), TranslationMode.Sub);
        Assert.Equal("high", r.ProviderId);
        Assert.True(r.IsFallback);
        // Reliable strategy keeps the primary.
        Assert.Equal("low", (await Chain([low, high]).StreamsAsync(a, new Episode("e", "1"), TranslationMode.Sub)).ProviderId);
    }

    [Fact]
    public async Task BestQualityPrefersSubtitlesWhenRequested()
    {
        var subbed = new FakeProvider("subbed", "en") { Priority = 0, Height = 720, Subs = [new SubtitleTrack("u", "en", "English", true)] };
        var bare = new FakeProvider("bare", "en") { Priority = 1, Height = 1080 };
        var a = A("bare", alts: Alt("subbed", "s"));
        var withSubs = new AppSettings { SourceStrategy = SourceStrategy.BestQuality, SubtitleLanguage = "pl", TranslateSubtitles = true };
        Assert.Equal("subbed", (await Chain([subbed, bare], withSubs).StreamsAsync(a, new Episode("e", "1"), TranslationMode.Sub)).ProviderId);
        var noSubs = new AppSettings { SourceStrategy = SourceStrategy.BestQuality };
        Assert.Equal("bare", (await Chain([subbed, bare], noSubs).StreamsAsync(a, new Episode("e", "1"), TranslationMode.Sub)).ProviderId);
    }

    [Fact]
    public async Task BestQualityHardsubInRequestedLanguageCountsAsSubtitled()
    {
        var hard = new FakeProvider("hard", "zh") { Height = 480, Hardsub = "zh" };
        var plain = new FakeProvider("plain", "en") { Height = 1080 };
        var s = new AppSettings { ContentLanguage = "zh", SourceStrategy = SourceStrategy.BestQuality, SubtitleLanguage = "zh" };
        var r = await Chain([hard, plain], s).StreamsAsync(A("plain", alts: Alt("hard", "h")), new Episode("e", "1"), TranslationMode.Sub);
        Assert.Equal("hard", r.ProviderId);
    }

    [Fact]
    public async Task ProviderIdsAndLookup()
    {
        var c = Chain([new FakeProvider("a", "en")]);
        Assert.Equal(["a"], c.Ids);
        Assert.Equal("a", c.Provider("a")!.Id);
        Assert.Null(c.Provider("zzz"));
        Assert.Equal(["a"], Ids(await c.ChainAsync()));
        Assert.Equal("en", c.EffectiveLanguage(new AppSettings()));
    }

    [Fact]
    public void AutoLanguageFollowsDeviceLocale()
    {
        ProviderChain C(string dev) => Chain([new FakeProvider("a", "en"), new FakeProvider("b", "pl"), new FakeProvider("c", "zh")], device: dev);
        Assert.Equal("pl", C("pl").EffectiveLanguage(new AppSettings()));
        Assert.Equal("zh", C("zh").EffectiveLanguage(new AppSettings()));
        Assert.Equal("en", C("en").EffectiveLanguage(new AppSettings()));
        Assert.Equal("en", C("de").EffectiveLanguage(new AppSettings()));
        Assert.Equal("pl", C("PL").EffectiveLanguage(new AppSettings()));
        Assert.Equal("zh", C("pl").EffectiveLanguage(new AppSettings { ContentLanguage = "zh" }));
        Assert.Equal("zh", C("pl").EffectiveLanguage(new AppSettings { SubtitleLanguage = "zh" }));
        Assert.Equal("pt", C("en").EffectiveLanguage(new AppSettings { ContentLanguage = " pt-BR " }));
        Assert.Equal(["b", "a"], Ids(C("pl").ChainFor(new AppSettings())));
    }
}
