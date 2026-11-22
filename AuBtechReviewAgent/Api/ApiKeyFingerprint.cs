using System;
using System.Security.Cryptography;
using System.Text;

namespace AuBtechReviewAgent;

/// <summary>
/// Turns a caller's key into a short HMAC, so the rate limiter can count calls per key without holding the key.
/// The secret is random and lives only in memory: a restart gives every key a new fingerprint, which is fine for
/// limits that count per minute and per hour, and no fingerprint can be matched to a key outside this process.
/// This is the same idea as RunQuotaService, which counts client addresses by their HMAC.
/// </summary>
public sealed class ApiKeyFingerprint
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>24 hex digits of the key's HMAC-SHA256, or "none" when there is no key.</summary>
    public string Of(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "none";
        return Convert.ToHexString(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(key)))[..24];
    }
}
