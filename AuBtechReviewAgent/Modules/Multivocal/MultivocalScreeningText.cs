using System.Globalization;

namespace AuBtechReviewAgent;

/// <summary>
/// What the method, the paper, the statement on AI use and the run view say about the screening, written in code from
/// the screening file (<see cref="MultivocalSecondScreening"/>). A run screened before the targeted rule, with no audit
/// seed, keeps the words it had: every source screened twice, κ over all of them.
/// </summary>
public static class MultivocalScreeningText
{
    /// <summary>The method's opening sentences on the screening, up to the counts of the decisions.</summary>
    public static string Method(MultivocalScreeningFile s)
    {
        var c = MultivocalSecondScreening.Count(s, null);
        if (!c.Targeted)
        {
            string kappa = s.Kappa is double k ? $" The two screenings agreed with Cohen's κ = {K(k)}." : "";
            return $"Each of the {Count(s.Sources.Count, "source")} found was screened twice by the language model against the protocol's criteria, independently and with the steps in a different order (G9, G10).{kappa} ";
        }
        return $"Each of the {Count(s.Sources.Count, "source")} found was screened once by the language model against the protocol's criteria (G9, G10). " +
               $"Following the rapid-review practice of single screening with a verified sample, a source was screened a second time, independently and with the steps in a different order, " +
               "when the first screening included it, was unsure (low or medium confidence, or no summary to judge by) or found text that looked like instructions to the model, " +
               $"and when it fell in a random audit sample of {Share(s)} of its pool (at least {s.AuditMinimum ?? MultivocalSecondScreening.AuditMinimum}), " +
               $"drawn with the seed {s.AuditSeed} from every source the first screening decided, whatever it decided. " +
               $"Of the {Num(c.Screened)} sources screened, {Num(c.ScreenedTwice)} {Were(c.ScreenedTwice)} screened twice and {Num(c.Once)} once: " +
               $"{Num(c.TargetedTotal)} for a reason above ({Num(c.ForIncluded)} included, {Num(c.ForUncertain)} unsure, {Num(c.ForFlagged)} flagged), of which {Num(c.TargetedAgreed)} agreed with the first screening, " +
               $"and {Num(c.Audit)} in the audit sample, {Num(c.AuditOnly)} of them for that reason alone. " +
               $"Cohen's κ was computed on the audit sample only, so the targeted second screenings do not enter it: κ = {Kappa(c.Kappa)} over {Count(c.Audit, "source")}. " +
               "Both screenings are the same model at temperature 0, so κ measures how stable the model's decisions are, not agreement between two people. ";
    }

    /// <summary>With a formal pool: each pool's κ on its audit sample, and its targeted agreement apart.</summary>
    public static string PoolAgreement(MultivocalScreeningFile s)
    {
        if (s.AuditSeed == null)
            return $"Cohen's κ between the two screenings was {Kappa(s.KappaGrey)} for the grey sources, {Kappa(s.KappaFormal)} for the academic sources and {Kappa(s.Kappa)} for both.";
        var (g, f) = (MultivocalSecondScreening.Count(s, false), MultivocalSecondScreening.Count(s, true));
        return $"On the audit sample, Cohen's κ between the two screenings was {Kappa(g.Kappa)} for the grey sources ({Num(g.Audit)} in the sample), {Kappa(f.Kappa)} for the academic sources ({Num(f.Audit)}) and {Kappa(s.Kappa)} for both; " +
               $"of the targeted second screenings, {Num(g.TargetedAgreed)} of {Num(g.TargetedTotal)} agreed with the first for the grey sources and {Num(f.TargetedAgreed)} of {Num(f.TargetedTotal)} for the academic sources.";
    }

    /// <summary>How the statement on AI use names the screening: "twice" only when every source was screened twice (or in an older run).</summary>
    public static string AiUseTask(MultivocalScreeningFile s, string sources, string criteria) =>
        s.AuditSeed == null || MultivocalSecondScreening.Count(s, null).AllTwice
            ? $"screening {sources} twice against {criteria}"
            : $"screening {sources} against {criteria}, once each and again when the first screening included a source, was unsure or found text that looked like instructions, or drew it for a random audit sample";

    /// <summary>The agreement for the statement on AI use: κ on the audit sample and the targeted agreement, apart.</summary>
    public static string AiUseAgreement(MultivocalScreeningFile s, bool bothPools)
    {
        if (s.AuditSeed == null)
            return bothPools
                ? $"The two screenings agreed with Cohen's kappa = {Kappa(s.KappaGrey)} for the grey sources, {Kappa(s.KappaFormal)} for the academic sources and {Kappa(s.Kappa)} for both. "
                : s.Kappa is double k ? $"The two screenings agreed with Cohen's kappa = {K(k)}. " : "";
        var c = MultivocalSecondScreening.Count(s, null);
        string kappa = bothPools
            ? $"{Kappa(s.KappaGrey)} for the grey sources, {Kappa(s.KappaFormal)} for the academic sources and {Kappa(s.Kappa)} for both"
            : Kappa(s.Kappa);
        return $"On the random audit sample of {Count(c.Audit, "source")}, the two screenings agreed with Cohen's kappa = {kappa}; " +
               $"{Num(c.TargetedAgreed)} of {Num(c.TargetedTotal)} targeted second screenings agreed with the first. " +
               "Both screenings were the same model at temperature 0, so kappa measures its stability, not agreement between people. ";
    }

    /// <summary>The screening line of the facts the abstract may use.</summary>
    public static string Facts(MultivocalScreeningFile s, int screened)
    {
        var c = MultivocalSecondScreening.Count(s, null);
        return !c.Targeted
            ? $"Screened twice against the criteria: {screened}. "
            : $"Screened against the criteria: {screened}, each once and {c.ScreenedTwice} of them a second time ({c.TargetedTotal} because the first screening included them, was unsure or flagged them; {c.Audit} in a random audit sample). ";
    }

    /// <summary>The label of the screening step in the flow diagram and the funnel.</summary>
    public static string StepLabel(MultivocalScreeningFile? s) => s != null && s.AuditSeed == null ? "Screened twice" : "Screened";

    /// <summary>The screening box of a TikZ flow diagram: "Screened twice: n", or "Screened: n" with how many twice.</summary>
    public static string FlowBox(MultivocalScreeningFile s, int screened, int twice) =>
        s.AuditSeed == null ? $"Screened twice: {screened}" : $@"Screened: {screened}\\{{\footnotesize of which twice: {twice}}}";

    /// <summary>The note beside "Screened" in a grey literature review's funnel on the run view.</summary>
    public static string? FunnelNote(MultivocalScreeningFile s)
    {
        if (s.AuditSeed == null) return s.Kappa is double k ? $"twice; agreement κ = {K(k)}" : null;
        var c = MultivocalSecondScreening.Count(s, null);
        return $"{Num(c.ScreenedTwice)} twice ({Num(c.TargetedAgreed)} of {Num(c.TargetedTotal)} targeted agreed); κ = {Kappa(c.Kappa)} on an audit sample of {Num(c.Audit)}";
    }

    private static string Share(MultivocalScreeningFile s) =>
        ((int)System.Math.Round((s.AuditShare ?? MultivocalSecondScreening.AuditShare) * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    private static string Kappa(double? k) => k is double v ? K(v) : "not computed";
    private static string K(double k) => k.ToString("0.00", CultureInfo.InvariantCulture);
    /// <summary>
    /// The method's sentences on the decisions flagged for the reviewer (#473): how many and why, each counted once by
    /// its first reason (disagreement, text like instructions, an unsure model, a failed call), how many the reviewer
    /// looked at and changed, and how many flagged decisions were left unseen, exclusions apart, since an unseen flagged
    /// exclusion stands as the model made it.
    /// </summary>
    public static string Flagged(MultivocalScreeningFile s)
    {
        var flagged = s.Sources.Where(x => x.Uncertain).ToList();
        int reviewed = s.Sources.Count(x => x.Reviewed);
        int changed = s.Sources.Count(x => x.Reviewed && x.ModelDecision != null && x.ModelDecision != x.Decision);
        string looked = $"The reviewer looked at {Count(reviewed, "decision")} and changed {Num(changed)}";
        const string onFile = "the model's decision stays on file next to the reviewer's. ";
        if (flagged.Count == 0) return $"No decision was flagged for the reviewer. {looked}; {onFile}";
        var reasons = flagged.GroupBy(FlagReason).OrderBy(g => g.Key).Select(g => $"{Num(g.Count())} {ReasonText(g.Key)}");
        var unseen = flagged.Where(x => !x.Reviewed).ToList();
        int unseenExcluded = unseen.Count(x => x.Decision == "Excluded");
        string left = unseen.Count == 0 ? "every flagged decision was looked at"
            : $"{Num(unseen.Count)} of the flagged decisions {Were(unseen.Count)} not looked at, {Num(unseenExcluded)} of them {(unseenExcluded == 1 ? "an exclusion that stands" : "exclusions that stand")} as the model made {(unseenExcluded == 1 ? "it" : "them")}";
        return $"{Num(flagged.Count)} {(flagged.Count == 1 ? "decision was" : "decisions were")} flagged for the reviewer: {string.Join("; ", reasons)}. {looked}; {left}; {onFile}";
    }

    /// <summary>The first reason a decision was flagged, in the order <see cref="Flagged"/> names them.</summary>
    public static int FlagReason(GreyScreening x) =>
        x.Decision == "Error" ? 3
        : x.First != null && x.Second != null && x.First.IsIncluded != x.Second.IsIncluded ? 0
        : PromptSafety.AnyStrong(x.InjectionFlags) ? 1
        : 2;

    private static string ReasonText(int reason) => reason switch
    {
        0 => "because the two screenings disagreed, so the source was kept",
        1 => "because the source's text looked like instructions to the model",
        2 => "because the model was unsure (low confidence, or no summary to judge by)",
        _ => "because the model call failed, so the source was not screened",
    };

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Were(int n) => n == 1 ? "was" : "were";
    private static string Count(int n, string noun) => $"{Num(n)} {(n == 1 ? noun : noun + "s")}";
}
