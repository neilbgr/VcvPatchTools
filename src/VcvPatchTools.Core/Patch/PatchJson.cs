using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VcvPatchTools.Core.Patch;

/// <summary>
/// Lenient typed reads of patch.json values: null when a value is missing or of the wrong kind, instead of the
/// InvalidOperationException that JsonNode.GetValue throws. Numbers are read whatever they are held as (parsed
/// from a file, or set in memory as an int, a long or a double), which GetValue does not do either.
/// </summary>
public static class PatchJson
{
    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    public static double? Double(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            && double.IsFinite(number)
                ? number
                : null;

    /// <summary>A whole number; a fractional one (1.5) is not.</summary>
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        string text = value.ToJsonString();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
        {
            return whole;
        }
        // "2.0" or "2e3" are still whole numbers.
        return Double(node) is double number && number == Math.Floor(number) && number >= long.MinValue && number < long.MaxValue
            ? (long)number
            : null;
    }

    public static int? Int(JsonNode? node) => Long(node) is long value && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;

    /// <summary>A rack position, [x, y] in HP and rows: (0, 0) when missing, rounded when fractional.</summary>
    /// <exception cref="InvalidDataException">"pos" is there but is not two numbers.</exception>
    public static (int X, int Y) Position(JsonObject module)
    {
        JsonNode? pos = module["pos"];
        if (pos is null)
        {
            return (0, 0);
        }
        if (pos is JsonArray { Count: 2 } array && Double(array[0]) is double x && Double(array[1]) is double y
            && Math.Abs(x) < int.MaxValue && Math.Abs(y) < int.MaxValue)
        {
            return ((int)Math.Round(x, MidpointRounding.AwayFromZero), (int)Math.Round(y, MidpointRounding.AwayFromZero));
        }
        throw new InvalidDataException($"Module {Describe(module)} has an invalid \"pos\" (expected [x, y]): {pos.ToJsonString()}.");
    }

    /// <summary>"7 (Fundamental/VCO)": how error messages name a module.</summary>
    public static string Describe(JsonObject module) =>
        $"{module["id"]?.ToJsonString() ?? "without id"} ({String(module["plugin"]) ?? "?"}/{String(module["model"]) ?? "?"})";
}