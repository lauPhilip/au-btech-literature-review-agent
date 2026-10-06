using System;
using System.Collections.Generic;
using System.Linq;

namespace AuBtechReviewAgent;

/// <summary>
/// One kind of review, built as a module: its own recipe of steps, its own ledger, pages and report, on top of the
/// shared core (sources, model access, the citation check, the run folder). A module offers one or more cards on the
/// start page; a card is the module with preset options, so five cards need only two modules. See
/// docs/roadmap/review-modules.md for the design.
///
/// This first version only describes a module. Running a review through the module, its stages and its archive
/// files are added in later steps (M2 to M4); until then the systematic module's runs go through
/// <see cref="PrismaReviewEngine"/> as before.
/// </summary>
public interface IReviewModule
{
    /// <summary>Short, stable key written to every run's run.json, e.g. "systematic".</summary>
    string Key { get; }

    /// <summary>Name shown to people, e.g. "Systematic review".</summary>
    string Name { get; }

    /// <summary>Start of the module's page addresses, e.g. "/review".</summary>
    string RoutePrefix { get; }

    /// <summary>The cards this module offers on the start page.</summary>
    IReadOnlyList<ReviewMethod> Cards { get; }
}

/// <summary>Every module the app knows. Adding a kind of review means adding a module here.</summary>
public static class ReviewModules
{
    public static readonly IReadOnlyList<IReviewModule> All = new IReviewModule[]
    {
        new SystematicModule(),
        new MultivocalModule(),
    };

    /// <summary>The module of every run that has no run.json (runs made before modules existed).</summary>
    public static IReviewModule Default => All[0];

    /// <summary>The module with this key, or null.</summary>
    public static IReviewModule? Find(string? key) =>
        All.FirstOrDefault(m => m.Key.Equals(key?.Trim() ?? "", StringComparison.OrdinalIgnoreCase));

    /// <summary>The module that offers this card; the default module for an unknown card.</summary>
    public static IReviewModule ForCard(string? cardKey) =>
        All.FirstOrDefault(m => m.Cards.Any(c => c.Key.Equals(cardKey?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))) ?? Default;
}
