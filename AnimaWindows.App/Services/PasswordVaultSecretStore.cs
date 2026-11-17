using System.Text;
using AnimaWindows.Core.Abstractions;
using Windows.Security.Credentials;

namespace AnimaWindows.App.Services;

/// <summary>
/// <see cref="ISecretStore"/> on the Windows Credential Locker (<see cref="PasswordVault"/>). A single credential has a small size
/// limit and OAuth token JSON can exceed it, so values are stored in chunks (<c>key#0</c>, <c>key#1</c>, ...) plus a count (<c>key#n</c>).
/// </summary>
public sealed class PasswordVaultSecretStore : ISecretStore
{
    private const string Resource = "animawindows";
    private const int ChunkSize = 400;
    private readonly object _gate = new();
    private readonly PasswordVault _vault = new();

    public string? Get(string key)
    {
        lock (_gate)
        {
            try
            {
                if (!int.TryParse(ReadRaw(CountKey(key)), out var count) || count <= 0)
                {
                    return null;
                }

                var sb = new StringBuilder();
                for (var i = 0; i < count; i++)
                {
                    var chunk = ReadRaw(ChunkKey(key, i));
                    if (chunk is null)
                    {
                        return null;
                    }

                    sb.Append(chunk);
                }

                return sb.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            RemoveCore(key);
            var count = 0;
            for (var offset = 0; offset < value.Length; offset += ChunkSize)
            {
                var part = value.Substring(offset, Math.Min(ChunkSize, value.Length - offset));
                _vault.Add(new PasswordCredential(Resource, ChunkKey(key, count), part));
                count++;
            }

            _vault.Add(new PasswordCredential(Resource, CountKey(key), count.ToString()));
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            RemoveCore(key);
        }
    }

    private void RemoveCore(string key)
    {
        try
        {
            if (int.TryParse(ReadRaw(CountKey(key)), out var count))
            {
                for (var i = 0; i < count; i++)
                {
                    RemoveRaw(ChunkKey(key, i));
                }
            }

            RemoveRaw(CountKey(key));
        }
        catch (Exception)
        {
            // Nothing stored for this key.
        }
    }

    private string? ReadRaw(string name)
    {
        try
        {
            var credential = _vault.Retrieve(Resource, name);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch (Exception)
        {
            return null; // "Element not found"
        }
    }

    private void RemoveRaw(string name)
    {
        try
        {
            _vault.Remove(_vault.Retrieve(Resource, name));
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    private static string CountKey(string key) => key + "#n";

    private static string ChunkKey(string key, int index) => key + "#" + index;
}
