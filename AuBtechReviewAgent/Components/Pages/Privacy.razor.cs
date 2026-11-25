using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Components;

namespace AuBtechReviewAgent.Components.Pages;

/// <summary>
/// The words of the privacy page. Every sentence describes what the code does: RunQuotaService, SiteRateLimits,
/// ActionLimiter and RunCoordinator (network address), RunStore and SessionCleanupWorker (runs), ReviewCache, RunMetricsStore,
/// GalleryStore (with ConsentUtc), wwwroot/js/review-output.js and theme.js (the browser), and for grey literature
/// MultivocalPages, PageFetcher and RobotsRules. The periods come from the running configuration. Change them together.
/// </summary>
public partial class Privacy
{
    [Inject] private RunsOptions RunsOptions { get; set; } = default!;
    [Inject] private CacheOptions CacheOptions { get; set; } = default!;
    [Inject] private QuotaOptions QuotaOptions { get; set; } = default!;
    [Inject] private ReviewModulesOptions ModuleOptions { get; set; } = default!;
    [Inject] private GalleryOptions GalleryOptions { get; set; } = default!;

    /// <summary>The data controller, as the page names it.</summary>
    public const string Controller = "Department of Business Development and Technology (BTECH), Aarhus University";

    /// <summary>The authority people can complain to (GDPR article 77).</summary>
    public const string DatatilsynetUrl = "https://www.datatilsynet.dk/";

    /// <summary>Where questions and requests go until the site has its own address (issue #543).</summary>
    public static string ContactUrl => $"{SiteSeo.RepositoryUrl}/issues";

    /// <summary>Whether the grey literature review is described: its card is on, or its module is in preview.</summary>
    private bool Grey => ModuleOptions.Shows("grey");

    /// <summary>Cached files are deleted after the longest of the two periods (ReviewCache.Prune).</summary>
    private int CacheDeletedAfterDays => (int)Math.Ceiling(Math.Max(CacheOptions.ScreeningDecisionDays, CacheOptions.SearchResponseHours / 24.0 + 1));

    /// <summary>The sections listed under this page in the trust pages' side list.</summary>
    private IReadOnlyList<(string Id, string Label)> DocSections =>
        new[] { ("controller", "Who is responsible") }
            .Concat(Sections.Where(x => Grey || x.Id != "web-pages").Select(x => (x.Id, x.Heading)))
            .Concat(new[] { ("retention", "How long things are kept"), ("third-parties", "Who else receives what"), ("rights", "Your rights"), ("choices", "Your choices") })
            .ToList();

    private (string Title, string Text)[] Summary => new[]
    {
        ("No account, no tracking", "No sign-up, no analytics, no advertising cookies."),
        ("Your network address", "Only used to count free runs and to slow down automated traffic. Written to disk only as a keyed fingerprint, and forgotten after two days."),
        ("Your review", $"Kept on this server for {RunsOptions.RetentionDays} days, then deleted. You can delete it sooner."),
        ("Your API keys", "Kept encrypted in this browser tab only, and gone when you close it."),
    };

    /// <summary>What is kept, where, and for how long; the periods come from the configuration.</summary>
    private (string What, string Where, string HowLong)[] Retention => new[]
    {
        ("Your review and everything it produced", "This server", $"{RunsOptions.RetentionDays} days after the run last changed, or until you delete it"),
        ("Fingerprints of network addresses, for the free runs", "This server", "Two days"),
        ("Counts of requests per network address", "The server's memory only", "An hour at most, or until the server restarts"),
        ("Cached database answers and screening decisions", "This server", $"Deleted after {CacheDeletedAfterDays} days"),
        ("A report you added to the gallery", "This server", "Until you withdraw it, a newer run of the same review replaces it, or the gallery is full"),
        ("The quality figures of a finished run (Metrics)", "This server", "No time limit; they hold no personal data"),
        ("Edit keys, the unfinished draft and your theme", "Your browser", "Edit keys and the draft 30 days; the theme until you clear it"),
        ("Your API keys", "Your browser tab", "Until you close the tab or press Clear session"),
    };

    private (string Id, string Heading, string[] Paragraphs)[] Sections => new[]
    {
        ("cookies", "Cookies and tracking", new[]
        {
            "TraceableAI uses no analytics, advertising or tracking cookies, and loads no scripts, fonts or images from other sites. " +
            "The only cookie is a technical one the web framework sets to protect forms against requests from other sites; it identifies no one.",
        }),
        ("address", "Your network address", PrivacyText.Address(QuotaOptions.FreeRunsPerDay)),
        ("review", "Your review", PrivacyText.Review(RunsOptions.RetentionDays, Grey)),
        ("keys", "Your API keys", new[]
        {
            "Keys you enter under API keys are kept encrypted in your browser tab's session storage. The server uses them only to make the " +
            "calls of your run; it does not write them to disk or to its logs. They are gone when you close the tab, or when you press Clear session.",
        }),
        ("browser", "What your browser keeps", PrivacyText.Browser),
        ("web-pages", "Web pages a grey literature review reads", PrivacyText.WebPages(RobotsRules.ProductToken, RunsOptions.RetentionDays)),
        ("reuse", "What is kept longer", PrivacyText.Reuse(CacheOptions.SearchResponseHours, CacheOptions.ScreeningDecisionDays, Grey)),
        ("gallery", "The gallery of example reports", PrivacyText.Gallery(RunsOptions.RetentionDays, GalleryOptions.MaxEntries)),
    };

    private static readonly (string Service, string Receives)[] ThirdParties =
    {
        ("Mistral AI (the language model), France", "Your question, objective and criteria, and titles, abstracts and text of the papers, to screen, extract, write and check. On the site's own key Mistral AI processes this on BTECH's behalf. Runs with your own key go through your own Mistral account."),
        ("Simply.com (the hosting provider)", "Everything stored on this server, which it hosts on BTECH's behalf, and the standard access logs of the web server."),
        ("arXiv, OpenAlex, Semantic Scholar, Crossref", "Your search query and its extra phrasings, and the identifiers of included papers for citation chaining."),
        ("Scopus, ScienceDirect, IEEE Xplore", "Your search query, only when a key for them is set."),
        ("Unpaywall, Zenodo and publishers' open-access servers", "The DOI or link of an included paper, to download a legal open-access copy."),
    };

    /// <summary>The services a grey literature review adds; listed while it is described (<see cref="Grey"/>).</summary>
    private static readonly (string Service, string Receives)[] GreyThirdParties =
    {
        ("Stack Exchange, GitHub, Hacker News (Algolia), OpenAlex and Zenodo", "In a grey literature review: the review's grey search strings, to find practitioner posts, repositories, discussions, reports and theses."),
        ("The sites of the sources found", "In a grey literature review: a request for robots.txt and for the page of a source the searches found, from the server, naming TraceableAI."),
    };
}
