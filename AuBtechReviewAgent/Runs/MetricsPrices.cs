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
/// An optional price table for the downloads' estimated_cost (issue #414), bound from "Metrics:Prices". Off unless a
/// deployment fills it in: prices change, so the code holds none, and the metrics store keeps tokens only. The estimate
/// is worked out when the file is downloaded, so it always uses today's table, not the price on the day of the run.
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
}
