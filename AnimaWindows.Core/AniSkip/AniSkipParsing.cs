using System.Text.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.AniSkip;

/// <summary>One entry of the AniSkip <c>results</c> array.</summary>
internal sealed record SkipResult(double StartTime, double EndTime, string SkipType);

internal static class AniSkipParsing
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) animawindows";

    /// <summary>Pure validation, mirrors ani-skip build_options (Kotlin <c>buildSkipTimes</c>).</summary>
    public static SkipTimes? BuildSkipTimes(IReadOnlyList<SkipResult> results)
    {
        SkipInterval? op = null;
        var opRaw = results.FirstOrDefault(r => r.SkipType == "op");
        if (opRaw is not null && opRaw.StartTime < opRaw.EndTime) op = new SkipInterval(opRaw.StartTime, opRaw.EndTime);

        SkipInterval? ed = null;
        var edRaw = results.FirstOrDefault(r => r.SkipType == "ed");
        if (edRaw is not null && edRaw.StartTime < edRaw.EndTime && (op is null || edRaw.StartTime >= op.EndSec))
            ed = new SkipInterval(edRaw.StartTime, edRaw.EndTime);

        return op is null && ed is null ? null : new SkipTimes(op, ed);
    }

    /// <summary>Parses the AniSkip response body. Returns null when <c>found</c> is false; throws <see cref="JsonException"/> on garbage.</summary>
    public static SkipTimes? ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Not an object");
        if (!root.TryGetProperty("found", out var found) || found.ValueKind != JsonValueKind.True) return null;
        var results = new List<SkipResult>();
        if (root.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("interval", out var iv) || iv.ValueKind != JsonValueKind.Object) throw new JsonException("Missing interval");
                if (!item.TryGetProperty("skip_type", out var st) || st.ValueKind != JsonValueKind.String) throw new JsonException("Missing skip_type");
                if (!iv.TryGetProperty("start_time", out var s) || !iv.TryGetProperty("end_time", out var e)
                    || s.ValueKind != JsonValueKind.Number || e.ValueKind != JsonValueKind.Number)
                    throw new JsonException("Missing times");
                results.Add(new SkipResult(s.GetDouble(), e.GetDouble(), st.GetString()!));
            }
        }

        return BuildSkipTimes(results);
    }
}
