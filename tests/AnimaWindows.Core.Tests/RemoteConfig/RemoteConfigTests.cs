#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.RemoteConfig;
using AnimaWindows.Core.Tests.Net;

namespace AnimaWindows.Core.Tests.RemoteConfig;

public sealed class RemoteConfigTests : IDisposable
{
    /// <summary>Stand-in for Ed25519: "signature" = SHA-256 of the message, verified against the same.</summary>
    private sealed class HashVerifier : IEd25519Verifier
    {
        public bool Verify(byte[] publicKey, byte[] message, byte[] signature) => SHA256.HashData(message).SequenceEqual(signature);
    }

    private static readonly byte[] Key = new byte[32];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "anima-rc-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundledPath;
    private const string Url = "https://cfg.test/providers.json";

    public RemoteConfigTests()
    {
        Directory.CreateDirectory(_dir);
        _bundledPath = Path.Combine(_dir, "bundled.json");
        File.WriteAllText(_bundledPath, Doc(1, "https://bundled.example"));
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string CacheDir => Path.Combine(_dir, "cache");

    private static string Doc(int rev, string url = "https://x.example", string? min = null) =>
        "{\"revision\":" + rev + ",\"providers\":{\"p\":{\"baseUrls\":[\"" + url + "/\"],\"minAppVersion\":" +
        (min is null ? "null" : "\"" + min + "\"") + ",\"params\":{\"k\":\"v\"}}}}";

    private static string Sign(string body) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private RemoteConfigRepository Repo(FakeHttpMessageHandler h, bool allowUnsigned = false, byte[]? key = null, string version = "1.0.0") =>
        new(new HttpClient(h), CacheDir, _bundledPath,
            new RemoteConfigOptions(version, key ?? Key, allowUnsigned, Url), new HashVerifier());

    [Fact]
    public void UsesBundledWhenNothingFetched()
    {
        var r = Repo(new FakeHttpMessageHandler());
        Assert.Equal("https://bundled.example", r.ForProvider("p").BaseUrl);
        Assert.Equal(ProviderRemoteConfig.Default, r.ForProvider("unknown"));
        Assert.True(r.ForProvider("unknown").Enabled);
    }

    [Fact]
    public void MissingBundledFileGivesEmptyDocument()
    {
        var r = new RemoteConfigRepository(new HttpClient(new FakeHttpMessageHandler()), CacheDir, Path.Combine(_dir, "nope.json"), new RemoteConfigOptions("1.0.0"));
        Assert.Equal(0, r.Current.Revision);
    }

    [Fact]
    public async Task ValidSignatureIsAdoptedAndCached()
    {
        var body = Doc(2);
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, body, ("ETag", "\"abc\"")).Enqueue(HttpStatusCode.OK, Sign(body));
        var r = Repo(h);
        var changed = 0;
        r.Changed += (_, _) => changed++;
        Assert.True(await r.RefreshAsync());
        Assert.Equal(1, changed);
        Assert.Equal("https://x.example", r.ForProvider("p").BaseUrl);
        Assert.Equal("v", r.ForProvider("p").Param("k"));
        Assert.Equal(2, r.Current.Revision);
        Assert.Equal(Url + ".sig", h.Requests[1].Uri.ToString());
        // A fresh instance reads the verified cache.
        Assert.Equal("https://x.example", Repo(new FakeHttpMessageHandler()).ForProvider("p").BaseUrl);
    }

    [Fact]
    public async Task BadSignatureFallsBack()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, Doc(2)).Enqueue(HttpStatusCode.OK, Sign(Doc(3)));
        var r = Repo(h);
        Assert.False(await r.RefreshAsync());
        Assert.Equal("https://bundled.example", r.ForProvider("p").BaseUrl);
        Assert.False(File.Exists(Path.Combine(CacheDir, "providers.json")));
    }

    [Fact]
    public async Task MissingSignatureRejectedUnlessAllowed()
    {
        var body = Doc(2);
        var h = new FakeHttpMessageHandler();
        for (var i = 0; i < 2; i++) h.Enqueue(HttpStatusCode.OK, body).Enqueue(HttpStatusCode.NotFound);
        Assert.False(await Repo(h, allowUnsigned: false).RefreshAsync());
        Assert.True(await Repo(h, allowUnsigned: true).RefreshAsync());
    }

    [Fact]
    public async Task PresentButInvalidSignatureRejectedEvenWhenUnsignedAllowed()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, Doc(2)).Enqueue(HttpStatusCode.OK, "AAAA");
        Assert.False(await Repo(h, allowUnsigned: true).RefreshAsync());
    }

    [Fact]
    public async Task NoKeyConfiguredRejectsPresentSignature()
    {
        var body = Doc(2);
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, body).Enqueue(HttpStatusCode.OK, Sign(body));
        var r = new RemoteConfigRepository(new HttpClient(h), CacheDir, _bundledPath, new RemoteConfigOptions("1.0.0", null, true, Url), new HashVerifier());
        Assert.False(await r.RefreshAsync());
    }

    [Fact]
    public void TamperedCacheIsIgnored()
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(Path.Combine(CacheDir, "providers.json"), Doc(9, "https://evil.example"));
        File.WriteAllText(Path.Combine(CacheDir, "providers.json.sig"), Sign(Doc(9, "https://other.example")));
        Assert.Equal("https://bundled.example", Repo(new FakeHttpMessageHandler()).ForProvider("p").BaseUrl);
    }

    [Fact]
    public async Task EtagSentAndNotModifiedKeepsCache()
    {
        var body = Doc(2);
        var h = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, body, ("ETag", "\"abc\""))
            .Enqueue(HttpStatusCode.OK, Sign(body))
            .Enqueue(HttpStatusCode.NotModified);
        var r = Repo(h);
        Assert.True(await r.RefreshAsync());
        Assert.False(await r.RefreshAsync());
        Assert.Equal("\"abc\"", h.Requests[2].Headers["If-None-Match"]);
        Assert.Equal("https://x.example", r.ForProvider("p").BaseUrl);
    }

    [Fact]
    public async Task RollbackToOlderRevisionIgnored()
    {
        var body = Doc(0, "https://old.example");
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, body).Enqueue(HttpStatusCode.OK, Sign(body));
        Assert.False(await Repo(h).RefreshAsync());
    }

    [Fact]
    public async Task NetworkErrorKeepsBundled()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.InternalServerError);
        var r = Repo(h);
        Assert.False(await r.RefreshAsync());
        Assert.Equal("https://bundled.example", r.ForProvider("p").BaseUrl);

        var throwing = new FakeHttpMessageHandler().Enqueue(_ => throw new HttpRequestException("down"));
        Assert.False(await Repo(throwing).RefreshAsync());
    }

    [Fact]
    public void MinAppVersionDisablesProvider()
    {
        File.WriteAllText(_bundledPath, Doc(1, min: "2.0.0"));
        Assert.False(Repo(new FakeHttpMessageHandler(), version: "1.9.9").ForProvider("p").Enabled);
        Assert.True(Repo(new FakeHttpMessageHandler(), version: "2.0.0").ForProvider("p").Enabled);
    }

    [Fact]
    public void PlaceholderPublicKeyIsNull() => Assert.Null(RemoteConfigKeys.PublicKey());

    [Fact]
    public void RealKeyDecodes() => Assert.Equal(32, RemoteConfigKeys.PublicKey(Convert.ToBase64String(new byte[32]))!.Length);
}
