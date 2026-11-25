using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using AuBtechReviewAgent;
using Xunit;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Every answer type the model fills through LlmJson.GetAsync, fed a null in each field, each list and each list entry in
/// turn (audit T4): the null is read as missing, and the answer is either accepted with no null left where its type allows none, or sent back to the model.
/// It never ends the step with another exception, which is what one null from the model used to do (audit C2).
/// </summary>
public class ModelAnswerNullFuzzTests
{
    /// <summary>The answer types, by name; nested private ones as Outer+Inner.</summary>
    public static readonly string[] AnswerTypes =
    {
        "ModelThemeList", "ModelPlacement", "ModelTensions", "ModelQualityAnswers", "ModelExtraction", "ModelMapAnswer", "ScreeningAnswer",
        "MultivocalSynthesiser+SupportAnswer",
        "ReviewWriter+SummaryAnswer", "ReviewWriter+FrontMatterAnswer", "ReviewWriter+CritiqueAnswer", "ReviewWriter+TextAnswer",
        "ReviewWriter+OutlineAnswer", "ReviewWriter+SectionAnswer", "ReviewWriter+DiscussionAnswer", "ReviewWriter+ClosingAnswer",
        "ThematicSynthesis+SecondAnswer", "ThematicSynthesis+CodebookAnswer", "ThematicSynthesis+CodingAnswer",
        "PrismaReviewEngine+PeerReviewAnswer", "PrismaReviewEngine+SectionsAnswer", "PrismaReviewEngine+CritiqueAnswer", "PrismaReviewEngine+PerspectivesAnswer",
        "StudyExtractor+ExtractionAnswer", "CitationRepairer+RepairAnswer", "RepairSense+SenseAnswer", "RepairSense+TextAnswer", "ArtifactBuilder+Answer",
    };

    public static IEnumerable<object[]> Types() => AnswerTypes.Select(t => new object[] { t });

    /// <summary>The real validators of the public answer types; the others are checked with none, so nothing hides a null.</summary>
    private static readonly Dictionary<Type, Func<object, string?>> Validators = new()
    {
        [typeof(ModelThemeList)] = a => ModelThemeList.Problem((ModelThemeList)a),
        [typeof(ModelPlacement)] = a => ModelPlacement.Problem((ModelPlacement)a, new HashSet<string> { "x" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x" }),
        [typeof(ModelTensions)] = a => ModelTensions.Problem((ModelTensions)a),
        [typeof(ModelQualityAnswers)] = a => ModelQualityAnswers.Validate((ModelQualityAnswers)a),
        [typeof(ScreeningAnswer)] = a => ScreeningAnswer.Validate((ScreeningAnswer)a),
    };

    [Theory]
    [MemberData(nameof(Types))]
    public async Task ANullAnywhereIsSentBackNeverACrash(string name)
    {
        var type = typeof(LlmJson).Assembly.GetType("AuBtechReviewAgent." + name);
        Assert.NotNull(type);
        var sample = Sample(type!, 0);
        var variants = NullVariants(sample).ToList();
        Assert.NotEmpty(variants);
        Validators.TryGetValue(type!, out var validate);

        foreach (var (path, json) in variants)
        {
            var run = (Task<(object? Value, Exception? Error)>)typeof(ModelAnswerNullFuzzTests).GetMethod(nameof(RunAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type!).Invoke(null, new object?[] { json, validate })!;
            var (value, error) = await run;

            if (error != null) Assert.True(error is LlmOutputException, $"{name} {path}: {error.GetType().Name} {error.Message}");
            else Assert.Null(NullLeft(value!, path));
        }
    }

    [Fact]
    public void EveryAnswerTypeTheModelFillsIsInTheList()
    {
        // A new answer type is added to AnswerTypes, so it is fuzzed too: every T given to LlmJson.GetAsync in the code.
        var root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Join(root, "AuBtechReviewAgent.sln"))) root = Path.GetDirectoryName(root);
        root = Path.Join(root!, "AuBtechReviewAgent");
        var named = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"(?:LlmJson\.GetAsync|CachedAsync|CachedAnswerAsync)<(\w+)>").Select(m => m.Groups[1].Value))
            .Where(t => t != "T").ToHashSet();
        var listed = AnswerTypes.Select(t => t.Split('+')[^1]).ToHashSet();
        Assert.Empty(named.Except(listed));
    }

    private static async Task<(object? Value, Exception? Error)> RunAsync<T>(string json, Func<object, string?>? validate) where T : class
    {
        var chat = new FakeChatService().RespondsWith(_ => json);
        try { return (await LlmJson.GetAsync<T>(chat, "Answer.", null, a => validate?.Invoke(a)), null); }
        catch (Exception ex) { return (null, ex); }
    }

    /// <summary>A complete answer of the type: text "x", numbers 1, true, one entry in every list.</summary>
    private static JsonNode? Sample(Type type, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return JsonValue.Create("x");
        if (type == typeof(bool)) return JsonValue.Create(true);
        if (type.IsPrimitive || type == typeof(decimal)) return JsonValue.Create(1);
        if (depth > 5) return null;
        var element = type.IsArray ? type.GetElementType() : type.GetInterfaces().Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
        if (element != null) return new JsonArray(Sample(element, depth + 1));
        var obj = new JsonObject();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            obj[char.ToLowerInvariant(p.Name[0]) + p.Name[1..]] = Sample(p.PropertyType, depth + 1);
        return obj;
    }

    /// <summary>One copy of the sample per field, list and list entry, with that one set to null.</summary>
    private static IEnumerable<(string Path, string Json)> NullVariants(JsonNode sample)
    {
        foreach (var node in Walk(sample).Where(n => n.Parent != null))
        {
            var copy = sample.DeepClone();
            var at = Find(copy, node.GetPath());
            if (at!.Parent is JsonObject o) o[at.GetPropertyName()] = null;
            else if (at.Parent is JsonArray a) a[a.IndexOf(at)] = null;
            yield return (node.GetPath(), copy.ToJsonString());
        }
    }

    private static IEnumerable<JsonNode> Walk(JsonNode node)
    {
        yield return node;
        var children = node switch { JsonObject o => o.Select(kv => kv.Value), JsonArray a => a.AsEnumerable(), _ => Enumerable.Empty<JsonNode?>() };
        foreach (var child in children.OfType<JsonNode>()) foreach (var n in Walk(child)) yield return n;
    }

    private static JsonNode? Find(JsonNode root, string path) => Walk(root).FirstOrDefault(n => n.GetPath() == path);

    /// <summary>The first member that is null where its type allows no null, in an accepted answer; null when there is none.</summary>
    private static string? NullLeft(object value, string path)
    {
        var context = new NullabilityInfoContext();
        foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
        {
            var info = context.Create(p);
            var member = p.GetValue(value);
            if (member == null)
            {
                if (info.ReadState == NullabilityState.NotNull) return $"{path}: {p.Name} is null";
                continue;
            }
            if (member is string || p.PropertyType.Assembly != typeof(LlmJson).Assembly && member is not IEnumerable) continue;
            if (member is IEnumerable list)
            {
                var element = info.ElementType ?? (info.GenericTypeArguments.Length == 1 ? info.GenericTypeArguments[0] : null);
                foreach (var item in list)
                    if (item == null && element is { ReadState: NullabilityState.NotNull }) return $"{path}: {p.Name} has a null entry";
                    else if (item != null && item.GetType().Assembly == typeof(LlmJson).Assembly && NullLeft(item, path) is { } inner) return inner;
            }
            else if (NullLeft(member, path) is { } inner) return inner;
        }
        return null;
    }
}
