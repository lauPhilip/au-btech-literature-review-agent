using System;
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace AuBtechReviewAgent;

// A null from the model is read as a missing value, never a crash (audit C2): one null used to end a whole step with a
// NullReferenceException instead of getting a repair round. Whether a missing value is acceptable is decided in code, by the
// answer type's validator, exactly as for a value the model left out; the model's own null is never trusted to mean anything.
public static partial class LlmJson
{
    /// <summary>The send-back when a validator itself failed on the answer (it met a value it did not expect).</summary>
    public const string ValidatorFailedProblem =
        "a field of the answer was missing or null where a value is required; give every field of the required structure a value.";

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { NullIsMissing } },
    };

    /// <summary>
    /// A null where the answer type allows none is read as if the field were missing: the property keeps its default (an empty
    /// list, an empty object), and text reads as empty text, which the validators already check with IsNullOrWhiteSpace, so the
    /// model gets their own exact send-back ("every theme needs a name"). A field the type marks optional (string?, List?) stays null.
    /// </summary>
    private static void NullIsMissing(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        var context = new NullabilityInfoContext();
        foreach (var property in info.Properties)
        {
            if (property.PropertyType.IsValueType || property.Set is not { } set) continue;
            if (property.AttributeProvider is not PropertyInfo member || context.Create(member).WriteState != NullabilityState.NotNull) continue;
            property.Set = property.PropertyType == typeof(string)
                ? (owner, value) => set(owner, value ?? "")
                : (owner, value) => { if (value != null) set(owner, value); };
            property.IsSetNullable = true;
        }
    }

    private static string JsonProblem(JsonException ex) => $"The answer was not valid JSON ({ex.Message.Split('.')[0]}).";

    /// <summary>The validator's problem ("" when there is none); a validator that throws on the answer is a send-back, logged.</summary>
    private static string Check<T>(Func<T, string?> validate, T value)
    {
        try { return validate(value) ?? ""; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.For(typeof(LlmJson).FullName!).LogWarning(ex, "The validator for {Type} failed on the model's answer; sent back to the model.", typeof(T).Name);
            return ValidatorFailedProblem;
        }
    }

    /// <summary>
    /// Tidies a parsed answer so no null is left where its type allows none: a null entry in a list ("themes":[null]) is
    /// dropped as a missing entry, anywhere in the answer. Returns a send-back only for what cannot be read as missing: a
    /// required field with no default that is still null, or a null entry in a fixed array. Null when the answer is clean.
    /// </summary>
    public static string? NullProblem(object value) => NullProblem(value, new NullabilityInfoContext(), 0);

    private static string? NullProblem(object value, NullabilityInfoContext context, int depth)
    {
        var type = value.GetType();
        if (depth > 8 || !type.IsClass || type.Namespace is { } ns && (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal))) return null;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.GetMethod == null || property.PropertyType.IsValueType) continue;
            var info = context.Create(property);
            var member = property.GetValue(value);
            string name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if (member == null)
            {
                if (info.ReadState == NullabilityState.NotNull) return $"\"{name}\" is missing; give it a value, or an empty list [] where there is nothing to list.";
                continue;
            }
            if (member is string) continue;
            if (member is IEnumerable list)
            {
                var element = info.ElementType ?? (info.GenericTypeArguments.Length == 1 ? info.GenericTypeArguments[0] : null);
                bool entriesRequired = element is { ReadState: NullabilityState.NotNull };
                if (entriesRequired && member is IList { IsFixedSize: false } growable)
                    for (int i = growable.Count - 1; i >= 0; i--) if (growable[i] == null) growable.RemoveAt(i);
                foreach (var item in list)
                {
                    if (item == null)
                    {
                        if (entriesRequired) return $"the list \"{name}\" has a null entry; leave it out or give it a value.";
                        continue;
                    }
                    if (NullProblem(item, context, depth + 1) is { } inner) return inner;
                }
            }
            else if (NullProblem(member, context, depth + 1) is { } inner) return inner;
        }
        return null;
    }
}
