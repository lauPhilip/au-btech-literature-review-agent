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

/// <summary>
/// The coding steps of the thematic synthesis. Every code must point at evidence that was verified in code:
/// a key finding whose quote <see cref="StudyExtractor"/> found in the paper, or (for a study without such
/// findings) a verbatim quote that is checked here. Themes are built from codes, and which studies belong to
/// a theme is derived from its codes in code, not taken from the model.
/// </summary>
public static partial class ThematicSynthesis
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
    private sealed class CodeAnswer { public string Label { get; set; } = ""; public List<string>? Findings { get; set; } public string? Quote { get; set; } public List<string>? Questions { get; set; } }

    /// <summary>Codes every study, a batch at a time. Studies that could not be coded are returned with the reason.</summary>
    public static async Task<(List<ThematicCode> Codes, List<UncodedStudy> Uncoded)> CodeStudiesAsync(
        IChatCompletionService chat, string objective, IReadOnlyList<ReferencedPaper> papers,
        IReadOnlyList<StudyExtraction> extractions, int batchSize, int parallelism, Action<int, int>? progress = null, IReadOnlyList<string>? questions = null,
        string criteria = "", SynthesisCache? cache = null, SynthesisReuse? reuse = null)
    {
        questions ??= Array.Empty<string>();
        var byRef = extractions.ToDictionary(e => e.ReferenceNumber);
        var batches = papers.OrderBy(p => p.ReferenceNumber).Chunk(Math.Max(1, batchSize)).ToList();
        using var throttle = new SemaphoreSlim(Math.Max(1, parallelism));
        int finished = 0;
        var work = batches.Select(async batch =>
        {
            await throttle.WaitAsync();
            try
            {
                var coded = await CodeBatchAsync(chat, objective, batch, byRef, questions, criteria, cache);
                progress?.Invoke(Interlocked.Increment(ref finished), batches.Count);
                return coded;
            }
            finally { throttle.Release(); }
        }).ToList();

        var codes = new List<ThematicCode>();
        var uncoded = new List<UncodedStudy>();
        foreach (var task in work)
        {
            var (c, u, reused) = await task;
            codes.AddRange(c);
            uncoded.AddRange(u);
            if (reuse != null) { reuse.CodingAnswers++; if (reused) reuse.CodingAnswersReused++; }
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

    private static async Task<(List<ThematicCode>, List<UncodedStudy>, bool FromCache)> CodeBatchAsync(
        IChatCompletionService chat, string objective, IReadOnlyList<ReferencedPaper> batch, IReadOnlyDictionary<int, StudyExtraction> byRef,
        IReadOnlyList<string> questions, string criteria, SynthesisCache? cache)
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
                foreach (var (id, f) in findings) data.AppendLine($"{id}{QuestionTag(f)}: {f.Value} (quote: \"{f.Quote}\")");
            else
                data.AppendLine($"No verified findings. Abstract: {(string.IsNullOrWhiteSpace(p.Abstract) ? "(none)" : p.Abstract.Trim())}");
            data.AppendLine();
        }

        // With research questions a finding that bears only on one of them is still coded (the real runs dropped such codes).
        var (aim, bearsOn, questionRules, questionField) = CodingQuestionPrompt(objective, questions, criteria);
        string prompt = $$"""
            You are coding studies for the thematic synthesis of a systematic literature review (Thomas & Harden, 2008: line-by-line coding of findings, then descriptive themes).

            {{aim}}
            {{PromptSafety.ReviewerInputNotice}}

            {{PromptSafety.DataOnlyNotice}}

            {{PromptSafety.Wrap(data.ToString(), "the studies to code: verified findings or abstracts")}}

            TASK: For each study, give 1 to 4 codes. A code is a short descriptive label (3 to 8 words) for one thing the study found, proposed or showed that bears on the review objective{{bearsOn}}. Codes should be specific enough that two studies with the same code report the same kind of finding.
            - Base every code on the study's listed findings and name them in "findings" (e.g. ["F3.1"]). Use only finding ids listed for that study.
            - For a study with "No verified findings", base the code on its abstract and copy a verbatim quote (one sentence, max 40 words, unbroken) from the abstract into "quote"; leave "findings" empty.
            - If a study has nothing that bears on the objective{{bearsOn}}, give "codes": [] and a one-sentence "reason".{{questionRules}}
            Respond ONLY with a minified JSON object:
            {"studies":[{"ref":3,"codes":[{"label":"...","findings":["F3.1"],"quote":""{{questionField}}}],"reason":""}]}
            """;

        var codes = new List<ThematicCode>();
        var uncoded = new List<UncodedStudy>();
        bool fromCache = false;
        try
        {
            var expected = batch.Select(p => p.ReferenceNumber).ToHashSet();
            // A reused answer passes the same check again: every study answered, every finding id one of its verified findings.
            string? Validate(CodingAnswer a)
            {
                if (a.Studies == null) return "studies must be a list.";
                var missing = expected.Except(a.Studies.Select(s => s.Ref)).ToList();
                if (missing.Count > 0) return $"every study needs an entry; missing: {string.Join(", ", missing)}.";
                foreach (var s in a.Studies.Where(s => expected.Contains(s.Ref)))
                    foreach (var c in s.Codes ?? new())
                        if ((c.Findings ?? new()).Any(f => !allowedFindings[s.Ref].Contains(f.Trim())))
                            return $"study {s.Ref} uses a finding id that is not listed for it; use only {(allowedFindings[s.Ref].Count == 0 ? "a quote" : string.Join(", ", allowedFindings[s.Ref]))}.";
                return CodeQuestionProblem(a.Studies.Where(s => expected.Contains(s.Ref)).SelectMany(s => (s.Codes ?? new()).Select(c => (s.Ref, c.Questions))), questions);
            }
            (var answer, fromCache) = await CachedAnswerAsync<CodingAnswer>(chat, prompt, PrismaReviewEngine.JsonMode(0.0), Validate, cache, CodingCacheKind, CodingPromptVersion);

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
                var evidence = CanonicalText.Of(EvidenceText(p, e));
                foreach (var c in valid)
                {
                    var ids = (c.Findings ?? new()).Select(f => f.Trim().ToUpperInvariant()).Distinct().ToList();
                    string quote = (c.Quote ?? "").Trim();
                    string anchor = ids.Count > 0 ? "verified finding"
                        : CitationSupportChecker.QuoteOccursIn(quote, evidence) ? "verified quote" : "unanchored";
                    codes.Add(new ThematicCode { Reference = p.ReferenceNumber, Label = c.Label.Trim(), FindingIds = ids, Quote = quote, Anchor = anchor, Questions = CodeQuestions(c.Questions, questions), FromCache = fromCache ? true : null });
                }
            }
        }
        catch (Exception ex)
        {
            uncoded.AddRange(batch.Select(p => new UncodedStudy(p.ReferenceNumber, $"The coding call failed ({ex.GetType().Name}).")));
        }
        return (codes, uncoded, fromCache);
    }

    // ─── 4. Coverage and text from data ─────────────────────────────────────────────────────────────────

    /// <summary>The distinct reference numbers cited in a text.</summary>
    public static SortedSet<int> CitedIn(string? text) =>
        new(CitationSupportChecker.ExtractCitedSentences(text ?? "", "x").SelectMany(s => s.References));

    /// <summary>
    /// Guard for every revision of a section (coverage fill, peer review): the revision may not lose a study
    /// the section cited, may only cite numbers in the reference list, and may add no sentence that cites more than
    /// <see cref="ReviewWriter.MaxReferencesPerSentence"/> references. Returns null when acceptable.
    /// </summary>
    public static string? RevisionProblem(string before, string after, int referenceCount)
    {
        if (string.IsNullOrWhiteSpace(after)) return "the revision was empty";
        var lost = CitedIn(before).Except(CitedIn(after)).ToList();
        if (lost.Count > 0) return $"the revision dropped the citation of {string.Join(", ", lost.Select(r => $"[{r}]"))}";
        var outOfRange = CitedIn(after).Where(r => r < 1 || r > referenceCount).ToList();
        if (outOfRange.Count > 0) return $"the revision cites numbers outside the reference list: {string.Join(", ", outOfRange)}";
        return ReviewWriter.CitationLoadProblem(after, before) is { } load ? $"the revision breaks the rule on references per sentence ({load})" : null;
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
    /// <param name="checkModelName">The model of the citation checks, when it is not <paramref name="modelName"/>.</param>
    public static string MethodsText(ThematicCodebook? book, string? modelName, bool repairEnabled, string? checkModelName = null)
    {
        string model = string.IsNullOrWhiteSpace(modelName) ? "the language model" : $"the language model {MethodsSectionWriter.ModelInBrackets(modelName)}";
        // The checks are named only when another model made them, so the text says which model did what.
        string checkedBy = string.IsNullOrWhiteSpace(checkModelName) || checkModelName == modelName ? "" : $" by a second language model {MethodsSectionWriter.ModelInBrackets(checkModelName)}";
        if (book == null || book.Themes.Count == 0)
        {
            string why = book?.FallbackReason is { Length: > 0 } r ? $" Thematic coding was not used in this run: {r.TrimEnd('.')}." : "";
            return $"The findings were synthesised narratively by {model} from the abstracts and selected full-text passages of the included studies, organised by themes proposed in a preceding outline step.{why} Every cited sentence was then checked against the cited paper{checkedBy} (see the automated citation check).";
        }
        var sb = new StringBuilder();
        sb.Append($"The findings were synthesised thematically, following the thematic synthesis method of Thomas and Harden (2008), with {model} performing each step (descriptive themes only; no analytical themes were developed). ");
        sb.Append($"First, the key findings extracted from each study (whose supporting quotes had been found in the paper's text) were coded; each code names the findings it comes from, and for studies without verified findings the code had to quote the abstract verbatim, which was checked in code ({book.Codes.Count(c => c.Anchor != "unanchored")} of {book.Codes.Count} codes were anchored this way). ");
        sb.Append($"Second, the codes were grouped into {book.Themes.Count} descriptive themes; a study belongs to a theme when one of its codes does, and the model's answer was rejected until every coded study was either placed in a theme or explicitly left out with a reason. ");
        if (ReuseSentence(book.Reuse) is { Length: > 0 } reused) sb.Append(reused).Append(' ');
        if (book.SecondCoding is { } second)
        {
            sb.Append(second.Kappa is double k
                ? $"A second, independent coding assigned the studies to the same themes; agreement over {second.CellsCompared} study-theme decisions was Cohen's kappa = {k.ToString("0.00", CultureInfo.InvariantCulture)} ({second.Disagreements.Count} disagreement{(second.Disagreements.Count == 1 ? "" : "s")}, listed in thematic-codebook.json). " +
                  Agreement.Sentence(second.Agreements, second.CellsCompared) + Agreement.SameModelSentence("codings")
                : $"A second, independent coding was attempted but could not be completed ({second.Error}). ");
        }
        sb.Append($"Third, each theme was written up as a subsection from its own studies only, with no sentence citing more than {ReviewWriter.MaxReferencesPerSentence} studies; " +
            $"in a theme of up to {ReviewWriter.CiteEveryUpTo} studies every study was to be cited, and studies still uncited were given to one further revision pass, while a larger theme was synthesised from the studies that report its claims most directly, and its uncited studies are listed with the citation coverage. ");
        sb.Append(repairEnabled
            ? $"Finally, the part of every cited sentence attributed to a paper was checked against that paper{checkedBy}; sentences judged not supported or only partly supported were rewritten once from the paper's evidence and checked again."
            : $"Finally, the part of every cited sentence attributed to a paper was checked against that paper{checkedBy}.");
        return sb.ToString();
    }
}
