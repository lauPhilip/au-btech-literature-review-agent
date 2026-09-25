// Screening evaluation for TraceableAI.
//
// Runs the review pipeline's own screening prompt (PrismaReviewEngine.ScreenPaperAsync) over a dataset
// whose inclusion decisions were made by human reviewers, and reports how well the model agrees with them:
// recall, precision, specificity, F1 and Cohen's kappa, plus every paper the model missed.
//
// Usage:
//   set MISTRAL_API_KEY=...            (PowerShell: $env:MISTRAL_API_KEY="...")
//   dotnet run --project tools/ScreeningEval -- --data review.csv \
//       --inclusion "..." --exclusion "..." [--max-excluded 200] [--seed 42] [--out eval-results.json]
//
// Datasets: any ASReview-format CSV (title, abstract, label_included). The SYNERGY collection
// (https://github.com/asreview/synergy-dataset) has 26 real systematic reviews in this format; take the
// inclusion/exclusion criteria from the original review's paper so the comparison is fair.

using System.Text.Json;
using AuBtechReviewAgent;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

var options = ParseArgs(args);
if (!options.TryGetValue("data", out var dataPath) || !options.TryGetValue("inclusion", out var inclusion))
{
    Console.WriteLine("Usage: ScreeningEval --data <file.csv> --inclusion \"...\" [--exclusion \"...\"] [--max-excluded N] [--seed 42] [--out results.json]");
    return 1;
}
string exclusion = options.GetValueOrDefault("exclusion", "None.");
int? maxExcluded = options.TryGetValue("max-excluded", out var me) ? int.Parse(me) : null;
int seed = options.TryGetValue("seed", out var sd) ? int.Parse(sd) : 42;
string outPath = options.GetValueOrDefault("out", "eval-results.json");

string? apiKey = Environment.GetEnvironmentVariable("MISTRAL_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Set the MISTRAL_API_KEY environment variable first.");
    return 1;
}

var records = ScreeningEvaluation.LoadCsv(File.ReadAllText(dataPath));
var (sample, excludedWeight) = ScreeningEvaluation.Sample(records, maxExcluded, seed);
Console.WriteLine($"Loaded {records.Count} labelled records ({records.Count(r => r.Included)} relevant). Screening {sample.Count} (seed {seed}).");

#pragma warning disable SKEXP0070
var kernel = Kernel.CreateBuilder().AddMistralChatCompletion(PrismaReviewEngine.ModelId, apiKey).Build();
#pragma warning restore SKEXP0070
var chat = new RecordingChatCompletionService(kernel.GetRequiredService<IChatCompletionService>(), PrismaReviewEngine.ModelId);

var results = new List<object>();
var pairs = new List<(bool Gold, bool Predicted)>();
int done = 0;
foreach (var record in sample)
{
    var paper = new AcademicPaper(record.Id, record.Title, record.Abstract, $"Published: {record.Year}",
        record.Authors.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), "", record.Doi);
    string decision, reasoning;
    try
    {
        using (LlmStage.Begin("screening-eval"))
        {
            var json = await PrismaReviewEngine.ScreenPaperAsync(chat, paper, inclusion, exclusion);
            decision = json.TryGetProperty("decision", out var d) ? d.GetString() ?? "Excluded" : "Excluded";
            reasoning = json.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
        }
    }
    catch (Exception ex)
    {
        decision = "Error";
        reasoning = ex.Message;
    }

    bool predicted = decision.Equals("Included", StringComparison.OrdinalIgnoreCase);
    if (decision != "Error") pairs.Add((record.Included, predicted));
    results.Add(new { record.Id, record.Title, Gold = record.Included ? "Included" : "Excluded", Model = decision, Reasoning = reasoning });
    if (++done % 10 == 0) Console.WriteLine($"  {done}/{sample.Count}");
}

var sampleMatrix = ScreeningConfusion.From(pairs);
var estimated = ScreeningConfusion.From(pairs, excludedWeight);
string F(double v) => ScreeningEvaluation.Format(v);

Console.WriteLine();
Console.WriteLine($"On the screened sample ({pairs.Count} records, {sample.Count - pairs.Count} errors):");
Console.WriteLine($"  Recall {F(sampleMatrix.Recall)}  Precision {F(sampleMatrix.Precision)}  Specificity {F(sampleMatrix.Specificity)}  F1 {F(sampleMatrix.F1)}  Kappa {F(sampleMatrix.CohensKappa)}");
if (excludedWeight > 1)
    Console.WriteLine($"  Estimated on the full dataset (irrelevant records weighted x{excludedWeight:0.##}): Precision {F(estimated.Precision)}  F1 {F(estimated.F1)}  Kappa {F(estimated.CohensKappa)}");
Console.WriteLine($"  Missed relevant papers (false negatives): {sampleMatrix.FalseNegatives}");

var output = new
{
    Dataset = Path.GetFileName(dataPath),
    GeneratedUtc = DateTime.UtcNow,
    Inclusion = inclusion,
    Exclusion = exclusion,
    Seed = seed,
    RecordsInDataset = records.Count,
    RelevantInDataset = records.Count(r => r.Included),
    Screened = sample.Count,
    Errors = sample.Count - pairs.Count,
    ExcludedWeight = excludedWeight,
    Sample = Metrics(sampleMatrix),
    EstimatedFullDataset = Metrics(estimated),
    RunSettings = chat.Summarize(RecordingChatCompletionService.AppVersion),
    Records = results,
};
File.WriteAllText(outPath, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Details written to {outPath}");
return 0;

static object Metrics(ScreeningConfusion m) => new
{
    m.TruePositives, m.FalsePositives, m.TrueNegatives, m.FalseNegatives,
    m.Recall, m.Precision, m.Specificity, m.F1, m.Accuracy, m.CohensKappa
};

static Dictionary<string, string> ParseArgs(string[] args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        string key = args[i][2..];
        map[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
    }
    return map;
}
