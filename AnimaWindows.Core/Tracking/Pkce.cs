using System.Security.Cryptography;

namespace AnimaWindows.Core.Tracking;

/// <summary>
/// MAL only supports <c>code_challenge_method=plain</c>, so the challenge equals the verifier (RFC 7636: 43..128 unreserved
/// chars). Mirrors core/sync/Pkce.kt.
/// </summary>
public static class Pkce
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    public static string Verifier(int length = 96)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 43);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 128);
        return string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
    }

    public static string Challenge(string verifier) => verifier;

    public static string State() => Verifier(43);
}
