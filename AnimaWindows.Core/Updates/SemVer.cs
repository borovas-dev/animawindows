using System.Globalization;

namespace AnimaWindows.Core.Updates;

/// <summary>Minimal semantic version: <c>v1.2.3-beta.1+build</c> = numeric core + optional pre-release. Lenient on missing parts. Port of <c>SemVer</c>.</summary>
public sealed record SemVer(IReadOnlyList<int> Parts, string? PreRelease = null) : IComparable<SemVer>
{
    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var n = Math.Max(Parts.Count, other.Parts.Count);
        for (var i = 0; i < n; i++)
        {
            var a = i < Parts.Count ? Parts[i] : 0;
            var b = i < other.Parts.Count ? other.Parts[i] : 0;
            var c = a.CompareTo(b);
            if (c != 0) return c;
        }

        if (PreRelease == other.PreRelease) return 0;
        if (PreRelease is null) return 1; // release > pre-release
        if (other.PreRelease is null) return -1;
        return ComparePre(PreRelease, other.PreRelease);
    }

    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xi = int.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xv);
            var yi = int.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yv);
            var c = xi && yi ? xv.CompareTo(yv) : xi ? -1 : yi ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return Math.Sign(c);
        }

        return x.Length.CompareTo(y.Length);
    }

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    /// <summary>Value equality of numeric parts and pre-release (records compare lists by reference, so override).</summary>
    public bool Equals(SemVer? other) => other is not null && PreRelease == other.PreRelease && Parts.SequenceEqual(other.Parts);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var p in Parts) h.Add(p);
        h.Add(PreRelease);
        return h.ToHashCode();
    }

    public static SemVer? Parse(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        string? pre = dash >= 0 ? s[(dash + 1)..] : null;
        if (pre is { Length: 0 }) pre = null;
        var nums = new List<int>();
        foreach (var part in core.Split('.'))
        {
            if (!int.TryParse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)) return null;
            nums.Add(v);
        }

        if (nums.Count == 0 || nums.Count > 4) return null;
        return new SemVer(nums, pre);
    }

    /// <summary>True when <paramref name="candidate"/> parses and is strictly newer than <paramref name="current"/> (both must parse).</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var c = Parse(candidate);
        var cur = Parse(current);
        return c is not null && cur is not null && c > cur;
    }
}
