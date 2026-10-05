using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>Settings bound from the "Synthesis" section of appsettings.json.</summary>
public class SynthesisOptions
{
    /// <summary>Code the studies and write one subsection per theme. When false (or when coding fails), the
    /// synthesis is written in one pass as before.</summary>
    public bool ThematicSynthesis { get; set; } = true;

    /// <summary>A second, independent assignment of studies to themes, compared with Cohen's kappa.</summary>
    public bool DualCoding { get; set; } = true;

    /// <summary>Rewrite cited sentences the support check judged not supported or partly supported, then check them again.</summary>
    public bool RepairCitations { get; set; } = true;

    /// <summary>Give partly supported and not supported citations a second, independent check with more excerpts.</summary>
    public bool SecondCitationCheck { get; set; } = true;

    /// <summary>Upper limit on the number of themes in the codebook.</summary>
    public int MaxThemes { get; set; } = 8;

    /// <summary>Studies per coding call.</summary>
    public int CodingBatchSize { get; set; } = 6;
}

/// <summary>One code: a short label for what a study found, anchored to verified evidence.</summary>
public class ThematicCode
{
    public string Id { get; set; } = "";
    public int Reference { get; set; }
    public string Label { get; set; } = "";

    /// <summary>Ids of the verified extraction findings the code comes from (e.g. "F3.1").</summary>
    public List<string> FindingIds { get; set; } = new();

    /// <summary>For a study without verified findings: a verbatim quote from its text.</summary>
    public string Quote { get; set; } = "";

    /// <summary>"verified finding", "verified quote" or "unanchored" (the evidence could not be confirmed in code).</summary>
    public string Anchor { get; set; } = "";
}

public class Theme
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> CodeIds { get; set; } = new();

    /// <summary>Reference numbers of the studies with at least one code in this theme (derived in code).</summary>
    public List<int> Studies { get; set; } = new();
}

public record UncodedStudy(int Reference, string Reason);

/// <summary>Coverage of one theme subsection: which of its studies are cited, before and after the fill pass.</summary>
public class ThemeCoverage
{
    public string ThemeId { get; set; } = "";
    public string Field { get; set; } = "";
    public List<int> Studies { get; set; } = new();
    public List<int> CitedAfterWriting { get; set; } = new();
    public List<int> CitedFinal { get; set; } = new();
    public List<int> NotCited { get; set; } = new();
    public string FillPass { get; set; } = "not needed";
}

/// <summary>The second, independent assignment of studies to themes and its agreement with the first.</summary>
public class SecondCoding
{
    public double? Kappa { get; set; }
    public int CellsCompared { get; set; }
    public int Agreements { get; set; }
    public List<string> Disagreements { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>Everything the thematic synthesis decided, written to thematic-codebook.json and the ledger.</summary>
public class ThematicCodebook
{
    public string Method { get; set; } = "Thematic synthesis (Thomas & Harden, 2008): codes from verified findings, grouped into descriptive themes, one subsection per theme.";
    public List<ThematicCode> Codes { get; set; } = new();
    public List<Theme> Themes { get; set; } = new();
    public List<UncodedStudy> Uncoded { get; set; } = new();
    public List<ThemeCoverage> Coverage { get; set; } = new();
    public SecondCoding? SecondCoding { get; set; }

    /// <summary>Included studies cited nowhere in the synthesis or discussion, with the reason.</summary>
    public List<UncodedStudy> NotCited { get; set; } = new();
    public string? FallbackReason { get; set; }

    [JsonIgnore] public IEnumerable<int> CodedStudies => Themes.SelectMany(t => t.Studies).Distinct().OrderBy(r => r);
}

/// <summary>
/// The coding steps of the thematic synthesis. Every code must point at evidence that was verified in code:
/// a key finding whose quote <see cref="StudyExtractor"/> found in the paper, or (for a study without such
/// findings) a verbatim quote that is checked here. Themes are built from codes, and which studies belong to
/// a theme is derived from its codes in code, not taken from the model.
/// </summary>
public static class ThematicSynthesis
{
    /// <summary>Id of the i-th (0-based) key finding of a study, as shown to the model.</summary>
    public static string FindingId(int reference, int index) => $"F{reference}.{index + 1}";

    /// <summary>The verified key findings of a study, by finding id.</summary>
    public static IReadOnlyList<(string Id, EvidencedValue Finding)> VerifiedFindings(StudyExtraction? e) =>
        e == null || e.Error != null
            ? Array.Empty<(string, EvidencedValue)>()
            : e.KeyFindings.Select((f, i) => (FindingId(e.ReferenceNumber, i), f))
                .Where(x => x.f.QuoteVerified && !x.f.Value.Equals("not reported", StringComparison.OrdinalIgnoreCase))
                .ToList();

    /// <summary>Number of themes to ask for: about one per six studies, between 2 and maxThemes.</summary>
    public static (int Min, int Max) ThemeRange(int studies, int maxThemes)
    {
        int max = Math.Max(2, Math.Min(maxThemes, (int)Math.Ceiling(studies / 3.0)));
        int min = Math.Min(max, Math.Max(2, Math.Min(4, studies / 6)));
        return (min, max);
    }

    /// <summary>Target length of a theme subsection: about one paragraph per four studies, 1 to 8.</summary>
    public static int ParagraphsFor(int studies) => Math.Clamp((int)Math.Ceiling(studies / 4.0), 1, 8);

    // ─── 1. Coding ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class CodingAnswer { public List<CodedStudyAnswer>? Studies { get; set; } }
    private sealed class CodedStudyAnswer { public int Ref { get; set; } public List<CodeAnswer>? Codes { get; set; } public string? Reason { get; set; } }
    private sealed class CodeAnswer { public string Label { get; set; } = ""; public List<string>? Findings { get; set; } public string? Quote { get; set; } }

    /// <summary>Codes every study, a batch at a time. Studies that could not be coded are returned with the reason.</summary>
    public static async Task<(List<ThematicCode> Codes, List<UncodedStudy> Uncoded)> CodeStudiesAsync(
        IChatCompletionService chat, string objective, IReadOnlyList<ReferencedPaper> papers,
        IReadOnlyList<StudyExtraction> extractions, int batchSize, int parallelism, Action<int, int>? progress = null)
    {
        var byRef = extractions.ToDictionary(e => e.ReferenceNumber);
        var batches = papers.OrderBy(p => p.ReferenceNumber).Chunk(Math.Max(1, batchSize)).ToList();
        using var throttle = new SemaphoreSlim(Math.Max(1, parallelism));
        int finished = 0;
        var work = batches.Select(async batch =>
        {
            await throttle.WaitAsync();
            try
            {
                var coded = await CodeBatchAsync(chat, objective, batch, byRef);
                progress?.Invoke(Interlocked.Increment(ref finished), batches.Count);
                return coded;
            }
            finally { throttle.Release(); }
        }).ToList();

        var codes = new List<ThematicCode>();
        var uncoded = new List<UncodedStudy>();
        foreach (var task in work)
        {
            var (c, u) = await task;
            codes.AddRange(c);
            uncoded.AddRange(u);
        }
        // Ids in reference order, so the codebook reads the same on every run with the same answers.
        codes = codes.OrderBy(c => c.Reference).ToList();
        for (int i = 0; i < codes.Count; i++) codes[i].Id = $"C{i + 1}";
        return (codes, uncoded.OrderBy(u => u.Reference).ToList());
    }

    /// <summary>The text a study's codes are checked against when it has no verified findings.</summary>
    private static string EvidenceText(ReferencedPaper p, StudyExtraction? e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(p.Abstract);
        if (e != null)
            foreach (var f in e.KeyFindings) if (f.QuoteVerified) sb.AppendLine(f.Quote);
        return sb.ToString();
    }

    private static async Task<(List<ThematicCode>, List<UncodedStudy>)> CodeBatchAsync(
        IChatCompletionService chat, string objective, IReadOnlyList<ReferencedPaper> batch, IReadOnlyDictionary<int, StudyExtraction> byRef)
    {
        var data = new StringBuilder();
        var allowedFindings = new Dictionary<int, HashSet<string>>();
        foreach (var p in batch)
        {
            byRef.TryGetValue(p.ReferenceNumber, out var e);
            var findings = VerifiedFindings(e);
            allowedFindings[p.ReferenceNumber] = findings.Select(f => f.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            data.AppendLine($"STUDY [{p.ReferenceNumber}] {p.Title}");
            if (e != null && e.Error == null) data.AppendLine($"Study type: {e.StudyType}. Method: {e.Method.Value}");
            if (findings.Count > 0)
                foreach (var (id, f) in findings) data.AppendLine($"{id}: {f.Value} (quote: \"{f.Quote}\")");
            else
                data.AppendLine($"No verified findings. Abstract: {(string.IsNullOrWhiteSpace(p.Abstract) ? "(none)" : p.Abstract.Trim())}");
            data.AppendLine();
        }

        string prompt = $$"""
            You are coding studies for the thematic synthesis of a systematic literature review (Thomas & Harden, 2008: line-by-line coding of findings, then descriptive themes).

            REVIEW OBJECTIVE: "{{objective}}"
            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(data.ToString(), "the studies to code: verified findings or abstracts")}}

            TASK: For each study, give 1 to 4 codes. A code is a short descriptive label (3 to 8 words) for one thing the study found, proposed or showed that bears on the review objective. Codes should be specific enough that two studies with the same code report the same kind of finding.
            - Base every code on the study's listed findings and name them in "findings" (e.g. ["F3.1"]). Use only finding ids listed for that study.
            - For a study with "No verified findings", base the code on its abstract and copy a verbatim quote (one sentence, max 40 words, unbroken) from the abstract into "quote"; leave "findings" empty.
            - If a study has nothing that bears on the objective, give "codes": [] and a one-sentence "reason".
            Respond ONLY with a minified JSON object:
            {"studies":[{"ref":3,"codes":[{"label":"...","findings":["F3.1"],"quote":""}],"reason":""}]}
            """;

        var codes = new List<ThematicCode>();
        var uncoded = new List<UncodedStudy>();
        try
        {
            var expected = batch.Select(p => p.ReferenceNumber).ToHashSet();
            var answer = await LlmJson.GetAsync<CodingAnswer>(chat, prompt, PrismaReviewEngine.JsonMode(0.0), a =>
            {
                if (a.Studies == null) return "studies must be a list.";
                var missing = expected.Except(a.Studies.Select(s => s.Ref)).ToList();
                if (missing.Count > 0) return $"every study needs an entry; missing: {string.Join(", ", missing)}.";
                foreach (var s in a.Studies.Where(s => expected.Contains(s.Ref)))
                    foreach (var c in s.Codes ?? new())
                        if ((c.Findings ?? new()).Any(f => !allowedFindings[s.Ref].Contains(f.Trim())))
                            return $"study {s.Ref} uses a finding id that is not listed for it; use only {(allowedFindings[s.Ref].Count == 0 ? "a quote" : string.Join(", ", allowedFindings[s.Ref]))}.";
                return null;
            });

            foreach (var p in batch)
            {
                var s = answer.Studies!.First(x => x.Ref == p.ReferenceNumber);
                byRef.TryGetValue(p.ReferenceNumber, out var e);
                var valid = (s.Codes ?? new()).Where(c => !string.IsNullOrWhiteSpace(c.Label)).Take(4).ToList();
                if (valid.Count == 0)
                {
                    uncoded.Add(new UncodedStudy(p.ReferenceNumber, string.IsNullOrWhiteSpace(s.Reason) ? "No code bearing on the objective." : s.Reason.Trim()));
                    continue;
                }
                string evidence = EvidenceText(p, e);
                foreach (var c in valid)
                {
                    var ids = (c.Findings ?? new()).Select(f => f.Trim().ToUpperInvariant()).Distinct().ToList();
                    string quote = (c.Quote ?? "").Trim();
                    string anchor = ids.Count > 0 ? "verified finding"
                        : CitationSupportChecker.QuoteOccursIn(quote, evidence) ? "verified quote" : "unanchored";
                    codes.Add(new ThematicCode { Reference = p.ReferenceNumber, Label = c.Label.Trim(), FindingIds = ids, Quote = quote, Anchor = anchor });
                }
            }
        }
        catch (Exception ex)
        {
            uncoded.AddRange(batch.Select(p => new UncodedStudy(p.ReferenceNumber, $"The coding call failed ({ex.GetType().Name}).")));
        }
        return (codes, uncoded);
    }

    // ─── 2. Codebook ────────────────────────────────────────────────────────────────────────────────────

    private sealed class CodebookAnswer { public List<ThemeAnswer>? Themes { get; set; } public List<UnassignedAnswer>? Unassigned { get; set; } }
    private sealed class ThemeAnswer { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public List<string>? Codes { get; set; } }
    private sealed class UnassignedAnswer { public string Code { get; set; } = ""; public string Reason { get; set; } = ""; }

    /// <summary>
    /// Groups the codes into descriptive themes. The model must place every coded study in at least one theme
    /// (through one of its codes) or say why its codes fit none; the answer is rejected otherwise.
    /// </summary>
    public static async Task<(List<Theme> Themes, List<UncodedStudy> Unplaced)> BuildCodebookAsync(
        IChatCompletionService chat, string objective, IReadOnlyList<ThematicCode> codes, int maxThemes)
    {
        var studies = codes.Select(c => c.Reference).Distinct().ToList();
        var (min, max) = ThemeRange(studies.Count, maxThemes);
        var ids = codes.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string codeList = string.Join("\n", codes.Select(c => $"{c.Id} [study {c.Reference}]: {c.Label}"));

        string prompt = $$"""
            You are building the codebook for the thematic synthesis of a systematic literature review: group the codes below into descriptive themes.

            REVIEW OBJECTIVE: "{{objective}}"
            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(codeList, "codes, each with the study it came from")}}

            TASK: Group the codes into {{min}} to {{max}} themes. A theme names a pattern that several studies share and that answers part of the objective; it is not a single study. Give each theme a short name (at most 8 words) and a one- or two-sentence description of what unites its codes.
            - Put every code in the theme it fits best; a code may also go into a second theme if it clearly belongs to both.
            - Every study should end up in at least one theme. Only leave a code out if it does not bear on the objective; list it under "unassigned" with a reason.
            - Use only the code ids listed above.
            Respond ONLY with a minified JSON object:
            {"themes":[{"name":"...","description":"...","codes":["C1","C4"]}],"unassigned":[{"code":"C9","reason":"..."}]}
            """;

        var answer = await LlmJson.GetAsync<CodebookAnswer>(chat, prompt, PrismaReviewEngine.JsonMode(0.1), a =>
        {
            if (a.Themes == null || a.Themes.Count == 0) return "themes must be a non-empty list.";
            if (a.Themes.Count > max) return $"give at most {max} themes.";
            if (a.Themes.Any(t => string.IsNullOrWhiteSpace(t.Name) || t.Codes == null || t.Codes.Count == 0)) return "every theme needs a name and at least one code.";
            var unknown = a.Themes.SelectMany(t => t.Codes!).Concat((a.Unassigned ?? new()).Select(u => u.Code)).Where(c => !ids.Contains(c.Trim())).Distinct().ToList();
            if (unknown.Count > 0) return $"unknown code ids: {string.Join(", ", unknown.Take(10))}.";
            var unplaced = StudiesWithoutTheme(codes, a.Themes.Select(t => t.Codes!), (a.Unassigned ?? new()).Select(u => u.Code));
            if (unplaced.Count > 0)
                return $"studies {string.Join(", ", unplaced)} are in no theme and none of their codes is listed as unassigned. Place one of their codes in the best fitting theme, or list the codes under unassigned with a reason.";
            return null;
        });

        var byId = codes.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var themes = answer.Themes!.Select((t, i) =>
        {
            var codeIds = t.Codes!.Select(c => c.Trim().ToUpperInvariant()).Where(byId.ContainsKey).Distinct().ToList();
            return new Theme
            {
                Id = $"T{i + 1}",
                Name = t.Name.Trim().TrimEnd('.'),
                Description = t.Description.Trim(),
                CodeIds = codeIds,
                Studies = codeIds.Select(c => byId[c].Reference).Distinct().OrderBy(r => r).ToList(),
            };
        }).Where(t => t.Studies.Count > 0).ToList();

        var placed = themes.SelectMany(t => t.Studies).ToHashSet();
        var reasons = (answer.Unassigned ?? new()).Where(u => byId.ContainsKey(u.Code.Trim()))
            .GroupBy(u => byId[u.Code.Trim()].Reference).ToDictionary(g => g.Key, g => g.First().Reason);
        var unplacedStudies = studies.Where(s => !placed.Contains(s)).OrderBy(s => s)
            .Select(s => new UncodedStudy(s, reasons.TryGetValue(s, out var r) && !string.IsNullOrWhiteSpace(r) ? $"Codes left out of the themes: {r.Trim()}" : "Codes left out of the themes."))
            .ToList();
        return (themes, unplacedStudies);
    }

    /// <summary>Coded studies that are in no theme and have none of their codes listed as unassigned.</summary>
    public static List<int> StudiesWithoutTheme(IReadOnlyList<ThematicCode> codes, IEnumerable<IEnumerable<string>> themeCodes, IEnumerable<string> unassigned)
    {
        var assigned = themeCodes.SelectMany(c => c).Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = unassigned.Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return codes.GroupBy(c => c.Reference)
            .Where(g => !g.Any(c => assigned.Contains(c.Id)) && !g.Any(c => left.Contains(c.Id)))
            .Select(g => g.Key).OrderBy(r => r).ToList();
    }

    // ─── 3. Second, independent coding ──────────────────────────────────────────────────────────────────

    private sealed class SecondAnswer { public List<SecondStudy>? Studies { get; set; } }
    private sealed class SecondStudy { public int Ref { get; set; } public List<string>? Themes { get; set; } }

    /// <summary>
    /// A second coder sees only the theme names and descriptions and each study's codes, and assigns the
    /// studies to themes. Agreement with the first assignment is Cohen's kappa over every study × theme cell.
    /// The second coding is reported; it does not change the themes.
    /// </summary>
    public static async Task<SecondCoding> SecondCodingAsync(IChatCompletionService chat, IReadOnlyList<Theme> themes, IReadOnlyList<ThematicCode> codes)
    {
        var studies = themes.SelectMany(t => t.Studies).Distinct().OrderBy(r => r).ToList();
        var themeIds = themes.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string themeList = string.Join("\n", themes.Select(t => $"{t.Id}: {t.Name}. {t.Description}"));
        string studyList = string.Join("\n", studies.Select(s => $"Study {s}: {string.Join("; ", codes.Where(c => c.Reference == s).Select(c => c.Label))}"));

        string prompt = $$"""
            You are the second, independent coder in the thematic synthesis of a systematic literature review. The themes are fixed; assign each study to every theme its codes belong to.

            {{PromptSafety.DataOnlyNotice}}

            THEMES:
            {{themeList}}

            {{PromptSafety.Wrap(studyList, "each study's codes")}}

            Respond ONLY with a minified JSON object listing every study:
            {"studies":[{"ref":3,"themes":["T1"]}]}
            """;
        try
        {
            var answer = await LlmJson.GetAsync<SecondAnswer>(chat, prompt, PrismaReviewEngine.JsonMode(0.0), a =>
            {
                if (a.Studies == null) return "studies must be a list.";
                var missing = studies.Except(a.Studies.Select(s => s.Ref)).ToList();
                if (missing.Count > 0) return $"every study needs an entry; missing: {string.Join(", ", missing.Take(15))}.";
                return (a.Studies.SelectMany(s => s.Themes ?? new()).FirstOrDefault(t => !themeIds.Contains(t.Trim())) is string bad) ? $"unknown theme id {bad}." : null;
            });
            var second = answer.Studies!.GroupBy(s => s.Ref).ToDictionary(g => g.Key,
                g => g.SelectMany(s => s.Themes ?? new()).Select(t => t.Trim().ToUpperInvariant()).ToHashSet());
            return CompareCodings(themes, second);
        }
        catch (Exception ex)
        {
            return new SecondCoding { Error = $"The second coding failed: {ex.GetType().Name}." };
        }
    }

    /// <summary>Cohen's kappa between the first assignment (theme study lists) and a second one, cell by cell.</summary>
    public static SecondCoding CompareCodings(IReadOnlyList<Theme> themes, IReadOnlyDictionary<int, HashSet<string>> second)
    {
        var result = new SecondCoding();
        var pairs = new List<(bool, bool)>();
        foreach (var study in themes.SelectMany(t => t.Studies).Distinct().OrderBy(r => r))
        {
            second.TryGetValue(study, out var assigned);
            foreach (var t in themes)
            {
                bool first = t.Studies.Contains(study);
                bool other = assigned != null && assigned.Contains(t.Id.ToUpperInvariant());
                pairs.Add((first, other));
                if (first != other)
                    result.Disagreements.Add($"Study {study}, {t.Id} ({t.Name}): {(first ? "first coding only" : "second coding only")}");
            }
        }
        result.CellsCompared = pairs.Count;
        result.Agreements = pairs.Count(p => p.Item1 == p.Item2);
        result.Kappa = pairs.Count == 0 ? null : Math.Round(ScreeningConfusion.From(pairs).CohensKappa, 3);
        return result;
    }

    // ─── 4. Coverage and text from data ─────────────────────────────────────────────────────────────────

    /// <summary>The distinct reference numbers cited in a text.</summary>
    public static SortedSet<int> CitedIn(string? text) =>
        new(CitationSupportChecker.ExtractCitedSentences(text ?? "", "x").SelectMany(s => s.References));

    /// <summary>
    /// Guard for every revision of a section (coverage fill, peer review): the revision may not lose a study
    /// the section cited, and may only cite numbers in the reference list. Returns null when acceptable.
    /// </summary>
    public static string? RevisionProblem(string before, string after, int referenceCount)
    {
        if (string.IsNullOrWhiteSpace(after)) return "the revision was empty";
        var lost = CitedIn(before).Except(CitedIn(after)).ToList();
        if (lost.Count > 0) return $"the revision dropped the citation of {string.Join(", ", lost.Select(r => $"[{r}]"))}";
        var outOfRange = CitedIn(after).Where(r => r < 1 || r > referenceCount).ToList();
        if (outOfRange.Count > 0) return $"the revision cites numbers outside the reference list: {string.Join(", ", outOfRange)}";
        return null;
    }

    /// <summary>The overview paragraph that opens the synthesis, written from the codebook (no model involved).</summary>
    public static string Overview(ThematicCodebook book, int included)
    {
        var sb = new StringBuilder();
        int coded = book.CodedStudies.Count();
        sb.Append($"The findings of the {included} included {(included == 1 ? "study were" : "studies were")} synthesised thematically. ");
        sb.Append($"{coded} {(coded == 1 ? "study" : "studies")} yielded {book.Codes.Count} {(book.Codes.Count == 1 ? "code" : "codes")}, grouped into {book.Themes.Count} {(book.Themes.Count == 1 ? "theme" : "themes")}: ");
        sb.Append(string.Join("; ", book.Themes.Select(t => $"{t.Name} ({t.Studies.Count} {(t.Studies.Count == 1 ? "study" : "studies")})")));
        sb.Append(". ");
        if (book.Uncoded.Count > 0)
            sb.Append($"{book.Uncoded.Count} {(book.Uncoded.Count == 1 ? "study was" : "studies were")} not placed in a theme; the reasons are listed in the evidence map. ");
        sb.Append("Each theme is reported in its own subsection below, and the evidence map shows which studies contribute to which theme.");
        return sb.ToString();
    }

    /// <summary>One sentence for the report on how many included studies the text cites.</summary>
    public static string CoverageSentence(int cited, int included, IReadOnlyList<UncodedStudy> notCited)
    {
        if (included == 0) return "";
        string s = $"The synthesis and discussion cite {cited} of the {included} included {(included == 1 ? "study" : "studies")}.";
        if (notCited.Count > 0)
            s += $" Not cited: {string.Join("; ", notCited.Select(n => $"[{n.Reference}] {n.Reason.TrimEnd('.')}"))}.";
        return s;
    }

    /// <summary>PRISMA item 13d, written from the codebook.</summary>
    public static string MethodsText(ThematicCodebook? book, string? modelName, bool repairEnabled)
    {
        string model = string.IsNullOrWhiteSpace(modelName) ? "the language model" : $"the language model ({modelName})";
        if (book == null || book.Themes.Count == 0)
        {
            string why = book?.FallbackReason is { Length: > 0 } r ? $" Thematic coding was not used in this run: {r.TrimEnd('.')}." : "";
            return $"The findings were synthesised narratively by {model} from the abstracts and selected full-text passages of the included studies, organised by themes proposed in a preceding outline step.{why} Every cited sentence was then checked against the cited paper (see the automated citation check).";
        }
        var sb = new StringBuilder();
        sb.Append($"The findings were synthesised thematically, following the thematic synthesis method of Thomas and Harden (2008), with {model} performing each step. ");
        sb.Append($"First, the key findings extracted from each study (whose supporting quotes had been found in the paper's text) were coded; each code names the findings it comes from, and for studies without verified findings the code had to quote the abstract verbatim, which was checked in code ({book.Codes.Count(c => c.Anchor != "unanchored")} of {book.Codes.Count} codes were anchored this way). ");
        sb.Append($"Second, the codes were grouped into {book.Themes.Count} descriptive themes; a study belongs to a theme when one of its codes does, and the model's answer was rejected until every coded study was either placed in a theme or explicitly left out with a reason. ");
        if (book.SecondCoding is { } second)
        {
            sb.Append(second.Kappa is double k
                ? $"A second, independent coding assigned the studies to the same themes; agreement over {second.CellsCompared} study-theme decisions was Cohen's kappa = {k.ToString("0.00", CultureInfo.InvariantCulture)} ({second.Disagreements.Count} disagreements, listed in thematic-codebook.json). "
                : $"A second, independent coding was attempted but could not be completed ({second.Error}). ");
        }
        sb.Append("Third, each theme was written up as a subsection from its own studies only, with every study of the theme to be cited; studies still uncited were given to one further revision pass. ");
        sb.Append(repairEnabled
            ? "Finally, the part of every cited sentence attributed to a paper was checked against that paper; sentences judged not supported or only partly supported were rewritten once from the paper's evidence and checked again."
            : "Finally, the part of every cited sentence attributed to a paper was checked against that paper.");
        return sb.ToString();
    }
}
