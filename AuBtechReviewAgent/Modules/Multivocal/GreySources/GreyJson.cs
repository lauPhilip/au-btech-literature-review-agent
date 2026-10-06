using System.Text.Json;

namespace AuBtechReviewAgent;

/// <summary>Small readers for API answers whose fields may be missing or null.</summary>
internal static class GreyJson
{
    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    public static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    /// <summary>A string or number field as text.</summary>
    public static string Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";
}
