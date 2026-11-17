using System.Text;
using System.Text.Json;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data;

/// <summary>
/// <see cref="ISettingsStore"/> backed by <c>settings.json</c> (CoreJsonContext), written atomically (temp file + move).
/// A missing, empty or corrupt file yields <see cref="AppSettings.Fresh"/>; corruption replaces the file.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public const string FileName = "settings.json";

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.Fresh;
    private string _currentJson;
    private bool _loaded;

    public JsonSettingsStore(IAppPaths paths)
        : this(Path.Combine(paths.DataDirectory, FileName))
    {
    }

    /// <summary>Store at an explicit file path (tests).</summary>
    public JsonSettingsStore(string filePath)
    {
        _path = filePath;
        _currentJson = Serialize(_current);
    }

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current => Volatile.Read(ref _current);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            var needsWrite = false;
            AppSettings settings;
            string? text = null;
            try
            {
                if (File.Exists(_path))
                {
                    text = await File.ReadAllTextAsync(_path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (IOException)
            {
                text = null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                settings = AppSettings.Fresh;
            }
            else
            {
                AppSettings? parsed = null;
                try
                {
                    parsed = JsonSerializer.Deserialize(text, CoreJsonContext.Default.AppSettings);
                }
                catch (JsonException)
                {
                    parsed = null;
                }

                if (parsed is null)
                {
                    settings = AppSettings.Fresh;
                    needsWrite = true;
                }
                else
                {
                    settings = MigrateV2(parsed);
                    needsWrite = !ReferenceEquals(settings, parsed);
                }
            }

            _currentJson = Serialize(settings);
            Volatile.Write(ref _current, settings);
            _loaded = true;
            if (needsWrite)
            {
                await WriteFileAsync(_currentJson, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(Func<AppSettings, AppSettings> transform, CancellationToken cancellationToken = default)
    {
        AppSettings? changed = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = transform(_current);
            var json = Serialize(next);
            if (json == _currentJson)
            {
                return;
            }

            await WriteFileAsync(json, cancellationToken).ConfigureAwait(false);
            _currentJson = json;
            Volatile.Write(ref _current, next);
            changed = next;
        }
        finally
        {
            _gate.Release();
        }

        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Serialize(Current));

    public async Task ImportJsonAsync(string json, CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppSettings)
            ?? throw new JsonException("Settings JSON must be an object.");
        var imported = MigrateV2(parsed);

        AppSettings? changed = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = Serialize(imported);
            if (text == _currentJson)
            {
                _loaded = true;
                return;
            }

            await WriteFileAsync(text, cancellationToken).ConfigureAwait(false);
            _currentJson = text;
            Volatile.Write(ref _current, imported);
            _loaded = true;
            changed = imported;
        }
        finally
        {
            _gate.Release();
        }

        if (changed is not null)
        {
            Changed?.Invoke(this, changed);
        }
    }

    /// <summary>
    /// One-time 0.2 migration: legacy <c>ProviderId</c> becomes a content language ("shinden" to "pl", "hianime" to "en")
    /// unless <c>ContentLanguage</c> was already set. Guarded by <c>MigratedV2</c>. Kotlin <c>migrateSettingsV2</c>.
    /// </summary>
    internal static AppSettings MigrateV2(AppSettings s)
    {
        if (s.MigratedV2)
        {
            return s;
        }

        var lang = s.ContentLanguage == "auto"
            ? s.ProviderId switch
            {
                "shinden" => "pl",
                "hianime" => "en",
                _ => "auto",
            }
            : s.ContentLanguage;
        return s with { ContentLanguage = lang, MigratedV2 = true };
    }

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, CoreJsonContext.Default.AppSettings);

    private async Task WriteFileAsync(string json, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = _path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);
    }
}
