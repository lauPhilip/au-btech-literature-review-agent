using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "OpenSources" section of appsettings.json.</summary>
public class OpenSourcesOptions
{
    /// <summary>
    /// Contact e-mail sent to OpenAlex, Crossref and Unpaywall ("polite pool"). OpenAlex and Crossref give
    /// faster, more reliable service to identified clients; Unpaywall requires it.
    /// </summary>
    public string ContactEmail { get; set; } = "";

    /// <summary>Optional Semantic Scholar API key (the API works without one, with a lower shared rate limit).</summary>
    public string SemanticScholarApiKey { get; set; } = "";

    /// <summary>How many referenced and citing papers to fetch per included paper when citation chaining is on.</summary>
    public int ChainingPerPaper { get; set; } = 5;
}

/// <summary>Shared HTTP plumbing for the open scholarly APIs: one HttpClient, polite retries on 429/5xx.</summary>
public static class OpenSourceHttp
{
    public const string UserAgent = "TraceableAI/1.0 (+https://github.com/lauPhilip/au-btech-literature-review-agent)";

    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>GET with up to three attempts on rate limits and server errors (honouring Retry-After).</summary>
    public static async Task<string> GetStringAsync(string url, IDictionary<string, string>? headers = null, Func<int, TimeSpan>? backoff = null)
    {
        backoff ??= attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt));
        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null) foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);

            using var response = await Client.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();

            bool transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!transient || attempt >= 3)
                throw new HttpRequestException($"{new Uri(url).Host} answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

            TimeSpan wait = response.Headers.RetryAfter?.Delta ?? backoff(attempt);
            await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait);
        }
    }

    public static string WithMailto(string url, string? email) =>
        string.IsNullOrWhiteSpace(email) ? url : $"{url}{(url.Contains('?') ? "&" : "?")}mailto={Uri.EscapeDataString(email.Trim())}";
}
