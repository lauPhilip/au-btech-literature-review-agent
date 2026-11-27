using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AuBtechReviewAgent;

/// <summary>
/// The shape of a theme subsection (issue #464). In the multivocal run 50e9a8e3 the rule "cite every source of the
/// theme at least once", with its fill pass, turned themes of 60 to 130 sources into lists ("A debate workflow uses ...
/// [106]." about fifty times) and the same findings came back theme after theme. A theme of at most
/// <see cref="CiteEveryUpTo"/> sources still cites every one, with the fill pass, as before; a larger theme is
/// synthesised from the sources that report its claims most directly, and its uncited sources are listed in the
/// coverage, in code. Code sends back a large theme written as a list of one-source summaries, and asks a theme that
/// repeats a claim another theme already reports to refer to that theme instead.
/// </summary>
public static partial class ReviewWriter
{
    /// <summary>A theme of at most this many sources cites every one of them, with the fill pass; a larger one is synthesised.</summary>
    public const int CiteEveryUpTo = 5;

    /// <summary>The share of cited sentences that may be one-source summaries in a row before a large theme reads as a list.</summary>
    public const double ListingShare = 0.6;

    /// <summary>A large theme is judged as a list only from this many cited sentences on.</summary>
    public const int ListingMinSentences = 6;

    /// <summary>The theme prompt's rule on which sources to cite: every one in a small theme (as before), the most direct in a large one.</summary>
    public static string WhichSourcesRule(WritingProfile profile, IReadOnlyList<int> sources)
    {
        string s = profile.Source, ss = profile.Sources;
        if (sources.Count <= CiteEveryUpTo)
            return $"Cite every one of these {ss} at least once: {string.Join(", ", sources.Select(r => $"[{r}]"))}. A {s} may be cited together with others when they report the same finding (\"... [3, 7, 12]\").";
        return $"Synthesise, do not list: state each claim of the theme once, supported by the {ss} that report it most directly, and do not write one sentence per {s} or repeat a claim. " +
               $"This theme has {sources.Count} {ss}; you need not cite every one. A {s} whose evidence adds nothing to a claim already made stays uncited, and the review lists the uncited {ss} itself.";
    }

    /// <summary>
    /// Why a large theme's subsection reads as a list of one-source summaries, or null (issue #464): among its cited
    /// sentences, more than <see cref="ListingShare"/> each cite exactly one source, a different one from the sentence
    /// before. Two sentences on the same source, and a sentence that compares sources, do not count. Public for unit tests.
    /// </summary>
    public static string? ListingProblem(string text, int themeSources, WritingProfile profile)
    {
        if (themeSources <= CiteEveryUpTo) return null;
        var cited = CitationSupportChecker.SplitSentences(text).Select(CitationSupportChecker.ReferencesIn).Where(r => r.Count > 0).ToList();
        if (cited.Count < ListingMinSentences) return null;
        int listed = cited.Where((refs, i) => refs.Count == 1 && (i == 0 || !cited[i - 1].SequenceEqual(refs))).Count();
        if (listed <= cited.Count * ListingShare) return null;
        return $"{listed} of its {cited.Count} cited sentences each summarise one {profile.Source}, so the subsection reads as a list of {profile.Sources}. " +
               $"Group the {profile.Sources} that report the same thing into one claim, compare and contrast them, and leave out a {profile.Source} that adds nothing new.";
    }

    /// <summary>
    /// The sentences of later subsections that say again what an earlier subsection says (the same source cited, and most
    /// of the words the same, <see cref="SaysAgain"/>, the same result figure with its unit, such as "34 %", issue #674, or
    /// the same finding in other words, <see cref="RetoldSources"/>, issue #744), each with the heading of the subsection
    /// that says it first. A sentence that tells again the findings of two or more sources from earlier in its own
    /// subsection, a summing up of what was just said, is listed too, with its own heading. Public for unit tests.
    /// </summary>
    public static IReadOnlyList<(string Field, string Sentence, string EarlierHeading)> RepeatedClaims(IReadOnlyList<SynthesisSection> sections)
    {
        var repeats = new List<(string, string, string)>();
        var earlier = new List<(string Sentence, IReadOnlyList<int> Refs, string Heading)>();
        foreach (var section in sections)
        {
            var own = CitationSupportChecker.SplitSentences(section.Text).Select(s => (Sentence: s, Refs: CitationSupportChecker.ReferencesIn(s))).ToList();
            for (int i = 0; i < own.Count; i++)
            {
                var (sentence, refs) = own[i];
                if (refs.Count == 0) continue;
                if (earlier.FirstOrDefault(e => e.Refs.Intersect(refs).Any() && (SaysAgain(sentence, e.Sentence) || UnitFigures(sentence).Overlaps(UnitFigures(e.Sentence)))) is { Heading: { } heading })
                    repeats.Add((section.Field, sentence, heading));
                else if (RetoldSources(sentence, earlier.Select(e => (e.Sentence, e.Refs))) is { Count: > 0 } retold)
                    repeats.Add((section.Field, sentence, earlier.First(e => e.Refs.Contains(retold[0])).Heading));
                else if (refs.Count >= 2 && RetoldSources(sentence, own.Take(i).Select(o => (o.Sentence, o.Refs))).Count >= 2)
                    repeats.Add((section.Field, sentence, section.Heading));
            }
            earlier.AddRange(own.Where(x => x.Refs.Count > 0).Select(x => (x.Sentence, x.Refs, section.Heading)));
        }
        return repeats;
    }

    /// <summary>
    /// One revision of each subsection that repeats a claim an earlier subsection reports (issue #464): the repeated
    /// sentences are pointed out and the model refers to the earlier subsection instead, keeping the citations. A revision
    /// that drops a citation, cites outside the theme, breaks the rule on references per sentence or leaves as many
    /// repeats is rejected and the subsection is kept; every decision is written to the theme's coverage.
    /// </summary>
    public static async Task RevisitRepeatsAsync(IChatCompletionService chat, WritingProfile profile, ReviewEvidence evidence,
        List<SynthesisSection> sections, List<ThemeCoverage> coverage, int referenceCount)
    {
        var repeats = RepeatedClaims(sections);
        foreach (var group in repeats.GroupBy(r => r.Field))
        {
            var section = sections.First(x => x.Field == group.Key);
            var themeCoverage = coverage.FirstOrDefault(c => c.Field == group.Key);
            var allowed = ThemeSources(section, evidence) ?? Array.Empty<int>();
            string list = string.Join("\n", group.Select(r => r.EarlierHeading == section.Heading
                ? $"- \"{r.Sentence}\" (sums up what this subsection already said)"
                : $"- \"{r.Sentence}\" (already reported under \"{r.EarlierHeading}\")"));
            string prompt = $$"""
                You are revising one theme subsection of {{profile.ReviewShort}}. Some of its sentences say again what an earlier subsection already reports, so the review repeats itself.

                THEME: {{section.Heading}}

                CURRENT SUBSECTION:
                {{section.Text}}

                REPEATED SENTENCES:
                {{list}}

                TASK: A finding told again in other words is a repeat too. Rewrite each repeated sentence so it adds what is specific to this theme, or shorten it to a brief pointer to the earlier subsection by its heading in words ("as reported under ..."), keeping its citation. Delete a sentence that only sums up what this subsection already said. Change nothing else. Keep every existing citation, and cite only {{profile.Sources}} of this theme or already cited here.
                {{CitationLoadRule(profile)}}
                {{ProseCleaner.PlainProseRule}}
                Respond ONLY with a valid minified JSON object:
                {"text":"the revised subsection with inline [n] citations"}
                """;
            string note;
            try
            {
                using (LlmStage.Begin("theme-repeats"))
                {
                    var answer = await LlmJson.GetAsync<TextAnswer>(chat, prompt, LlmJson.JsonMode(0.2), a =>
                        string.IsNullOrWhiteSpace(a.Text) ? "text must not be empty." : CitationLoadProblem(a.Text, section.Text));
                    string revised = MethodsSectionWriter.StripMarkdownEmphasis(answer.Text!.Trim());
                    var others = sections.Select(x => x.Field == section.Field ? new SynthesisSection { Field = x.Field, Heading = x.Heading, Text = revised } : x).ToList();
                    int left = RepeatedClaims(others).Count(r => r.Field == section.Field);
                    string? problem = ThematicSynthesis.RevisionProblem(section.Text, revised, referenceCount)
                        ?? (allowed.Count == 0 ? null : OutsideTheme(section.Text, revised, allowed, profile))
                        ?? (left >= group.Count() ? $"it still repeats {left} sentence(s)" : null);
                    if (problem == null) section.Text = revised;
                    note = problem == null ? $"{group.Count()} sentence(s) that repeated an earlier theme were revised ({left} left)." : $"Revision of {group.Count()} repeated sentence(s) rejected: {problem}; the subsection was kept.";
                }
            }
            catch (Exception ex)
            {
                note = $"Revision of {group.Count()} repeated sentence(s) failed ({ex.GetType().Name}); the subsection was kept.";
            }
            if (themeCoverage != null) (themeCoverage.WritingNotes ??= new()).Add(note);
        }
    }
}
