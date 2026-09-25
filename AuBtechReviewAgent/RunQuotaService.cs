using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Quota" section of appsettings.json / user secrets.</summary>
public class QuotaOptions
{
    /// <summary>Runs per client address per UTC day on the server's own Mistral key.</summary>
    public int FreeRunsPerDay { get; set; } = 3;

    /// <summary>
    /// Ceiling on server-key runs per UTC day across everyone. A per-address limit is easy to get around
    /// (VPN, phone hotspot); this is what actually caps the Mistral bill. 0 = no global ceiling.
    /// </summary>
    public int GlobalFreeRunsPerDay { get; set; } = 50;

    /// <summary>Runs per client address per hour when the user brings their own Mistral key. 0 = no limit.</summary>
    public int OwnKeyRunsPerHour { get; set; } = 10;

    /// <summary>Highest "max results per source" allowed on the free tier (the main cost driver).</summary>
    public int FreeTierMaxResults { get; set; } = 5;

    /// <summary>Developer access token. Keep it in user secrets / server environment, never in git.</summary>
    public string? AdminToken { get; set; }

    /// <summary>When true, every run is unlimited while ASPNETCORE_ENVIRONMENT is Development.</summary>
    public bool UnlimitedInDevelopment { get; set; } = true;

    /// <summary>Where the run counters are stored (relative paths are resolved against the content root).</summary>
    public string StoragePath { get; set; } = Path.Combine("App_Data", "run-quota.json");
}

public enum QuotaTier { Free, OwnKey, Developer }

public record QuotaStatus(QuotaTier Tier, bool Allowed, int Used, int Limit, string Message)
{
    public int Remaining => Math.Max(0, Limit - Used);
}

/// <summary>A reserved run. Hand it back with <see cref="RunQuotaService.Refund"/> if the run never started.</summary>
public record QuotaLease(string Bucket, DateTime ReservedAtUtc, bool CountsTowardsGlobal);

/// <summary>
/// Daily run quota per client address, replacing the ASP.NET rate-limiter policy that was defined but
/// never attached (and could not have worked anyway: in Blazor Server a button click travels over the
/// SignalR circuit, not as an HTTP request the middleware sees).
///
/// Counters are written to a small JSON file so a restart or redeploy does not reset everyone's quota.
/// Client addresses are never stored: each is replaced by an HMAC with a random per-install secret, which
/// is enough to count runs but cannot be turned back into an IP address.
/// </summary>
public class RunQuotaService
{
    private const string GlobalFreeBucket = "global-free";

    private readonly QuotaOptions _options;
    private readonly bool _isDevelopment;
    private readonly Func<DateTime> _utcNow;
    private readonly string _storagePath;
    private readonly object _gate = new();
    private QuotaFile _data;

    public RunQuotaService(QuotaOptions options, bool isDevelopment, string contentRoot, Func<DateTime>? utcNow = null)
    {
        _options = options;
        _isDevelopment = isDevelopment;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _storagePath = Path.IsPathRooted(options.StoragePath) ? options.StoragePath : Path.Combine(contentRoot, options.StoragePath);
        _data = Load();
    }

    public QuotaOptions Options => _options;

    /// <summary>Developer token (or Development environment) beats own key, which beats the free tier.</summary>
    public QuotaTier ResolveTier(bool hasOwnMistralKey, string? adminToken)
    {
        if (_isDevelopment && _options.UnlimitedInDevelopment) return QuotaTier.Developer;
        if (IsValidAdminToken(adminToken)) return QuotaTier.Developer;
        return hasOwnMistralKey ? QuotaTier.OwnKey : QuotaTier.Free;
    }

    public bool IsValidAdminToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminToken) || string.IsNullOrWhiteSpace(token)) return false;
        // Compare fixed-length hashes in constant time so the check does not leak the token by timing.
        byte[] expected = SHA256.HashData(Encoding.UTF8.GetBytes(_options.AdminToken.Trim()));
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Largest "max results per source" this tier may use.</summary>
    public int MaxResultsFor(QuotaTier tier, int requested)
    {
        int clamped = Math.Clamp(requested, 1, 50);
        return tier == QuotaTier.Free ? Math.Min(clamped, Math.Max(1, _options.FreeTierMaxResults)) : clamped;
    }

    public QuotaStatus GetStatus(string clientAddress, QuotaTier tier)
    {
        lock (_gate)
        {
            return Evaluate(clientAddress, tier);
        }
    }

    /// <summary>Checks the quota and, if a run is allowed, counts it straight away (so a double click cannot start two runs on one slot).</summary>
    public bool TryReserve(string clientAddress, QuotaTier tier, out QuotaStatus status, out QuotaLease? lease)
    {
        lock (_gate)
        {
            status = Evaluate(clientAddress, tier);
            lease = null;
            if (!status.Allowed) return false;
            if (tier == QuotaTier.Developer) return true; // not counted

            DateTime now = _utcNow();
            string bucket = BucketFor(clientAddress, tier);
            Add(bucket, now);
            bool global = tier == QuotaTier.Free;
            if (global) Add(GlobalFreeBucket, now);
            Save();

            lease = new QuotaLease(bucket, now, global);
            status = Evaluate(clientAddress, tier);
            return true;
        }
    }

    /// <summary>Gives a reserved run back, e.g. when the run failed before doing any work.</summary>
    public void Refund(QuotaLease lease)
    {
        lock (_gate)
        {
            Remove(lease.Bucket, lease.ReservedAtUtc);
            if (lease.CountsTowardsGlobal) Remove(GlobalFreeBucket, lease.ReservedAtUtc);
            Save();
        }
    }

    /// <summary>
    /// Normalises the address used as the quota key. IPv4-mapped IPv6 becomes plain IPv4, and IPv6 is cut
    /// to its /64 prefix because one household or phone typically gets a whole /64 and can rotate within it.
    /// </summary>
    public static string NormalizeClientAddress(IPAddress? address)
    {
        if (address == null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            for (int i = 8; i < 16; i++) bytes[i] = 0;
            return new IPAddress(bytes) + "/64";
        }
        return address.ToString();
    }

    // ── internals ──────────────────────────────────────────────────────────────────────────────

    private QuotaStatus Evaluate(string clientAddress, QuotaTier tier)
    {
        DateTime now = _utcNow();
        switch (tier)
        {
            case QuotaTier.Developer:
                return new QuotaStatus(tier, true, 0, int.MaxValue, "Developer access: no run limit.");

            case QuotaTier.OwnKey:
            {
                int limit = _options.OwnKeyRunsPerHour;
                if (limit <= 0) return new QuotaStatus(tier, true, 0, int.MaxValue, "Own Mistral key: no run limit.");
                int used = Count(BucketFor(clientAddress, tier), t => t > now.AddHours(-1));
                return used < limit
                    ? new QuotaStatus(tier, true, used, limit, $"Own Mistral key: {limit - used} of {limit} runs left this hour.")
                    : new QuotaStatus(tier, false, used, limit, $"Own Mistral key: the limit of {limit} runs per hour is reached. Try again later.");
            }

            default:
            {
                int limit = Math.Max(0, _options.FreeRunsPerDay);
                int used = Count(BucketFor(clientAddress, tier), t => t.Date == now.Date);
                int globalUsed = Count(GlobalFreeBucket, t => t.Date == now.Date);

                if (used >= limit)
                    return new QuotaStatus(tier, false, used, limit,
                        $"Free tier: you have used all {limit} runs for today. The count resets at midnight UTC, or add your own Mistral key under API Credentials.");
                if (_options.GlobalFreeRunsPerDay > 0 && globalUsed >= _options.GlobalFreeRunsPerDay)
                    return new QuotaStatus(tier, false, used, limit,
                        "Free tier: today's shared capacity is used up. Try again after midnight UTC, or add your own Mistral key under API Credentials.");
                return new QuotaStatus(tier, true, used, limit, $"Free tier: {limit - used} of {limit} runs left today.");
            }
        }
    }

    private string BucketFor(string clientAddress, QuotaTier tier)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(_data.Secret));
        string digest = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(clientAddress ?? "unknown")))[..24];
        return $"{(tier == QuotaTier.OwnKey ? "own" : "free")}:{digest}";
    }

    private int Count(string bucket, Func<DateTime, bool> predicate) =>
        _data.Events.TryGetValue(bucket, out var list) ? list.Count(predicate) : 0;

    private void Add(string bucket, DateTime at)
    {
        if (!_data.Events.TryGetValue(bucket, out var list)) _data.Events[bucket] = list = new List<DateTime>();
        list.Add(at);
    }

    private void Remove(string bucket, DateTime at)
    {
        if (_data.Events.TryGetValue(bucket, out var list)) list.Remove(at);
    }

    private QuotaFile Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var loaded = JsonSerializer.Deserialize<QuotaFile>(File.ReadAllText(_storagePath));
                if (loaded != null && !string.IsNullOrEmpty(loaded.Secret))
                {
                    loaded.Events ??= new();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Quota] Could not read {_storagePath}, starting with empty counters: {ex.Message}");
        }
        return new QuotaFile { Secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
    }

    private void Save()
    {
        // Only the last two days are ever needed; drop the rest so the file stays small.
        DateTime cutoff = _utcNow().AddDays(-2);
        foreach (var key in _data.Events.Keys.ToList())
        {
            _data.Events[key].RemoveAll(t => t < cutoff);
            if (_data.Events[key].Count == 0) _data.Events.Remove(key);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            string tmp = _storagePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, _storagePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Counting still works in memory; it just would not survive a restart.
            Console.WriteLine($"[Quota] Could not write {_storagePath}: {ex.Message}");
        }
    }

    private class QuotaFile
    {
        public string Secret { get; set; } = "";
        public Dictionary<string, List<DateTime>> Events { get; set; } = new();
    }
}
