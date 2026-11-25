# 10. Limits against overload and abuse

TraceableAI runs as one IIS process on shared hosting, with no proxy in front of it. Everything that keeps a crowd or a bot from breaking it therefore lives in the app. This page describes those limits: what one network address may do per minute and per hour, how live connections and uploads are capped, the hidden field that stops simple form bots, and what is logged when something is refused. The one fair line that every review waits in, and the free quota, are on the [operations page](06-web-app-and-operations.md#limits-quota-slots-cache). The options for a CAPTCHA, which is not added, close the page.

## One address, many kinds of request

Every limit counts per client address, normalised by `RunQuotaService.NormalizeClientAddress`: an IPv4-mapped address becomes plain IPv4, and an IPv6 address is cut to its /64, because one household or phone gets a whole /64 and could rotate within it. The address is the one the forwarded-headers set-up trusts, so a visitor cannot pick their own with `X-Forwarded-For`. The server itself is not limited: a request from a loopback address, or with no address at all as in the test server, is a health probe or a test, not a visitor.

The numbers are generous on purpose. A class on a campus network often reaches the site from one address, and thirty students opening the site at once must not be refused. A bot that hammers the site from one address is far above them.

## Plain HTTP requests

`Web/SiteRateLimits.cs` adds ASP.NET Core rate limiting for every address of the site, as one global limiter partitioned by kind of request and address. The API and the MCP server keep their own limiter (`ApiEndpoints.AddReviewApi`, 120 calls a minute per address and per key), which is chained after the site's and handles its own addresses.

| Kind | Addresses | Default limit per address | Setting |
|---|---|---|---|
| Pages | Every page and anything not listed below | 300 a minute | `Site:Limits:PagesPerMinute` |
| Static files | `/_framework`, `/_content`, and files such as `.css`, `.js`, fonts and images | 1,200 a minute | `Site:Limits:StaticFilesPerMinute` |
| Downloads | `/api/workspace/{id}/...` (archive, references, protocol, `main.tex`, report) and the Metrics page's files (`/metrics/runs.csv`, `runs.json`, `stages.csv`) | 20 a minute | `Site:Limits:DownloadsPerMinute` |
| Probes | `/health`, `/robots.txt`, `/sitemap.xml`, `/.well-known/` | 30 a minute | `Site:Limits:ProbesPerMinute` |
| New live connections | `/_blazor/negotiate` | 120 a minute | `Site:Limits:ConnectionsPerMinute` |
| Open live connections | `/_blazor` (a circuit's WebSocket) | 50 at once | `Site:Limits:OpenConnectionsPerClient` |

A refused request gets 429 Too Many Requests with `Retry-After` in seconds, and a short plain-text line. A refused live connection is refused the same way, and the browser's reconnect banner offers to try again. Every request body is capped at `Site:Limits:MaxRequestBodyKb` (1,024 KB): a body that says it is larger is refused with 413 before it is read, and the server's own limit is lowered for a body without a length (IIS in-process allows 30 MB by default). The API's start still allows only 64 KB, and a file upload on `/verify` travels over the live connection in small pieces, so it is not affected.

## Live connections

A Blazor Server page keeps a live connection (a circuit) with state on the server. `SiteRateLimits.LimitCircuits` and `LimitHub` write the circuit options out, so they are on file: a disconnected circuit is kept for 3 minutes for a reconnect, at most 50 of them (Blazor's default is 100), 10 render batches may wait for the browser, a call into the browser times out after a minute, a message from the browser is at most 32 KB, and one circuit runs one call at a time. With the cap of 50 open connections per address, one address can hold at most 50 circuits, and disconnected ones are dropped oldest first.

## Actions that cost money

Starting a review, proposing search strings, re-checking a citation, the editor's check of a changed sentence, checking an archive and adding a report to the gallery all run over the live connection, which the HTTP limiter never sees. `Web/ActionLimiter.cs` counts them per address and per hour, in memory. Before, a page allowed five proposals and a reload gave five more; the counts per address do not start again with a reload.

| Action | Where | Default per address per hour |
|---|---|---|
| `start-review` | Start review on the form, Start the review and Continue on the multivocal pages | 20 |
| `propose-strings` | Propose search strings, on both forms | 30 |
| `recheck` | Check again on Review output (on top of five per run) | 30 |
| `editor-check` | "Check and save" in the report's checked editor, for a sentence that cites a study (`SpecMatrix.Edits.cs`); the refusal shows in the edit box | 60 |
| `verify-upload` | Checking an archive on `/verify` | 20 |
| `gallery-submit` | "Yes, add it" when adding a report to the gallery (`GalleryOffer.razor`), which copies the run's report files | 5 |

`Site:ActionsPerHour` overrides any of them, for example `"Site": { "ActionsPerHour": { "propose-strings": 10 } }`; 0 means no limit. A refusal says when to try again, in the words of the quota ("Try again in 42 min."). Starting a review is also bound by the free quota and the one fair line; this limit only stops an address that keeps trying.

## Uploads

`/verify` checks an uploaded run archive. One check may unpack up to 400 MB, and the process is 32-bit, so `Web/ArchiveUpload.cs` lets only one check run at a time across the site; a second visitor is asked to try again in a minute. A file must be named `.zip`, be at most 200 MB and start with the bytes of a zip file. It is copied to a temporary file that is deleted when the check ends, not to memory as before.

## The hidden field

Both forms that start a review carry a field no person sees: `Components/Layout/Honeypot.razor`, labelled "Leave this field empty". It is hidden on screen (`sr-only`), from screen readers (`aria-hidden`) and from the keyboard (`tabindex="-1"`), and it needs no script, so the Content-Security-Policy is untouched. A bot that fills every field fills it too. The page then refuses the start with "The tool is busy; try again later." and logs it, before the start is counted, the line is joined or the quota is touched. It stops simple form bots only; a bot written for this site would leave it empty.

## What is logged

A refusal writes one warning line, at most once a minute per kind and address, so a flood of refusals cannot flood the log: "Refused probes from one address over its limit (fingerprint d6581aaf68)." The fingerprint is an HMAC of the address with a key that is made new on every start of the app, so lines can be matched within one run of the app but never turned back into an address. A refused run start logs only that it was refused and how full the line is. The privacy page says what is counted and that none of it is written to disk.

## Measured

`SiteRateLimitTests` sends 100 requests to `/health` from one address at once through the real app, with the probe limit at 30: 30 are answered and 70 get 429 with `Retry-After`, in about 0.4 s. Another address, the loopback address and a request without an address are answered right after, and 31 requests from 31 addresses in one IPv6 /64 share one count. The line's own load test (`RunQueueTests`) is on the operations page.

## A CAPTCHA, if one is ever needed

Nothing here asks a visitor to prove they are human. If bots get past these limits, three options are open, and each needs Lau's decision.

| Option | What it stops | Cost and changes |
|---|---|---|
| A self-hosted proof of work, such as ALTCHA | Bots that start many reviews or proposals cheaply: each start costs the browser a second or so of computing | Free and open source, no third party, no cookie. The script is served from the site; its CSP build needs no `blob:` worker, otherwise `worker-src 'self' blob:` is needed. A server-side check in C# and the widget on the two start forms. It does not stop a determined bot, only makes it slower |
| Cloudflare Turnstile | Most automated starts, with Cloudflare's signals about the browser | Free. Loads a script and a frame from `challenges.cloudflare.com`, so the CSP must allow that host for `script-src` and `frame-src`, and the privacy page must say that Cloudflare sees the visitor's address and browser |
| Cloudflare in front of the whole site | Floods and bots at the network edge, before they reach the server, with rules for rate limits and bot scores | Free plan possible. The domain's DNS moves to Cloudflare, which sees all traffic (privacy page). The forwarded-headers set-up must trust Cloudflare's address ranges (as known networks, which `Program.cs` does not read yet; `ReverseProxy:KnownProxies` takes single addresses), so the real visitor address reaches the limits here. WebSockets must stay on |
