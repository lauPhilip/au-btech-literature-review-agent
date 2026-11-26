using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuBtechReviewAgent;

/// <summary>What the language edit measured in one text: sentence lengths and the share of passive sentences.</summary>
/// <param name="Sentences">The number of sentences.</param>
/// <param name="MeanSentenceWords">The mean sentence length in words, citation markers left out.</param>
/// <param name="ShareOver25Words">The share of sentences longer than the 25-word target, from 0 to 1.</param>
/// <param name="SharePassive">The share of sentences with a passive verb, from 0 to 1.</param>
/// <param name="LongestSentenceWords">The length of the longest sentence in words.</param>
public sealed record PlainEnglishMeasures(int Sentences, double MeanSentenceWords, double ShareOver25Words, double SharePassive, int LongestSentenceWords);

/// <summary>
/// The code checks of plain technical English (based on ASD-STE100, at about 80 per cent; issue #599). They are simple
/// and deterministic on purpose: sentences are split with citations and common abbreviations kept whole, words are
/// counted without citation markers, and the passive and noun-cluster tests are heuristics. A sentence over
/// <see cref="MaxSentenceWords"/> words is a fault; a long noun cluster or paragraph is only flagged.
/// </summary>
public static class PlainEnglish
{
    /// <summary>The target length of a descriptive sentence.</summary>
    public const int TargetSentenceWords = 25;

    /// <summary>A sentence longer than this is a fault the language edit must split.</summary>
    public const int MaxSentenceWords = 30;

    /// <summary>A paragraph with more sentences than this is flagged.</summary>
    public const int MaxParagraphSentences = 6;

    /// <summary>A noun cluster longer than this is flagged.</summary>
    public const int MaxNounCluster = 3;

    // A full stop after one of these does not end a sentence.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g.", "i.e.", "al.", "etc.", "cf.", "vs.", "approx.", "fig.", "figs.", "eq.", "no.", "nos.", "dr.", "prof.", "ca.", "resp.", "viz.",
        "ref.", "refs.", "vol.", "pp.", "p.", "ch.", "sec.", "mr.", "ms.", "st.",
    };

    private static readonly Regex Boundary = new(@"(?<=[.!?][""”’)]?)\s+(?=[\p{Lu}\[(""“‘\d])", RegexOptions.Compiled);
    private static readonly Regex Marker = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex WordToken = new(@"[\p{L}\p{N}]+(?:['’.,/\-][\p{L}\p{N}]+)*", RegexOptions.Compiled);

    // A form of "to be", an optional adverb or "not", then a past participle: regular (-ed) or a common irregular one.
    private static readonly Regex Passive = new(
        @"\b(?:am|is|are|was|were|be|been|being)\s+(?:(?:not|\w+ly)\s+)?(?:(?!(?:need|indeed|seed|speed|feed|proceed|exceed|succeed|embed|hundred|shed|red|bed)\b)\w{2,}ed|" +
        @"done|found|given|known|made|seen|shown|taken|written|chosen|drawn|kept|held|set|put|read|run|built|led|met|sent|told|thought|brought|" +
        @"left|lost|meant|understood|begun|broken|driven|grown|hidden|spoken|thrown|cut|split|spread|paid|said|sought|won|worn|undertaken|withdrawn)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The sentences of a text, split after ., ! or ? when the next sentence starts, but not after an abbreviation or an initial.</summary>
    public static List<string> Sentences(string? text)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return sentences;
        foreach (var piece in Boundary.Split(text.Trim()).Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (sentences.Count > 0 && EndsWithAbbreviation(sentences[^1])) sentences[^1] += " " + piece;
            else sentences.Add(piece);
        }
        return sentences;
    }

    private static bool EndsWithAbbreviation(string sentence)
    {
        string last = sentence[(sentence.LastIndexOfAny(new[] { ' ', '(', '\n' }) + 1)..];
        return Abbreviations.Contains(last) || Regex.IsMatch(last, @"^\p{Lu}\.$");
    }

    /// <summary>The paragraphs of a text, split at blank lines.</summary>
    public static List<string> Paragraphs(string? text) =>
        string.IsNullOrWhiteSpace(text) ? new List<string>()
            : Regex.Split(text.Trim(), @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>The words of a sentence without its citation markers; a number such as 1,234 or 3.5 is one word.</summary>
    public static int Words(string sentence) => WordToken.Matches(Marker.Replace(sentence ?? "", " ")).Count;

    /// <summary>Whether a sentence has a passive verb ("records were screened"); a heuristic, for a measurement only.</summary>
    public static bool IsPassive(string sentence) => Passive.IsMatch(Marker.Replace(sentence ?? "", " "));

    /// <summary>The sentences of a text with more than <see cref="MaxSentenceWords"/> words, with their length.</summary>
    public static List<(string Sentence, int Words)> LongSentences(string? text) =>
        Paragraphs(text).SelectMany(Sentences).Select(s => (s, Words(s))).Where(x => x.Item2 > MaxSentenceWords).ToList();

    /// <summary>The measurements of a text; null for a text without sentences.</summary>
    public static PlainEnglishMeasures? Measure(string? text)
    {
        var sentences = Paragraphs(text).SelectMany(Sentences).Where(s => Words(s) > 0).ToList();
        if (sentences.Count == 0) return null;
        var words = sentences.Select(Words).ToList();
        return new PlainEnglishMeasures(sentences.Count, Math.Round(words.Average(), 1),
            Math.Round(words.Count(w => w > TargetSentenceWords) / (double)sentences.Count, 2),
            Math.Round(sentences.Count(IsPassive) / (double)sentences.Count, 2), words.Max());
    }

    /// <summary>
    /// The flags of a text, as sentences for the ledger: each noun cluster of more than <see cref="MaxNounCluster"/> words
    /// and each paragraph of more than <see cref="MaxParagraphSentences"/> sentences. A flag never fails an edit.
    /// </summary>
    public static List<string> Flags(string? text)
    {
        var flags = new List<string>();
        foreach (var (paragraph, i) in Paragraphs(text).Select((p, i) => (p, i + 1)))
        {
            var sentences = Sentences(paragraph);
            if (sentences.Count > MaxParagraphSentences)
                flags.Add($"Paragraph {i} has {sentences.Count} sentences (more than {MaxParagraphSentences}).");
            foreach (var cluster in sentences.SelectMany(NounClusters))
                flags.Add($"Noun cluster of {cluster.Split(' ').Length} words: \"{cluster}\".");
        }
        return flags;
    }

    /// <summary>
    /// The runs of more than <see cref="MaxNounCluster"/> noun-like words in a sentence ("software defect prediction model
    /// accuracy"). Quotations, citation markers and brackets are left out, and a word with a capital letter or a digit
    /// (a name such as GPT-4 or PRISMA, or a number) ends a run, so names, numbers, quotes and citations are never flagged.
    /// </summary>
    public static List<string> NounClusters(string sentence)
    {
        string text = Regex.Replace(sentence ?? "", @"""[^""]*""|“[^”]*”|‘[^’]*’|\[[^\]]*\]|\([^)]*\)", " ; ");
        var clusters = new List<string>();
        var run = new List<string>();
        void Close()
        {
            if (run.Count > MaxNounCluster) clusters.Add(string.Join(" ", run));
            run.Clear();
        }
        bool first = true;
        foreach (Match token in Regex.Matches(text, @"[\p{L}\p{N}][\p{L}\p{N}'’\-]*|[^\s\p{L}\p{N}]"))
        {
            string word = token.Value;
            if (first && word.Length > 1 && char.IsUpper(word[0]) && word[1..].All(c => !char.IsUpper(c))) word = char.ToLowerInvariant(word[0]) + word[1..];
            first = false;
            if (!NounLike(word)) { Close(); continue; }
            run.Add(word);
            if (IsPlural(word)) Close(); // a plural noun ends an English noun cluster
        }
        Close();
        return clusters;
    }

    private static bool NounLike(string word) =>
        word.Length >= 2 && word.All(c => char.IsLower(c) || c == '-') && !FunctionWords.Contains(word) && !Verbs.Contains(word)
        && !word.EndsWith("ly", StringComparison.Ordinal) && !(word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal));

    private static bool IsPlural(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal) && !word.EndsWith("is", StringComparison.Ordinal);

    private static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "this", "that", "these", "those", "and", "or", "but", "nor", "of", "in", "on", "at", "by", "for", "with", "from", "to",
        "into", "onto", "over", "under", "about", "as", "than", "then", "so", "such", "not", "no", "all", "any", "each", "every", "both", "either",
        "neither", "some", "many", "much", "more", "most", "less", "few", "other", "own", "same", "is", "are", "was", "were", "be", "been", "being",
        "am", "has", "have", "had", "do", "does", "did", "can", "could", "may", "might", "must", "shall", "should", "will", "would", "it", "its",
        "they", "them", "their", "we", "our", "us", "he", "she", "his", "her", "you", "your", "which", "who", "whom", "whose", "what", "when",
        "where", "why", "how", "while", "if", "because", "although", "though", "whereas", "also", "only", "very", "well", "yet", "still", "even",
        "just", "there", "here", "up", "out", "off", "between", "among", "across", "through", "during", "before", "after", "since", "until",
        "against", "within", "without", "per", "via", "versus", "whether", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "first", "second", "third", "often", "rarely", "never", "always", "however", "thus", "therefore", "hence", "instead", "rather",
    };

    // Common verbs of academic prose that would otherwise read as nouns and join a run.
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    {
        "report", "reports", "show", "shows", "suggest", "suggests", "find", "finds", "use", "uses", "need", "needs", "include", "includes",
        "describe", "describes", "propose", "proposes", "provide", "provides", "support", "supports", "remain", "remains", "make", "makes",
        "help", "helps", "require", "requires", "increase", "increases", "reduce", "reduces", "improve", "improves", "keep", "keeps", "lead",
        "leads", "allow", "allows", "enable", "enables", "affect", "affects", "depend", "depends", "differ", "differs", "address", "addresses",
        "examine", "examines", "indicate", "indicates", "identify", "identifies", "apply", "applies", "offer", "offers", "rely", "relies",
        "tend", "tends", "seem", "seems", "appear", "appears", "become", "becomes", "give", "gives", "take", "takes", "know", "knows",
        "mean", "means", "get", "gets", "vary", "varies", "compare", "compares", "measure", "measures", "map", "maps", "lack", "lacks",
    };
}
