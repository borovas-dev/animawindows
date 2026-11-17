namespace AnimaWindows.App.Player;

/// <summary>Splits stream headers into mpv's dedicated options (<c>referrer</c>, <c>user-agent</c>) and the generic header list.</summary>
public sealed record MpvHeaderSet(string? Referrer, string? UserAgent, IReadOnlyList<string> Fields)
{
    public static MpvHeaderSet From(IReadOnlyDictionary<string, string> headers)
    {
        string? referrer = null;
        string? userAgent = null;
        var fields = new List<string>();
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value)) continue;
            if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase) || name.Equals("Referrer", StringComparison.OrdinalIgnoreCase))
                referrer = value;
            else if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                userAgent = value;
            else
                fields.Add(name + ": " + value);
        }

        return new MpvHeaderSet(referrer, userAgent, fields);
    }
}
