using System.Text;
using System.Text.Json;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Json;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Updates;

namespace AnimaWindows.Core.RemoteConfig;

/// <summary>Constants of <c>RemoteConfigKeys</c>.</summary>
public static class RemoteConfigKeys
{
    /// <summary>Base64 of the raw 32-byte Ed25519 public key; placeholder until the owner generates a pair (see config/README).</summary>
    public const string PublicKeyBase64 = "PLACEHOLDER_REPLACE_WITH_BASE64_ED25519_PUBLIC_KEY";

    /// <summary>Same as Kotlin ALLOW_UNSIGNED_CONFIG: documents without a <c>.sig</c> are accepted; a present-but-wrong one never is.</summary>
    public const bool AllowUnsignedConfig = true;

    public const string ConfigUrl = "https://raw.githubusercontent.com/borovas-dev/animadroid/main/config/providers.json";
    public const string SignatureUrl = ConfigUrl + ".sig";

    /// <summary>Decoded key or null while still the placeholder / malformed.</summary>
    public static byte[]? PublicKey(string b64 = PublicKeyBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(string.Concat(b64.Where(c => !char.IsWhiteSpace(c))));
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Ed25519 detached-signature verification. .NET has no built-in Ed25519, so the app supplies an implementation (e.g. a
/// NSec/BouncyCastle wrapper) when signing is enforced. <see cref="NullEd25519Verifier"/> rejects everything.
/// </summary>
public interface IEd25519Verifier
{
    bool Verify(byte[] publicKey, byte[] message, byte[] signature);
}

/// <summary>Verifier that never validates anything (default). With <see cref="RemoteConfigOptions.AllowUnsigned"/> unsigned documents still load.</summary>
public sealed class NullEd25519Verifier : IEd25519Verifier
{
    public static NullEd25519Verifier Instance { get; } = new();
    public bool Verify(byte[] publicKey, byte[] message, byte[] signature) => false;
}

/// <param name="AppVersion">This app's version (compared with <c>minAppVersion</c>).</param>
/// <param name="PublicKey">Raw 32-byte Ed25519 key, or null (then any present signature is rejected, like Kotlin).</param>
/// <param name="AllowUnsigned">Accept a document whose <c>.sig</c> is missing.</param>
public sealed record RemoteConfigOptions(
    string AppVersion,
    byte[]? PublicKey = null,
    bool AllowUnsigned = RemoteConfigKeys.AllowUnsignedConfig,
    string ConfigUrl = RemoteConfigKeys.ConfigUrl,
    string? SignatureUrl = null);

/// <summary>
/// Remotely updatable provider constants (<c>config/providers.json</c>). Port of <c>RemoteConfigRepository</c>.
/// Load order at construction: verified disk cache, otherwise the bundled file (the one with the higher revision wins).
/// <see cref="RefreshAsync"/> fetches the raw GitHub file with <c>If-None-Match</c>, verifies the detached signature over the
/// exact bytes, and persists it atomically into <c>IAppPaths.CacheDirectory/remote-config</c>. Any failure keeps what is loaded.
/// </summary>
public sealed class RemoteConfigRepository : IRemoteConfigSource
{
    private readonly HttpClient _client;
    private readonly string _dir;
    private readonly RemoteConfigOptions _options;
    private readonly IEd25519Verifier _verifier;
    private readonly SemVer? _appVersion;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private RemoteConfigDocument _current;

    /// <param name="bundledConfigPath">Path of the bundled providers.json shipped with the app (missing/corrupt = empty document).</param>
    public RemoteConfigRepository(
        HttpClient client,
        IAppPaths paths,
        string bundledConfigPath,
        RemoteConfigOptions options,
        IEd25519Verifier? verifier = null)
        : this(client, Path.Combine(paths.CacheDirectory, "remote-config"), bundledConfigPath, options, verifier)
    {
    }

    /// <summary>Same with an explicit cache directory (tests).</summary>
    public RemoteConfigRepository(
        HttpClient client,
        string cacheDir,
        string bundledConfigPath,
        RemoteConfigOptions options,
        IEd25519Verifier? verifier = null)
    {
        _client = client;
        _dir = cacheDir;
        _options = options;
        _verifier = verifier ?? NullEd25519Verifier.Instance;
        _appVersion = SemVer.Parse(options.AppVersion);
        _current = LoadInitial(bundledConfigPath);
    }

    private string JsonFile => Path.Combine(_dir, "providers.json");
    private string SigFile => Path.Combine(_dir, "providers.json.sig");
    private string EtagFile => Path.Combine(_dir, "providers.json.etag");
    private string SignatureUrl => _options.SignatureUrl ?? _options.ConfigUrl + ".sig";

    public event EventHandler? Changed;

    public RemoteConfigDocument Current => Volatile.Read(ref _current);

    public ProviderRemoteConfig ForProvider(string id)
    {
        if (!Current.Providers.TryGetValue(id, out var p)) return ProviderRemoteConfig.Default;
        var min = p.MinAppVersion is null ? null : SemVer.Parse(p.MinAppVersion);
        return min is not null && _appVersion is not null && _appVersion < min ? p with { Enabled = false } : p;
    }

    private static RemoteConfigDocument? Decode(string text)
    {
        try
        {
            return JsonSerializer.Deserialize(text, CoreJsonContext.Default.RemoteConfigDocument);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private RemoteConfigDocument LoadInitial(string bundledPath)
    {
        RemoteConfigDocument bundled = new();
        try
        {
            if (File.Exists(bundledPath)) bundled = Decode(File.ReadAllText(bundledPath)) ?? new RemoteConfigDocument();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // keep the empty document
        }

        RemoteConfigDocument? cached = null;
        try
        {
            if (File.Exists(JsonFile))
            {
                var bytes = File.ReadAllBytes(JsonFile);
                var sig = File.Exists(SigFile) ? File.ReadAllText(SigFile) : null;
                if (Verified(bytes, sig)) cached = Decode(Encoding.UTF8.GetString(bytes));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            cached = null;
        }

        return cached is not null && cached.Revision >= bundled.Revision ? cached : bundled;
    }

    private bool Verified(byte[] body, string? sigB64)
    {
        if (sigB64 is null) return _options.AllowUnsigned;
        if (_options.PublicKey is not { } key) return false;
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(string.Concat(sigB64.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException)
        {
            return false;
        }

        return _verifier.Verify(key, body, sig);
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_dir);
            using var req = new HttpRequestMessage(HttpMethod.Get, _options.ConfigUrl);
            if (File.Exists(EtagFile) && File.Exists(JsonFile))
            {
                var etag = File.ReadAllText(EtagFile).Trim();
                if (etag.Length > 0) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var resp = await _client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if ((int)resp.StatusCode == 304 || !resp.IsSuccessStatusCode) return false;
            var newEtag = resp.Headers.ETag?.ToString();
            var body = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var sigText = await FetchSignatureAsync(cancellationToken).ConfigureAwait(false);
            if (!Verified(body, sigText)) return false;
            var parsed = Decode(Encoding.UTF8.GetString(body));
            if (parsed is null || parsed.Revision < Current.Revision) return false;
            Persist(body, sigText, newEtag);
            Volatile.Write(ref _current, parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<string?> FetchSignatureAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _client.GetAsync(SignatureUrl, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Persist(byte[] body, string? sig, string? etag)
    {
        AtomicWrite(JsonFile, body);
        if (sig is not null) AtomicWrite(SigFile, Encoding.UTF8.GetBytes(sig));
        else if (File.Exists(SigFile)) File.Delete(SigFile);
        if (etag is not null) AtomicWrite(EtagFile, Encoding.UTF8.GetBytes(etag));
        else if (File.Exists(EtagFile)) File.Delete(EtagFile);
    }

    private static void AtomicWrite(string target, byte[] bytes)
    {
        var tmp = target + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, target, overwrite: true);
    }
}
