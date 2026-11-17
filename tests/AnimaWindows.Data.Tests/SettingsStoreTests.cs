using System.Text.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data.Tests;

public class SettingsStoreTests : DataTestBase
{
    private string SettingsPath => Path.Combine(Dir, "settings.json");

    private JsonSettingsStore NewStore(string? name = null) => new(name is null ? SettingsPath : Path.Combine(Dir, name));

    [Fact]
    public async Task DefaultsThenUpdatePersistsAndRaisesChanged()
    {
        var store = NewStore();
        await store.LoadAsync(Ct);
        Assert.Equal(AppSettings.Fresh.ContentLanguage, store.Current.ContentLanguage);
        Assert.True(store.Current.MigratedV2);

        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;
        await store.UpdateAsync(s => s with { ThemeMode = ThemeMode.Dark, SkipOffsetSec = 3 }, Ct);
        Assert.Equal(ThemeMode.Dark, store.Current.ThemeMode);
        Assert.Equal(3, seen!.SkipOffsetSec);
        Assert.False(File.Exists(SettingsPath + ".tmp"));

        var reloaded = NewStore();
        await reloaded.LoadAsync(Ct);
        Assert.Equal(ThemeMode.Dark, reloaded.Current.ThemeMode);
        Assert.Equal(3, reloaded.Current.SkipOffsetSec);
    }

    [Fact]
    public async Task EqualUpdate_DoesNotRaiseChanged()
    {
        var store = NewStore();
        await store.LoadAsync(Ct);
        var raised = 0;
        store.Changed += (_, _) => raised++;
        await store.UpdateAsync(s => s with { DisabledProviders = [] }, Ct);
        await store.UpdateAsync(s => s, Ct);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task CorruptFile_ResetsToDefaultsAndReplacesFile()
    {
        await File.WriteAllTextAsync(SettingsPath, "{ not json !!", Ct);
        var store = NewStore();
        await store.LoadAsync(Ct);
        Assert.Equal("auto", store.Current.ContentLanguage);
        Assert.True(store.Current.MigratedV2);

        var onDisk = await File.ReadAllTextAsync(SettingsPath, Ct);
        Assert.NotNull(JsonSerializer.Deserialize(onDisk, AnimaWindows.Core.Json.CoreJsonContext.Default.AppSettings));
    }

    [Fact]
    public async Task EmptyOrMissingFile_YieldsDefaults()
    {
        var missing = NewStore("missing.json");
        await missing.LoadAsync(Ct);
        Assert.Equal(AppSettings.Fresh.Mode, missing.Current.Mode);

        await File.WriteAllTextAsync(SettingsPath, "  ", Ct);
        var empty = NewStore();
        await empty.LoadAsync(Ct);
        Assert.True(empty.Current.MigratedV2);
    }

    [Fact]
    public void MigrateV2_MapsLegacyProviderOnce()
    {
        var shinden = JsonSettingsStore.MigrateV2(new AppSettings { ProviderId = "shinden" });
        Assert.Equal("pl", shinden.ContentLanguage);
        Assert.True(shinden.MigratedV2);
        Assert.Equal("en", JsonSettingsStore.MigrateV2(new AppSettings { ProviderId = "hianime" }).ContentLanguage);
        Assert.Equal("auto", JsonSettingsStore.MigrateV2(shinden with { ContentLanguage = "auto" }).ContentLanguage);
        Assert.Equal("zh", JsonSettingsStore.MigrateV2(new AppSettings { ProviderId = "shinden", ContentLanguage = "zh" }).ContentLanguage);
    }

    [Fact]
    public async Task LegacyFileOnLoad_IsMigratedAndPersisted()
    {
        await File.WriteAllTextAsync(SettingsPath, """{"providerId":"shinden"}""", Ct);
        var store = NewStore();
        await store.LoadAsync(Ct);
        Assert.Equal("pl", store.Current.ContentLanguage);
        Assert.Contains("\"migratedV2\":true", await File.ReadAllTextAsync(SettingsPath, Ct));
    }

    [Fact]
    public async Task ExportImportRoundTrip_AndUnknownKeysIgnored()
    {
        var a = NewStore("a.json");
        await a.LoadAsync(Ct);
        await a.UpdateAsync(s => s with { ProviderId = "x", AutoPlayNext = false, Mode = TranslationMode.Dub }, Ct);
        var json = await a.ExportJsonAsync(Ct);
        Assert.Contains("\"mode\":\"DUB\"", json);

        var b = NewStore("b.json");
        await b.LoadAsync(Ct);
        await b.ImportJsonAsync(json, Ct);
        Assert.Equal(a.Current.Mode, b.Current.Mode);
        Assert.False(b.Current.AutoPlayNext);
        Assert.Equal("x", b.Current.ProviderId);

        await b.ImportJsonAsync("""{"providerId":"y","future":1}""", Ct);
        Assert.Equal("y", b.Current.ProviderId);
        Assert.Equal("auto", b.Current.ContentLanguage);
    }

    [Fact]
    public async Task Import_InvalidJsonThrowsJsonException_AndKeepsSettings()
    {
        var store = NewStore();
        await store.LoadAsync(Ct);
        await Assert.ThrowsAnyAsync<JsonException>(() => store.ImportJsonAsync("not json", Ct));
        await Assert.ThrowsAnyAsync<JsonException>(() => store.ImportJsonAsync("null", Ct));
        Assert.True(store.Current.MigratedV2);
    }
}
