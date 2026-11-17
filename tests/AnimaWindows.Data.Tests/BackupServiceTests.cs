using System.Text;
using System.Text.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data.Tests;

public class BackupServiceTests : DataTestBase
{
    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task<string> ExportAsync(BackupService service, string? settings = null)
    {
        using var ms = new MemoryStream();
        await service.ExportAsync(ms, settings, Ct);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task Export_UsesAndroidFieldNames()
    {
        await Library.SaveProgressAsync(MakeAnime("a1", "Alpha"), "1", 10, 100, Ct);
        var json = await ExportAsync(Backup, """{"s":1}""");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("""{"s":1}""", root.GetProperty("settings").GetString());
        var anime = root.GetProperty("anime")[0];
        foreach (var name in new[] { "providerId", "animeId", "title", "coverUrl", "synopsis", "malId", "subEpisodes", "dubEpisodes", "isFavorite", "favoritedAt", "lastWatchedAt", "alternates" })
        {
            Assert.True(anime.TryGetProperty(name, out _), name);
        }

        var progress = root.GetProperty("progress")[0];
        Assert.Equal("1", progress.GetProperty("episodeNumber").GetString());
        Assert.Equal(10, progress.GetProperty("positionMs").GetInt64());
        Assert.True(root.TryGetProperty("searches", out _));
    }

    [Fact]
    public async Task RoundTrip_ThenOlderIncomingDoesNotOverwriteNewerLocal()
    {
        await Library.SaveProgressAsync(MakeAnime("a1", "Alpha", cover: "c"), "1", 10, 100, Ct);
        await Library.SetFavoriteAsync(MakeAnime("b1", "Beta"), true, Ct);
        await Library.AddSearchAsync("x", Ct);
        var json = await ExportAsync(Backup, """{"s":1}""");

        var db2 = NewDb("second.db");
        var lib2 = new SqliteLibraryRepository(db2, Clock);
        var backup2 = new BackupService(db2, Clock);
        var result = await backup2.ImportAsync(Utf8(json), Ct);
        Assert.Equal("""{"s":1}""", result.SettingsJson);
        Assert.Equal(2, result.Anime);
        Assert.Equal(1, result.Progress);
        Assert.Equal(1, result.Searches);

        Assert.Equal(10, (await lib2.GetProgressAsync("hianime", "a1", "1", Ct))!.PositionMs);
        Assert.Equal(new[] { "b1" }, (await TakeAsync(lib2.ObserveFavorites(Ct), 1))[0].Select(a => a.Id));
        Assert.Equal(new[] { "x" }, (await TakeAsync(lib2.ObserveRecentSearches(cancellationToken: Ct), 1))[0]);

        await lib2.SaveProgressAsync(MakeAnime("a1", "Alpha"), "1", 99, 100, Ct);
        await backup2.ImportAsync(Utf8(json), Ct);
        Assert.Equal(99, (await lib2.GetProgressAsync("hianime", "a1", "1", Ct))!.PositionMs);
    }

    [Fact]
    public async Task Import_ProgressIsReplacedOnlyWhenIncomingIsNewer()
    {
        await Library.SaveProgressAsync(MakeAnime("a1"), "1", 5, 100, Ct);

        await Backup.ImportAsync(Utf8(BackupJson(progressUpdatedAt: 1, positionMs: 77)), Ct);
        Assert.Equal(5, (await Library.GetProgressAsync("hianime", "a1", "1", Ct))!.PositionMs);

        await Backup.ImportAsync(Utf8(BackupJson(progressUpdatedAt: long.MaxValue / 2, positionMs: 77)), Ct);
        Assert.Equal(77, (await Library.GetProgressAsync("hianime", "a1", "1", Ct))!.PositionMs);
    }

    private static string BackupJson(long progressUpdatedAt, long positionMs) =>
        "{\"version\":1,\"anime\":[{\"providerId\":\"hianime\",\"animeId\":\"a1\",\"title\":\"T\"}],"
        + "\"progress\":[{\"providerId\":\"hianime\",\"animeId\":\"a1\",\"episodeNumber\":\"1\",\"positionMs\":" + positionMs
        + ",\"durationMs\":100,\"updatedAt\":" + progressUpdatedAt + "}]}";

    [Fact]
    public async Task Merge_UnionsAlternates_NewerFavouriteAndWatchedWin_KeepsExistingMetadata()
    {
        // Local: not favourite, watched at 500, has alternates {y} and a cover.
        await Library.SaveProgressAsync(
            new Anime
            {
                Id = "a1", ProviderId = "hianime", Title = "Local", CoverUrl = "local-cover",
                Alternates = new Dictionary<string, string> { ["y"] = "2", ["z"] = "local" },
            },
            "1", 1, 2, Ct);
        var local = (await TakeAsync(Library.ObserveContinueWatching(Ct), 1))[0].Single();

        var incoming = """
            {"version":1,"anime":[{"providerId":"hianime","animeId":"a1","title":"Incoming","coverUrl":"in-cover","synopsis":"syn","malId":9,
              "isFavorite":true,"favoritedAt":5,"lastWatchedAt":1,"alternates":{"x":"1","z":"incoming"}}]}
            """;
        await Backup.ImportAsync(Utf8(incoming), Ct);

        var fav = (await TakeAsync(Library.ObserveFavorites(Ct), 1))[0].Single();
        Assert.Equal("Local", fav.Title); // local lastWatchedAt is newer, so its base row wins
        Assert.Equal("local-cover", fav.CoverUrl);
        Assert.Equal("syn", fav.Synopsis); // missing locally, filled from incoming
        Assert.Equal(9, fav.MalId);
        Assert.Equal("1", fav.Alternates["x"]);
        Assert.Equal("2", fav.Alternates["y"]);
        Assert.Equal("local", fav.Alternates["z"]); // existing wins on key clash

        var after = (await TakeAsync(Library.ObserveContinueWatching(Ct), 1))[0].Single();
        Assert.Equal(local.Last, after.Last);
    }

    [Fact]
    public async Task Import_AndroidStyleFile_WithNullsAndUnknownFields()
    {
        var json = """
            {"version":1,"futureField":true,
             "anime":[{"providerId":"hianime","animeId":"x","title":"T","coverUrl":null,"synopsis":null,"malId":5,"subEpisodes":12,"dubEpisodes":null,
                       "isFavorite":true,"favoritedAt":100,"lastWatchedAt":null,"alternates":{"shinden":"s1"}}],
             "progress":[{"providerId":"hianime","animeId":"x","episodeNumber":"12.5","positionMs":1000,"durationMs":2000,"updatedAt":7}],
             "searches":[{"query":"  Naruto ","searchedAt":5},{"query":"   ","searchedAt":6}],
             "settings":null}
            """;
        var result = await Backup.ImportAsync(Utf8(json), Ct);
        Assert.Equal(1, result.Anime);
        Assert.Equal(2, result.Searches);
        Assert.Null(result.SettingsJson);

        var fav = (await TakeAsync(Library.ObserveFavorites(Ct), 1))[0].Single();
        Assert.Equal(12, fav.SubEpisodes);
        Assert.Equal("s1", fav.Alternates["shinden"]);
        Assert.Equal(1000, (await Library.GetProgressAsync("hianime", "x", "12.5", Ct))!.PositionMs);
        Assert.Equal(new[] { "naruto" }, (await TakeAsync(Library.ObserveRecentSearches(cancellationToken: Ct), 1))[0]);
    }

    [Fact]
    public async Task Import_PreAlternatesFile_DefaultsToEmpty()
    {
        await Backup.ImportAsync(Utf8("""{"version":1,"anime":[{"providerId":"p","animeId":"a","title":"T","isFavorite":true}]}"""), Ct);
        Assert.Empty((await TakeAsync(Library.ObserveFavorites(Ct), 1))[0].Single().Alternates);
    }

    [Fact]
    public async Task Import_InvalidInputThrowsJsonException()
    {
        await Assert.ThrowsAnyAsync<JsonException>(() => Backup.ImportAsync(Utf8("garbage"), Ct));
        await Assert.ThrowsAnyAsync<JsonException>(() => Backup.ImportAsync(Utf8("""{"version":2}"""), Ct));
        await Assert.ThrowsAnyAsync<JsonException>(() => Backup.ImportAsync(Utf8("""{"anime":[{"providerId":"p"}]}"""), Ct));
    }

    [Fact]
    public async Task Import_CapsSearchesAtFifty()
    {
        var searches = string.Join(",", Enumerable.Range(0, 70).Select(i => "{\"query\":\"q" + i + "\",\"searchedAt\":" + (i + 1) + "}"));
        await Backup.ImportAsync(Utf8("{\"version\":1,\"searches\":[" + searches + "]}"), Ct);
        var recent = (await TakeAsync(Library.ObserveRecentSearches(100, Ct), 1))[0];
        Assert.Equal(50, recent.Count);
        Assert.Equal("q69", recent[0]);
    }

    [Fact]
    public async Task AniCliHistory_ImportsFinishedProgressForHianime()
    {
        var text = "5\tabc123\tOne Piece (1000 episodes)\n12\tdef\tBleach\r\n\nbad line\n";
        var count = await Backup.ImportAniCliHistoryAsync(Utf8(text), 1000, Ct);
        Assert.Equal(2, count);

        var list = (await TakeAsync(Library.ObserveContinueWatching(Ct), 1))[0];
        Assert.Equal(2, list.Count);
        var op = list.First(e => e.Anime.Id == "abc123");
        Assert.Equal("hianime", op.Anime.ProviderId);
        Assert.Equal("One Piece (1000 episodes)", op.Anime.Title);
        Assert.Equal("5", op.Last.EpisodeNumber);
        Assert.Equal(1, op.Last.PositionMs);
        Assert.Equal(1, op.Last.DurationMs);
        Assert.True(op.Last.IsFinished);
        Assert.Equal("Bleach", list.First(e => e.Anime.Id == "def").Anime.Title);
    }

    [Fact]
    public async Task AniCliHistory_KeepsExistingProgressRows()
    {
        await Library.SaveProgressAsync(MakeAnime("abc", "Show"), "5", 50, 100, Ct);
        await Backup.ImportAniCliHistoryAsync(Utf8("5\tabc\tShow\n"), 1000, Ct);
        Assert.Equal(50, (await Library.GetProgressAsync("hianime", "abc", "5", Ct))!.PositionMs);
    }
}
