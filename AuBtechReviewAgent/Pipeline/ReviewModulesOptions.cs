using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace AuBtechReviewAgent;

/// <summary>
/// The "ReviewModules" settings: modules that are shown as a preview while they are being built. A module in
/// preview has its pages and a "Preview" chip on its cards although the cards still say "coming soon", so it can be
/// developed and tried in Development without anyone seeing it in production. The setting is ignored outside
/// Development, so a stray value on the server cannot open an unfinished module.
/// </summary>
public sealed class ReviewModulesOptions
{
    /// <summary>Keys of the modules shown as a preview, e.g. ["multivocal"].</summary>
    public List<string> Preview { get; set; } = new();

    public bool IsPreview(string moduleKey) => Preview.Any(k => k.Equals(moduleKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>Can people open this module's pages here? Yes when one of its cards is available, or it is in preview.</summary>
    public bool CanOpen(IReviewModule module) => module.Cards.Any(c => c.Available) || IsPreview(module.Key);

    /// <summary>
    /// Should the site describe this card's review (on the model card, the privacy page and the like)? Yes when the
    /// card is available, or its module is in preview. So a review being built is described in Development and stays
    /// out of sight in production until its card is switched on (<see cref="ReviewMethod.Available"/>), which is the
    /// one switch that makes it public everywhere.
    /// </summary>
    public bool Shows(string cardKey)
    {
        var module = ReviewModules.ForCard(cardKey);
        var card = module.Cards.FirstOrDefault(c => c.Key.Equals(cardKey, StringComparison.OrdinalIgnoreCase));
        return card != null && (card.Available || IsPreview(module.Key));
    }

    /// <summary>Reads the section; outside Development the preview list is always empty.</summary>
    public static ReviewModulesOptions From(IConfiguration configuration, bool isDevelopment)
    {
        var options = configuration.GetSection("ReviewModules").Get<ReviewModulesOptions>() ?? new ReviewModulesOptions();
        if (!isDevelopment) options.Preview.Clear();
        return options;
    }
}
