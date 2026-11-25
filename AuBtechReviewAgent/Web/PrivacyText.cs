namespace AuBtechReviewAgent;

/// <summary>
/// The longer paragraphs of the privacy page (Components/Pages/Privacy.razor), one method a section, so the page stays
/// short. Each paragraph describes what the code does; the class named in a comment is the one it describes.
/// </summary>
public static class PrivacyText
{
    /// <summary>RunQuotaService (fingerprints on disk, two days), SiteRateLimits, ActionLimiter and RunCoordinator (memory only).</summary>
    public static string[] Address(int freeRunsPerDay) => new[]
    {
        $"Free runs are limited to {freeRunsPerDay} per day per network address. To count them, your address is replaced " +
        "by a keyed fingerprint (HMAC-SHA-256 with a secret that exists only on this server) before anything is stored. Without that secret " +
        "the stored value cannot be turned back into an address, and the server never tries to. Counts older than two days are deleted.",
        "To keep the tool fair and to slow down automated traffic, the server also counts in its memory how many pages, downloads and " +
        "costly actions (starting a review, proposing search strings, checking a citation again, checking an archive) come from one " +
        "address, and how many reviews it has running or waiting. These counts are never written to disk. They are gone after an hour " +
        "at most, or when the server restarts. When a request is refused because too many came from one address, the server's log notes " +
        "a short fingerprint that changes with every restart, never the address itself.",
        "Like any web server, the hosting provider may keep standard access logs (address, time, page) for operating and securing the server.",
    };

    /// <summary>RunStore and SessionCleanupWorker (Runs:RetentionDays).</summary>
    public static string[] Review(int retentionDays, bool grey) => new[]
    {
        "What you type in the review form (search query, objective, criteria, artifact instruction) is saved with the run, together with " +
        "everything the run produces: the protocol, the search results, every screening decision, the downloaded open-access papers, the " +
        "report and a record of every model call. This is what makes the review checkable." + (grey ? " A grey literature review saves its " +
        "plan the same way: the need for the review, its research questions, search strings, criteria and quality threshold." : ""),
        $"A run is kept for {retentionDays} days after it last changed and then deleted automatically, files and all, within the hour " +
        "after that. Anyone who has " +
        "the link to a run can read it, but only the browser that started it can change or delete it; the links contain a random identifier " +
        "that cannot be guessed, and they are not listed anywhere or shown to search engines.",
    };

    /// <summary>wwwroot/js/review-output.js (draft, edit keys) and theme.js (the theme). Edit keys are pruned when a new one is saved.</summary>
    public static readonly string[] Browser =
    {
        "While you fill in the review form, an unfinished draft (the question, criteria and options, never your keys) is kept in your browser's " +
        "local storage, so closing the tab does not lose it. It stays on your device until you start the review or press Start empty, and is " +
        "ignored after 30 days. If you ask to be notified when a review is ready, your browser shows the notification; nothing is sent anywhere else.",
        "When you start a review, your browser also keeps a random edit key for that run. Only the browser that has it can delete the run, " +
        "change its screening, check a citation again or write notes; anyone else with the link can only read it. The server stores only a " +
        "fingerprint of the key. Keys older than 30 days are removed from your browser the next time it starts a review.",
        "If you choose the light or the dark theme, your browser remembers the choice in its local storage. Nothing about it is sent to the server.",
        "These are needed for what you asked the site to do, or remember a choice you made, so the site does not ask for consent to them. " +
        "You can remove them at any time by clearing this site's data in your browser.",
    };

    /// <summary>MultivocalPages, PageFetcher and RobotsRules.</summary>
    public static string[] WebPages(string productToken, int retentionDays) => new[]
    {
        "A grey literature review cites web pages, such as blog posts, documentation, forum answers and reports, so it keeps a " +
        "snapshot of each page it reads. The server fetches only pages that the review's own searches found. Before it fetches anything from a " +
        $"site it reads the site's robots.txt for its name, {productToken}, and fetches nothing the site disallows " +
        "(nothing at all when the site has a robots.txt that cannot be read, as RFC 9309 asks). It sends at most one request a second to a site, or slower when the site asks for it, " +
        "never signs in, skips pages behind a sign-in and stops at 10 MB. Its requests name TraceableAI and link to this project.",
        $"The main text of each page is kept with the run, on this server, for the run's lifetime of {retentionDays} days, so that the " +
        "citation check can be repeated against the same text, and it is deleted with the run. It is not part of the run's archive and is not " +
        "shared: the archive holds the page's address, the date it was read, a fingerprint (SHA-256) of its text, the short passages the review " +
        "quotes and a link to the Wayback Machine's nearest capture. TraceableAI only builds that link; it does not ask the Internet Archive to " +
        "make a capture. A page may name its authors or the people who answered on a forum; they are kept only as they appear on the page and in " +
        "the reference to it.",
        "Stack Overflow and the other Stack Exchange sites refuse automated page requests, so for a question the review's search found there, " +
        "the server asks the public Stack Exchange API for the question and its answers instead of fetching the page. The text and the API's " +
        "answers are kept with the run in the same way, for the same time, and are not part of the archive. Because these posts are shared " +
        "under CC BY-SA, the archive's page record also names each post's author as the site shows it, with links to the post and the author's profile.",
    };

    /// <summary>ReviewCache and RunMetricsStore, with MetricsDownloads.</summary>
    public static string[] Reuse(int searchHours, int screeningDays, bool grey) => new[]
    {
        $"To save time and cost, a database's answer to a search is reused for {searchHours} hours, and a screening " +
        $"decision for the same paper, criteria and model is reused for {screeningDays} days. They are filed under a " +
        "fingerprint of the request and contain the papers' metadata and the decision, not who asked." + (grey ? " A grey literature review " +
        $"reuses its model decisions the same way, also for {screeningDays} days: the screening, the quality points, the " +
        "extracted values and the checks of values and citations, each with the short quotes it rests on but not the page's text." : ""),
        "For the public Metrics page, every finished run leaves a line of quality figures: how many citations were supported, coverage, " +
        "time and cost. It contains no research question, criteria or paper text, and no network address. Anyone can download these lines " +
        "from the Metrics page as CSV or JSON, with only the first eight characters of each run's ID, so a file cannot be used to open a run.",
    };

    /// <summary>GalleryStore (consent, ConsentUtc, withdrawal, cap, HiddenIds) and GallerySnapshot (what is copied).</summary>
    public static string[] Gallery(int retentionDays, int maxEntries) => new[]
    {
        "Nothing goes into the gallery unless the browser that started a run adds it and says yes to the text that explains what is " +
        "published. Readers with only the link never see the button. The gallery records the time of that yes with the entry. It then keeps a copy of the files the report is written from, " +
        "the same files as at the top of the run's archive: the report, the run's ledger with the topic, research questions and criteria, the " +
        "extracted data, themes and citation checks with their short quotes from the sources, the edit history and the protocol. It does not " +
        "copy your notes, the edit key's record, the record of model calls, the downloaded papers or the raw database answers, and it keeps " +
        "no network address.",
        "A report in the gallery is public: anyone can open it on this site, and search engines may find it. It shows the full ID of its run, " +
        $"so while the run itself is kept ({retentionDays} days) anyone could open that too. The copy stays after the run is " +
        "deleted, until the owner withdraws it. A newer run of the same review is kept as a new version, and the earlier versions stay, " +
        $"each at its own address, until they are withdrawn. The gallery keeps at most {maxEntries} versions in all; past " +
        "that it drops the oldest version of the review with the most versions, and a review leaves only when none has two.",
        "The owner can withdraw a report at any time, from its report page or its gallery page, with the browser that started the run; " +
        "every version of it is deleted at once. Browsers keep a run's edit key for 30 days. After that, or from another browser, ask for removal in the " +
        "issues on GitHub and the site's administrator will take the report down.",
    };
}
