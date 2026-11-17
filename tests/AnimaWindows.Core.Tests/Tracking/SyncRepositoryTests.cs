#pragma warning disable xUnit1051
using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tracking;

namespace AnimaWindows.Core.Tests.Tracking;

/// <summary>Port of core/sync SyncTest.kt plus queue-file and 85% tests.</summary>
public sealed class SyncRepositoryTests : IDisposable
{
    private readonly SyncEnv _env = new();

    public void Dispose() => _env.Dispose();

    private static Anime Show(int? malId) => new() { Id = "x", ProviderId = "p", Title = "t", MalId = malId };

    [Fact]
    public void Pkce_VerifierAndChallenge()
    {
        var v = Pkce.Verifier();
        Assert.Equal(96, v.Length);
        Assert.All(v, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'));
        Assert.Equal(v, Pkce.Challenge(v));
        Assert.NotEqual(v, Pkce.Verifier());
        Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.Verifier(10));
    }

    [Fact]
    public void NotConfigured_HasNoAuthUrl()
    {
        using var env = new SyncEnv(new SyncConfig());
        Assert.Null(env.Repo.AuthorizeUrl(SyncService.Mal));
        Assert.Null(env.Repo.AuthorizeUrl(SyncService.AniList));
        Assert.False(env.Repo.State.Services[SyncService.Mal].Configured);
    }

    [Fact]
    public void AniListAuthUrl_UsesImplicitGrant()
    {
        Assert.Equal(
            "https://anilist.co/api/v2/oauth/authorize?client_id=al&response_type=token",
            _env.Repo.AuthorizeUrl(SyncService.AniList));
    }

    [Fact]
    public void MalAuthUrl_UsesPlainPkce()
    {
        var url = _env.Repo.AuthorizeUrl(SyncService.Mal)!;
        Assert.StartsWith("https://mal.test/oauth/authorize?response_type=code", url);
        Assert.Contains("code_challenge_method=plain", url);
        Assert.Contains("redirect_uri=animawindows%3A%2F%2Fauth%2Fmal", url);
        Assert.Contains("client_id=mal", url);
    }

    [Fact]
    public async Task MalRedirect_ExchangesCodeWithVerifier()
    {
        var url = _env.Repo.AuthorizeUrl(SyncService.Mal)!;
        var state = System.Text.RegularExpressions.Regex.Match(url, "state=([^&]+)").Groups[1].Value;
        var challenge = System.Text.RegularExpressions.Regex.Match(url, "code_challenge=([^&]+)").Groups[1].Value;
        Assert.Equal(96, challenge.Length);
        _env.Http.Enqueue("""{"access_token":"A","refresh_token":"R","expires_in":3600}""");
        _env.Http.Enqueue("""{"name":"miryam"}""");

        Assert.True(await _env.Repo.HandleRedirectAsync($"animawindows://auth/mal?code=CODE&state={state}"));

        var tokenRequest = _env.Http.Requests[0];
        Assert.Equal("https://mal.test/oauth/token", tokenRequest.Uri.ToString());
        Assert.Contains("code=CODE", tokenRequest.Body);
        Assert.Contains($"code_verifier={challenge}", tokenRequest.Body);
        Assert.Contains("grant_type=authorization_code", tokenRequest.Body);
        Assert.Equal("Bearer A", _env.Http.Requests[1].Authorization);
        var stored = _env.Tokens.Get(SyncService.Mal)!;
        Assert.Equal("miryam", stored.Username);
        Assert.Equal(1_000_000 + 3_600_000, stored.ExpiresAt);
        Assert.True(_env.Repo.State.Services[SyncService.Mal].Connected);
    }

    [Fact]
    public async Task MalRedirect_RejectsWrongState()
    {
        _env.Repo.AuthorizeUrl(SyncService.Mal);
        Assert.True(await _env.Repo.HandleRedirectAsync("animawindows://auth/mal?code=C&state=bad"));
        Assert.Null(_env.Tokens.Get(SyncService.Mal));
        Assert.Empty(_env.Http.Requests);
        Assert.NotNull(_env.Repo.State.Error);
    }

    [Fact]
    public async Task AniListImplicitRedirect_StoresToken()
    {
        _env.Http.Enqueue("""{"data":{"Viewer":{"name":"bob"}}}""");
        Assert.True(await _env.Repo.HandleRedirectAsync("animawindows://auth/anilist#access_token=TOK&token_type=Bearer&expires_in=3600"));
        Assert.Equal("TOK", _env.Tokens.Get(SyncService.AniList)?.Access);
        Assert.Equal("bob", _env.Repo.State.Services[SyncService.AniList].Username);
    }

    [Fact]
    public async Task Redirect_ForeignUrisAreNotHandled()
    {
        Assert.False(await _env.Repo.HandleRedirectAsync("https://example.com/auth/mal"));
        Assert.False(await _env.Repo.HandleRedirectAsync("animawindows://other/mal"));
        Assert.False(await _env.Repo.HandleRedirectAsync("animawindows://auth/unknown"));
        Assert.False(await _env.Repo.HandleRedirectAsync("not a uri"));
    }

    [Fact]
    public async Task AniListScrobble_SendsLookupThenCompletedMutation()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Http.Enqueue("""{"data":{"Media":{"id":99,"episodes":12,"mediaListEntry":{"progress":10,"status":"CURRENT"}}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1,"progress":12,"status":"COMPLETED"}}}""");

        await _env.Repo.RecordAsync(5, 12, null);
        await _env.Repo.SyncNowAsync();

        var lookup = _env.Http.Requests[0];
        Assert.Equal("Bearer TOK", lookup.Authorization);
        Assert.Equal(SyncEnv.AniListEndpoint, lookup.Uri.ToString());
        Assert.Contains("\"idMal\":5", lookup.Body);
        Assert.Contains("Media(idMal: $idMal, type: ANIME)", lookup.Body);
        var save = _env.Http.Requests[1].Body;
        Assert.Contains("SaveMediaListEntry", save);
        Assert.Contains("\"mediaId\":99", save);
        Assert.Contains("\"progress\":12", save);
        Assert.Contains("\"status\":\"COMPLETED\"", save);
        Assert.Equal(0, _env.Repo.State.Pending);
    }

    [Fact]
    public async Task AniListScrobble_UsesCurrentBelowEpisodeCount()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Http.Enqueue("""{"data":{"Media":{"id":99,"episodes":12,"mediaListEntry":null}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1}}}""");
        await _env.Repo.RecordAsync(5, 3, null);
        await _env.Repo.SyncNowAsync();
        Assert.Contains("\"status\":\"CURRENT\"", _env.Http.Requests[1].Body);
    }

    [Fact]
    public async Task DoesNotRegressRemoteProgress()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Http.Enqueue("""{"data":{"Media":{"id":99,"episodes":12,"mediaListEntry":{"progress":8,"status":"CURRENT"}}}}""");
        await _env.Repo.RecordAsync(5, 3, null);
        await _env.Repo.SyncNowAsync();
        Assert.Single(_env.Http.Requests);
        Assert.Empty(_env.Queue.Load());
    }

    [Fact]
    public async Task MalScrobble_SendsPatchForm()
    {
        _env.Tokens.Put(SyncService.Mal, new Tokens("A", "R", 1_000_000 + 3_600_000));
        _env.Http.Enqueue("""{"num_episodes":24,"my_list_status":{"num_episodes_watched":2}}""");
        _env.Http.Enqueue("""{"status":"watching","num_episodes_watched":3}""");

        await _env.Repo.RecordAsync(7, 3, null);
        await _env.Repo.SyncNowAsync();

        var get = _env.Http.Requests[0];
        Assert.Equal("GET", get.Method);
        Assert.Equal("https://mal.test/v2/anime/7?fields=num_episodes,my_list_status", get.Uri.ToString());
        var patch = _env.Http.Requests[1];
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal("https://mal.test/v2/anime/7/my_list_status", patch.Uri.ToString());
        Assert.Equal("Bearer A", patch.Authorization);
        Assert.Contains("status=watching", patch.Body);
        Assert.Contains("num_watched_episodes=3", patch.Body);
    }

    [Fact]
    public async Task MalScrobble_CompletesAtEpisodeCount()
    {
        _env.Tokens.Put(SyncService.Mal, new Tokens("A"));
        _env.Http.Enqueue("""{"num_episodes":12,"my_list_status":{"num_episodes_watched":11}}""");
        _env.Http.Enqueue("{}");
        await _env.Repo.RecordAsync(7, 12, null);
        await _env.Repo.SyncNowAsync();
        Assert.Contains("status=completed", _env.Http.Requests[1].Body);
    }

    [Fact]
    public async Task QueueCoalescesPerShow_FurthestEpisodeWins_AndPersistsToFile()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        await _env.Repo.RecordAsync(5, 4, null);
        await _env.Repo.RecordAsync(5, 3, 12); // older episode: keeps 4, remembers the total
        await _env.Repo.RecordAsync(6, 1, null);

        var items = new SyncQueueStore(_env.Dir.Path).Load(); // fresh instance = what the next app start sees
        Assert.Equal(2, items.Count);
        var show5 = items.Single(i => i.MalId == 5);
        Assert.Equal(4, show5.Episode);
        Assert.Equal(12, show5.Total);
        Assert.True(File.Exists(Path.Combine(_env.Dir.Path, "sync_queue.json")));
        Assert.Equal(2, _env.Repo.State.Pending);
    }

    [Fact]
    public async Task QueueMergesServices()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        await _env.Repo.RecordAsync(5, 1, null);
        _env.Tokens.Put(SyncService.Mal, new Tokens("A"));
        await _env.Repo.RecordAsync(5, 2, null);
        var item = Assert.Single(_env.Queue.Load());
        Assert.Equal(2, item.Services.Count);
    }

    [Fact]
    public async Task Record_WithoutConnectedService_QueuesNothing()
    {
        await _env.Repo.RecordAsync(5, 1, null);
        Assert.Empty(_env.Queue.Load());
    }

    [Fact]
    public void CorruptQueueFile_ReadsAsEmpty()
    {
        File.WriteAllText(Path.Combine(_env.Dir.Path, "sync_queue.json"), "{ not json");
        Assert.Empty(_env.Queue.Load());
    }

    [Fact]
    public async Task FailedUpdate_StaysQueued_AndRetriesIdempotently()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        await _env.Repo.RecordAsync(5, 4, null);
        _env.Http.Enqueue("down", HttpStatusCode.ServiceUnavailable);

        await _env.Repo.SyncNowAsync();
        Assert.Single(_env.Queue.Load());
        Assert.Equal(1, _env.Retries);
        Assert.NotNull(_env.Repo.State.Error);
        Assert.Equal(1, _env.Repo.State.Pending);

        _env.Http.Enqueue("""{"data":{"Media":{"id":1,"episodes":null,"mediaListEntry":null}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1}}}""");
        await _env.Repo.SyncNowAsync();
        Assert.Empty(_env.Queue.Load());
        Assert.Null(_env.Repo.State.Error);
    }

    [Fact]
    public async Task NetworkError_KeepsUpdateQueued_AndRequestsRetry()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        await _env.Repo.RecordAsync(5, 4, null);
        _env.Http.EnqueueFailure(new HttpRequestException("no route to host"));

        await _env.Repo.SyncNowAsync();

        Assert.Single(_env.Queue.Load());
        Assert.Equal(1, _env.Retries);
        Assert.False(_env.Repo.State.Syncing);
        Assert.Contains("no route", _env.Repo.State.Error);
    }

    [Fact]
    public async Task MalRefreshesExpiredToken_AndPersistsIt()
    {
        _env.Tokens.Put(SyncService.Mal, new Tokens("OLD", "R1", 1_000_000 - 1, "u"));
        _env.Http.Enqueue("""{"access_token":"NEW","refresh_token":"R2","expires_in":3600}""");
        _env.Http.Enqueue("""{"num_episodes":12,"my_list_status":null}""");
        _env.Http.Enqueue("{}");

        await _env.Repo.RecordAsync(7, 1, null);
        await _env.Repo.SyncNowAsync();

        var refresh = _env.Http.Requests[0];
        Assert.Equal("https://mal.test/oauth/token", refresh.Uri.ToString());
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=R1", refresh.Body);
        Assert.Contains("client_id=mal", refresh.Body);
        Assert.Equal("Bearer NEW", _env.Http.Requests[1].Authorization);
        var t = _env.Tokens.Get(SyncService.Mal)!;
        Assert.Equal("NEW", t.Access);
        Assert.Equal("R2", t.Refresh);
        Assert.Equal("u", t.Username);
    }

    [Fact]
    public async Task MalRefreshesSixtySecondsBeforeExpiry()
    {
        _env.Tokens.Put(SyncService.Mal, new Tokens("OLD", "R1", 1_000_000 + 59_000));
        _env.Http.Enqueue("""{"access_token":"NEW","expires_in":3600}""");
        _env.Http.Enqueue("""{"num_episodes":12}""");
        _env.Http.Enqueue("{}");
        await _env.Repo.RecordAsync(7, 1, null);
        await _env.Repo.SyncNowAsync();
        Assert.Equal("https://mal.test/oauth/token", _env.Http.Requests[0].Uri.ToString());

        // far from expiry: no refresh
        _env.Tokens.Put(SyncService.Mal, new Tokens("OK", "R1", 1_000_000 + 120_000));
        _env.Http.Enqueue("""{"num_episodes":12}""");
        _env.Http.Enqueue("{}");
        await _env.Repo.RecordAsync(8, 1, null);
        await _env.Repo.SyncNowAsync();
        Assert.Equal("https://mal.test/v2/anime/8?fields=num_episodes,my_list_status", _env.Http.Requests[3].Uri.ToString());
    }

    [Fact]
    public async Task MalRetriesOnceAfter401()
    {
        _env.Tokens.Put(SyncService.Mal, new Tokens("STALE", "R1", 1_000_000 + 3_600_000));
        _env.Http.Enqueue("{}", HttpStatusCode.Unauthorized);
        _env.Http.Enqueue("""{"access_token":"NEW","expires_in":3600}""");
        _env.Http.Enqueue("""{"num_episodes":12}""");
        _env.Http.Enqueue("{}");

        await _env.Repo.RecordAsync(7, 1, null);
        await _env.Repo.SyncNowAsync();

        Assert.Equal(4, _env.Http.Requests.Count);
        Assert.Empty(_env.Queue.Load());
        Assert.Equal("R1", _env.Tokens.Get(SyncService.Mal)?.Refresh); // kept when the server sends none
    }

    [Fact]
    public async Task RevokedRefreshToken_Disconnects()
    {
        _env.Tokens.Put(SyncService.Mal, new Tokens("OLD", "R1", 1_000_000 - 1));
        _env.Http.Enqueue("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);

        await _env.Repo.RecordAsync(7, 1, null);
        await _env.Repo.SyncNowAsync();

        Assert.Null(_env.Tokens.Get(SyncService.Mal));
        Assert.Empty(_env.Queue.Load());
        Assert.NotNull(_env.Repo.State.Error);
    }

    [Fact]
    public async Task Disconnect_ForgetsTokensAndDropsQueuedService()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Tokens.Put(SyncService.Mal, new Tokens("A"));
        await _env.Repo.RecordAsync(5, 2, null);
        await _env.Repo.DisconnectAsync(SyncService.Mal);
        Assert.Null(_env.Tokens.Get(SyncService.Mal));
        var item = Assert.Single(_env.Queue.Load());
        Assert.Equal(SyncService.AniList, Assert.Single(item.Services));
        await _env.Repo.DisconnectAsync(SyncService.AniList);
        Assert.Empty(_env.Queue.Load());
    }

    [Fact]
    public void OnEpisodeWatched_IgnoresShowsWithoutMalId_AndNonIntegerEpisodes()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Repo.OnEpisodeWatched(Show(null), "3", null);
        _env.Repo.OnEpisodeWatched(Show(1), "12.5", null);
        _env.Repo.OnEpisodeWatched(Show(1), "SP", null);
        _env.Repo.OnEpisodeWatched(Show(1), "0", null);
        Assert.Empty(_env.Http.Requests);
        Assert.Empty(_env.Queue.Load());
    }

    [Theory]
    [InlineData("1", true, 1)]
    [InlineData("12", true, 12)]
    [InlineData("12.0", true, 12)]
    [InlineData("12.5", false, 0)]
    [InlineData("0", false, 0)]
    [InlineData("-3", false, 0)]
    [InlineData("OVA", false, 0)]
    [InlineData("", false, 0)]
    public void TryParseEpisode(string text, bool ok, int expected)
    {
        Assert.Equal(ok, SyncRepository.TryParseEpisode(text, out var ep));
        Assert.Equal(expected, ep);
    }

    [Theory]
    [InlineData(84_999, 100_000, false)]
    [InlineData(85_000, 100_000, true)]
    [InlineData(100_000, 100_000, true)]
    [InlineData(50_000, 0, false)]
    public void WatchedAtEightyFivePercent(long position, long duration, bool watched) =>
        Assert.Equal(watched, SyncRepository.IsWatched(position, duration));

    [Fact]
    public async Task OnEpisodeWatched_QueuesAndDelivers()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Http.Enqueue("""{"data":{"Media":{"id":1,"episodes":12,"mediaListEntry":null}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1}}}""");
        await _env.Repo.OnEpisodeWatchedAsync(5, 3, null);
        Assert.Equal(2, _env.Http.Requests.Count);
        Assert.Empty(_env.Queue.Load());
    }

    [Fact]
    public async Task ReportProgress_OnlyFiresPastEightyFivePercent_OncePerEpisode()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        _env.Http.Enqueue("""{"data":{"Media":{"id":1,"episodes":12,"mediaListEntry":null}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1}}}""");
        var show = Show(5);

        _env.Repo.ReportProgress(show, "3", 10_000, 100_000, null);
        Assert.Empty(_env.Http.Requests);

        _env.Repo.ReportProgress(show, "3", 90_000, 100_000, null);
        _env.Repo.ReportProgress(show, "3", 95_000, 100_000, null); // duplicate: ignored
        await WaitUntilAsync(() => _env.Http.Requests.Count >= 2);
        await Task.Delay(50);
        Assert.Equal(2, _env.Http.Requests.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }
}
