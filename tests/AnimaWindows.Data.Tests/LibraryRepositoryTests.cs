using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data.Tests;

public class LibraryRepositoryTests : DataTestBase
{
    private readonly Anime _a = MakeAnime("a1", "Alpha", cover: "c");
    private readonly Anime _b = MakeAnime("b1", "Beta");

    private async Task<IReadOnlyList<string>> RecentAsync(int limit = 20) => (await TakeAsync(Library.ObserveRecentSearches(limit, Ct), 1))[0];

    private async Task<IReadOnlyList<WatchEntry>> ContinueAsync() => (await TakeAsync(Library.ObserveContinueWatching(Ct), 1))[0];

    [Fact]
    public async Task Searches_AreNormalisedDedupedAndCapped()
    {
        await Library.AddSearchAsync("  Naruto ", Ct);
        await Library.AddSearchAsync("naruto", Ct);
        Assert.Equal(new[] { "naruto" }, await RecentAsync());

        for (var i = 0; i < 60; i++)
        {
            await Library.AddSearchAsync("q" + i, Ct);
        }

        var all = await RecentAsync(100);
        Assert.Equal(ILibraryRepository.MaxSearches, all.Count);
        Assert.Equal("q59", all[0]);
        Assert.DoesNotContain("naruto", all);

        await Library.RemoveSearchAsync("Q59", Ct);
        Assert.DoesNotContain("q59", await RecentAsync(100));

        await Library.ClearSearchesAsync(Ct);
        Assert.Empty(await RecentAsync());
    }

    [Fact]
    public async Task Searches_BlankIsNoOp_AndLimitApplies()
    {
        await Library.AddSearchAsync("   ", Ct);
        Assert.Empty(await RecentAsync());
        await Library.AddSearchAsync("a", Ct);
        await Library.AddSearchAsync("b", Ct);
        await Library.AddSearchAsync("c", Ct);
        Assert.Equal(new[] { "c", "b" }, await RecentAsync(2));
    }

    [Fact]
    public async Task Searches_RepeatRefreshesTimestamp()
    {
        await Library.AddSearchAsync("one", Ct);
        await Library.AddSearchAsync("two", Ct);
        await Library.AddSearchAsync("ONE", Ct);
        Assert.Equal(new[] { "one", "two" }, await RecentAsync());
    }

    [Fact]
    public async Task ContinueWatching_OrderAndLastEpisode()
    {
        await Library.SaveProgressAsync(_a, "1", 10, 100, Ct);
        await Library.SaveProgressAsync(_a, "2", 20, 100, Ct);
        await Library.SaveProgressAsync(_b, "7", 5, 50, Ct);

        var list = await ContinueAsync();
        Assert.Equal(new[] { "b1", "a1" }, list.Select(e => e.Anime.Id));
        Assert.Equal("2", list[1].Last.EpisodeNumber);

        await Library.SaveProgressAsync(_a, "1", 30, 100, Ct);
        list = await ContinueAsync();
        Assert.Equal(new[] { "a1", "b1" }, list.Select(e => e.Anime.Id));
        Assert.Equal("1", list[0].Last.EpisodeNumber);
        Assert.Equal(30, (await Library.GetProgressAsync("hianime", "a1", "1", Ct))!.PositionMs);
        Assert.Equal(2, (await TakeAsync(Library.ObserveProgressForAnime("hianime", "a1", Ct), 1))[0].Count);

        await Library.RemoveFromHistoryAsync("hianime", "a1", Ct);
        Assert.Equal(new[] { "b1" }, (await ContinueAsync()).Select(e => e.Anime.Id));
        Assert.Null(await Library.GetProgressAsync("hianime", "a1", "1", Ct));
    }

    [Fact]
    public async Task ContinueWatching_ExcludesAnimeWithoutProgressOrLastWatched()
    {
        await Library.SetFavoriteAsync(_a, true, Ct);
        Assert.Empty(await ContinueAsync());
    }

    [Fact]
    public async Task SaveProgress_RefreshesMetadataWithCoalesceSemantics()
    {
        var rich = new Anime
        {
            Id = "a1", ProviderId = "hianime", Title = "Old", CoverUrl = "cover", Synopsis = "syn", MalId = 7, SubEpisodes = 12,
            Alternates = new Dictionary<string, string> { ["shinden"] = "s1" },
        };
        await Library.SaveProgressAsync(rich, "1", 1, 2, Ct);

        // Null cover/synopsis/malId/counts/alternates must not erase; title always updates.
        await Library.SaveProgressAsync(MakeAnime("a1", "New"), "2", 1, 2, Ct);
        var entry = (await ContinueAsync()).Single();
        Assert.Equal("New", entry.Anime.Title);
        Assert.Equal("cover", entry.Anime.CoverUrl);
        Assert.Equal("syn", entry.Anime.Synopsis);
        Assert.Equal(7, entry.Anime.MalId);
        Assert.Equal(12, entry.Anime.SubEpisodes);
        Assert.Equal("s1", entry.Anime.Alternates["shinden"]);
    }

    [Fact]
    public async Task Favorites_SurviveProgress_OrderedNewestFirst_AndHistoryRemoval()
    {
        await Library.SetFavoriteAsync(_a, true, Ct);
        Assert.True((await TakeAsync(Library.ObserveIsFavorite("hianime", "a1", Ct), 1))[0]);
        await Library.SaveProgressAsync(_a, "1", 1, 2, Ct);
        await Library.SetFavoriteAsync(_b, true, Ct);

        var favs = (await TakeAsync(Library.ObserveFavorites(Ct), 1))[0];
        Assert.Equal(new[] { "b1", "a1" }, favs.Select(f => f.Id));
        Assert.Single(await ContinueAsync());

        await Library.RemoveFromHistoryAsync("hianime", "a1", Ct);
        Assert.Contains("a1", (await TakeAsync(Library.ObserveFavorites(Ct), 1))[0].Select(f => f.Id));

        await Library.SetFavoriteAsync(_a, false, Ct);
        Assert.False((await TakeAsync(Library.ObserveIsFavorite("hianime", "a1", Ct), 1))[0]);
        Assert.False((await TakeAsync(Library.ObserveIsFavorite("hianime", "zzz", Ct), 1))[0]);
    }

    [Fact]
    public async Task Observe_YieldsCurrentThenDistinctChanges()
    {
        var emissions = await TakeAsync(
            Library.ObserveFavorites(Ct),
            2,
            async i =>
            {
                // An unrelated anime-table change must not produce an (equal) emission.
                await Library.SaveProgressAsync(_b, "1", 1, 2, Ct);
                await Library.SetFavoriteAsync(_a, true, Ct);
            });

        Assert.Equal(2, emissions.Count);
        Assert.Empty(emissions[0]);
        Assert.Equal(new[] { "a1" }, emissions[1].Select(f => f.Id));
    }

    [Fact]
    public async Task Observe_EndsQuietlyOnCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var count = 0;
        var task = Task.Run(
            async () =>
            {
                await foreach (var _ in Library.ObserveRecentSearches(cancellationToken: cts.Token))
                {
                    count++;
                }
            },
            Ct);

        await Task.Delay(200, Ct);
        await cts.CancelAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ObserveProgressForAnime_ReactsToSaves()
    {
        var emissions = await TakeAsync(
            Library.ObserveProgressForAnime("hianime", "a1", Ct),
            2,
            _ => Library.SaveProgressAsync(_a, "3", 5, 10, Ct));
        Assert.Empty(emissions[0]);
        Assert.Equal("3", emissions[1].Single().EpisodeNumber);
    }
}
