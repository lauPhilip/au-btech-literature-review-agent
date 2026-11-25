using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>
/// Who a grey source is credited to in its reference (issue #471), worked out in code from its search record and its
/// address, never by the model: the producer the search gave; else the owner of a code repository or of a GitHub
/// Pages site; else the organisation behind a known publisher's site. When none of these is known the producer is
/// empty and the reference starts with the title, as APA sets a work with no author. A bare web address is never a producer.
/// </summary>
public static class GreyProducer
{
    // Sites whose pages an organisation publishes, cited as a group author. A host matches itself and its subdomains.
    private static readonly Dictionary<string, string> Publishers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic.com"] = "Anthropic",
        ["claude.com"] = "Anthropic",
        ["claude.ai"] = "Anthropic",
        ["openai.com"] = "OpenAI",
        ["developers.googleblog.com"] = "Google",
        ["blog.google"] = "Google",
        ["research.google"] = "Google Research",
        ["ai.google.dev"] = "Google",
        ["cloud.google.com"] = "Google Cloud",
        ["deepmind.google"] = "Google DeepMind",
        ["microsoft.com"] = "Microsoft",
        ["aws.amazon.com"] = "Amazon Web Services",
        ["ai.meta.com"] = "Meta",
        ["mistral.ai"] = "Mistral AI",
        ["huggingface.co"] = "Hugging Face",
        ["nvidia.com"] = "NVIDIA",
        ["ibm.com"] = "IBM",
        ["github.blog"] = "GitHub",
        ["langchain.com"] = "LangChain",
        ["blog.langchain.dev"] = "LangChain",
        ["llamaindex.ai"] = "LlamaIndex",
        ["databricks.com"] = "Databricks",
        ["cohere.com"] = "Cohere",
        ["manus.im"] = "Manus",
        ["cognition.ai"] = "Cognition",
        ["vercel.com"] = "Vercel",
        ["cursor.com"] = "Cursor",
    };

    // Code hosts where the first part of the path is the account that owns the repository.
    private static readonly HashSet<string> CodeHosts = new(StringComparer.OrdinalIgnoreCase) { "github.com", "gitlab.com", "codeberg.org", "bitbucket.org" };

    // Paths on a code host that are not an account.
    private static readonly HashSet<string> NotAccounts = new(StringComparer.OrdinalIgnoreCase)
        { "orgs", "topics", "sponsors", "marketplace", "features", "explore", "search", "collections", "trending", "about", "blog", "pricing", "login" };

    private static readonly Regex HostLike = new(@"^(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The producer to cite, or "" when the reference should start with the title.</summary>
    public static string Of(string? producer, string? url)
    {
        string given = Regex.Replace(producer ?? "", @"\s+", " ").Trim();
        if (given.Length > 0 && !HostLike.IsMatch(given)) return given;
        if (!PrismaReviewEngine.IsWebAddress(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        string? account = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (CodeHosts.Contains(host) && account != null && !NotAccounts.Contains(account)) return account;
        if (host.EndsWith(".github.io", StringComparison.Ordinal) && host.Count(c => c == '.') == 2) return host[..host.IndexOf('.')];
        for (string h = host; h.Contains('.'); h = h[(h.IndexOf('.') + 1)..])
            if (Publishers.TryGetValue(h, out var name)) return name;
        return "";
    }
}
