using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>The price of one model, per million tokens, as the provider lists it on a given day.</summary>
public class ModelPrice
{
    public double InputPerMillion { get; set; }

    /// <summary>The price of an input token served from the provider's cache; null: the same as <see cref="InputPerMillion"/>.</summary>
    public double? CachedInputPerMillion { get; set; }

    /// <summary>The price of an output token; reasoning tokens are output tokens and cost the same.</summary>
    public double OutputPerMillion { get; set; }
}

/// <summary>
/// An optional price table for the estimated cost of a run, bound from "Metrics:Prices": the downloads' estimated_cost
/// (issue #414) and the Metrics page's cost columns (issue #504). Off when empty: prices change, so the code holds
/// none, and the metrics store keeps tokens only. The repository's appsettings.json sets Mistral's list prices. The
/// estimate is worked out when it is shown, so it always uses today's table, not the price on the day of the run.
/// </summary>
public class MetricsPrices
{
    public static readonly MetricsPrices None = new();

    /// <summary>The currency of every price, such as "EUR" or "USD".</summary>
    public string Currency { get; set; } = "";

    /// <summary>The prices by the model name the calls were sent to (llm-calls.json ModelRequested).</summary>
    public Dictionary<string, ModelPrice> Models { get; set; } = new();

    public bool Enabled => Models.Count > 0 && Currency.Length > 0;

    /// <summary>
    /// The estimated cost of a run, rounded to four decimals, or null when the table is off, the run did not count its
    /// tokens by model, or a model that used tokens has no price.
    /// </summary>
    public double? Cost(RunMetrics run)
    {
        if (!Enabled || run.CostByModel == null) return null;
        double total = 0;
        foreach (var (model, cost) in run.CostByModel)
        {
            long input = cost.InputTokens ?? 0, cached = Math.Min(cost.CachedInputTokens ?? 0, input), output = cost.OutputTokens ?? 0;
            if (input + output == 0) continue;
            if (!Models.TryGetValue(model, out var price)) return null;
            total += ((input - cached) * price.InputPerMillion + cached * (price.CachedInputPerMillion ?? price.InputPerMillion) + output * price.OutputPerMillion) / 1_000_000;
        }
        return Math.Round(total, 4);
    }

    /// <summary>The models of a run that used tokens but have no price; empty when the table is off or the run did not count its tokens by model.</summary>
    public IReadOnlyList<string> Unpriced(RunMetrics run) =>
        !Enabled || run.CostByModel == null
            ? Array.Empty<string>()
            : run.CostByModel.Where(kv => (kv.Value.InputTokens ?? 0) + (kv.Value.OutputTokens ?? 0) > 0 && !Models.ContainsKey(kv.Key))
                .Select(kv => kv.Key).ToList();

    /// <summary>The mean cost per run over the runs that have one, or null when none has.</summary>
    public double? MeanCost(IEnumerable<RunMetrics> runs)
    {
        var known = runs.Select(Cost).OfType<double>().ToList();
        return known.Count == 0 ? null : known.Average();
    }

    /// <summary>The summed cost of the runs that have one, or null when none has.</summary>
    public double? TotalCost(IEnumerable<RunMetrics> runs)
    {
        var known = runs.Select(Cost).OfType<double>().ToList();
        return known.Count == 0 ? null : known.Sum();
    }

    /// <summary>A cost as the page shows it, to the cent: "$0.37", "€0.37", "£0.37", or "0.37 DKK" for another currency.</summary>
    public string Format(double cost)
    {
        string amount = cost.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);
        return Currency.ToUpperInvariant() switch
        {
            "USD" => "$" + amount,
            "EUR" => "€" + amount,
            "GBP" => "£" + amount,
            _ => amount + " " + Currency,
        };
    }
}
