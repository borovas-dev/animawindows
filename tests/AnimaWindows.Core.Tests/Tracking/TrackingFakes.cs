using System.Net;
using System.Text;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tracking;

namespace AnimaWindows.Core.Tests.Tracking;

internal sealed record RecordedRequest(string Method, Uri Uri, string? Authorization, string Body);

internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<object> _responses = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK) => _responses.Enqueue((status, body));

    public void EnqueueFailure(Exception error) => _responses.Enqueue(error);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        }

        if (!_responses.TryDequeue(out var next))
        {
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        }

        if (next is Exception error)
        {
            throw error;
        }

        var (status, text) = ((HttpStatusCode, string))next;
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new();

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string value) => _values[key] = value;

    public void Remove(string key) => _values.Remove(key);
}

internal sealed class FakeClock : IClock
{
    public FakeClock(long unixMilliseconds) => Millis = unixMilliseconds;

    public long Millis { get; set; }

    public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(Millis);
}

internal sealed class FakeNetwork : INetworkCostProvider
{
    public NetworkCost Current { get; private set; } = NetworkCost.Unmetered;

    public event EventHandler<NetworkCost>? Changed;

    public void Set(NetworkCost cost)
    {
        Current = cost;
        Changed?.Invoke(this, cost);
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "anima-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class SyncEnv : IDisposable
{
    public const string AniListEndpoint = "https://anilist.test/graphql";

    public SyncEnv(SyncConfig? config = null)
    {
        Clock = new FakeClock(1_000_000);
        Dir = new TempDir();
        Queue = new SyncQueueStore(Dir.Path);
        var http = new HttpClient(Http);
        Repo = new SyncRepository(
            config ?? new SyncConfig("al", "mal"),
            new AniListClient(http, AniListEndpoint),
            new MalClient(http, "https://mal.test/v2", "https://mal.test/oauth"),
            Secrets,
            Queue,
            Clock);
        Repo.RetryRequested += (_, _) => Retries++;
    }

    public FakeHttpHandler Http { get; } = new();

    public InMemorySecretStore Secrets { get; } = new();

    public FakeClock Clock { get; }

    public TempDir Dir { get; }

    public SyncQueueStore Queue { get; }

    public SyncRepository Repo { get; }

    public int Retries { get; private set; }

    public SecretTokenStore Tokens => new(Secrets);

    public void Dispose() => Dir.Dispose();
}
