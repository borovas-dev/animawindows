using AnimaWindows.Core.Models;

namespace AnimaWindows.Data.Tests;

public class DownloadRepositoryTests : DataTestBase
{
    private static readonly StreamSet Set = new()
    {
        MasterUrl = null,
        Variants = [new StreamVariant(720, "https://cdn/720.m3u8")],
        Headers = new Dictionary<string, string> { ["Referer"] = "https://site/" },
        ExternalAudioUrl = "https://cdn/audio.m3u8",
        Subtitles = [new SubtitleTrack("https://cdn/en.vtt", "en", "English", true)],
    };

    private static DownloadItem Item(string anime, string ep, long created, DownloadState state = DownloadState.Queued, string? cover = null) =>
        new("hianime", anime, "id-" + ep, ep, "Title " + anime, cover, TranslationMode.Sub, 720, state, 0, 0f, created, Set);

    [Fact]
    public async Task Upsert_RoundTripsSnapshotAndReplacesExisting()
    {
        await Downloads.UpsertAsync(Item("a", "1", 10), Ct);
        var got = (await Downloads.GetAsync("hianime", "a", "1", Ct))!;
        Assert.Equal(Set.Variants, got.StreamSet!.Variants);
        Assert.Equal("https://site/", got.StreamSet.Headers["Referer"]);
        Assert.Equal("https://cdn/audio.m3u8", got.StreamSet.ExternalAudioUrl);
        Assert.Equal("en", got.StreamSet.Subtitles[0].Language);
        Assert.Equal(DownloadState.Queued, got.State);
        Assert.Equal(TranslationMode.Sub, got.Mode);
        Assert.Equal(720, got.Quality);
        Assert.Null(await Downloads.GetAsync("hianime", "a", "2", Ct));

        await Downloads.UpsertAsync(Item("a", "1", 11, DownloadState.Completed) with { Mode = TranslationMode.Dub, StreamSet = null }, Ct);
        got = (await Downloads.GetAsync("hianime", "a", "1", Ct))!;
        Assert.Equal(DownloadState.Completed, got.State);
        Assert.Equal(TranslationMode.Dub, got.Mode);
        Assert.Null(got.StreamSet);
        Assert.Single(await Downloads.GetAllAsync(Ct));
    }

    [Fact]
    public async Task UpdateProgress_OnlyTouchesExistingRowsAndKeepsSnapshot()
    {
        await Downloads.UpsertAsync(Item("a", "1", 10), Ct);
        await Downloads.UpdateProgressAsync("hianime", "a", "1", DownloadState.Downloading, 500, 0.25f, null, Ct);
        await Downloads.UpdateProgressAsync("hianime", "a", "99", DownloadState.Downloading, 500, 0.25f, null, Ct);
        var got = (await Downloads.GetAsync("hianime", "a", "1", Ct))!;
        Assert.Equal(DownloadState.Downloading, got.State);
        Assert.Equal(500, got.Bytes);
        Assert.Equal(0.25f, got.Percent);
        Assert.NotNull(got.StreamSet);
        Assert.Null(await Downloads.GetAsync("hianime", "a", "99", Ct));
    }

    [Fact]
    public async Task FailureReason_RoundTripsAndIsClearedByProgressUpdate()
    {
        await Downloads.UpsertAsync(Item("a", "1", 1), Ct);
        await Downloads.UpdateProgressAsync("hianime", "a", "1", DownloadState.Failed, 0, 0f, "Server returned HTTP 403", Ct);
        Assert.Equal("Server returned HTTP 403", (await Downloads.GetAsync("hianime", "a", "1", Ct))!.FailureReason);
        await Downloads.UpdateProgressAsync("hianime", "a", "1", DownloadState.Downloading, 10, 0.1f, null, Ct);
        Assert.Null((await Downloads.GetAsync("hianime", "a", "1", Ct))!.FailureReason);
    }

    [Fact]
    public async Task Grouped_SortsEpisodesNumericallyAndGroupsNewestFirst()
    {
        await Downloads.UpsertAsync(Item("a", "10", 1), Ct);
        await Downloads.UpsertAsync(Item("a", "2", 2, cover: "c"), Ct);
        await Downloads.UpsertAsync(Item("a", "special", 3), Ct);
        await Downloads.UpsertAsync(Item("b", "1", 50), Ct);

        var groups = (await TakeAsync(Downloads.ObserveGroupedByAnime(Ct), 1))[0];
        Assert.Equal(new[] { "b", "a" }, groups.Select(g => g.AnimeId));
        Assert.Equal(new[] { "2", "10", "special" }, groups[1].Items.Select(i => i.EpisodeNumber));
        Assert.Equal("c", groups[1].CoverUrl);
    }

    [Fact]
    public async Task ObserveFlows_ReflectUpsertAndDelete()
    {
        await Downloads.UpsertAsync(Item("a", "1", 1, DownloadState.Completed), Ct);
        await Downloads.UpsertAsync(Item("a", "2", 2), Ct);
        await Downloads.UpsertAsync(Item("b", "1", 3), Ct);

        var map = (await TakeAsync(Downloads.ObserveForAnime("hianime", "a", Ct), 1))[0];
        Assert.Equal(new[] { "1", "2" }, map.Keys.Order());
        Assert.True(map["1"].IsPlayable);
        Assert.False(map["2"].IsPlayable);

        var all = (await TakeAsync(Downloads.ObserveDownloads(Ct), 1))[0];
        Assert.Equal(new[] { "b", "a", "a" }, all.Select(d => d.AnimeId));

        var single = await TakeAsync(
            Downloads.Observe("hianime", "a", "1", Ct),
            3,
            async i =>
            {
                if (i == 0)
                {
                    await Downloads.UpdateProgressAsync("hianime", "a", "1", DownloadState.Paused, 5, 0.5f, "Paused by system", Ct);
                }
                else
                {
                    await Downloads.DeleteAsync("hianime", "a", "1", Ct);
                }
            });
        Assert.Equal(DownloadState.Completed, single[0]!.State);
        Assert.Equal(DownloadState.Paused, single[1]!.State);
        Assert.Null(single[2]);
        Assert.Equal(2, (await Downloads.GetAllAsync(Ct)).Count);
    }

    [Fact]
    public async Task UnknownStateOrCorruptSnapshot_DegradeGracefully()
    {
        await Downloads.UpsertAsync(Item("a", "1", 1), Ct);
        Db.Read(c => c.Exec(null, "UPDATE download SET state = 'WAT', streamSetJson = '{broken'"));
        var got = (await Downloads.GetAsync("hianime", "a", "1", Ct))!;
        Assert.Equal(DownloadState.Failed, got.State);
        Assert.Null(got.StreamSet);
    }
}
