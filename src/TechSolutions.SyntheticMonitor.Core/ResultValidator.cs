using System.Globalization;
using System.Text.Json;

namespace TechSolutions.SyntheticMonitor;

public static class ResultValidator
{
    /// <summary>Returns the list of mismatches; empty means the result matches the golden case.</summary>
    public static IReadOnlyList<string> Validate(ExpectedResult expected, JsonElement actual, double areaTolerance)
    {
        var errors = new List<string>();

        if (!actual.TryGetProperty("lawnAreaSqft", out var area) || area.ValueKind != JsonValueKind.Number)
            errors.Add("lawnAreaSqft missing");
        else
        {
            var value = area.GetDouble();
            var allowed = Math.Abs(expected.LawnAreaSqft) * areaTolerance;
            if (Math.Abs(value - expected.LawnAreaSqft) > allowed)
                errors.Add(string.Create(CultureInfo.InvariantCulture,
                    $"lawnAreaSqft {value:0.##} outside {expected.LawnAreaSqft:0.##} ±{areaTolerance:P0}"));
        }

        if (!actual.TryGetProperty("polygons", out var polygons) || polygons.ValueKind != JsonValueKind.Array)
            errors.Add("polygons missing");
        else if (polygons.GetArrayLength() != expected.PolygonCount)
            errors.Add($"polygon count {polygons.GetArrayLength()} != {expected.PolygonCount}");

        actual.TryGetProperty("flags", out var flags);
        foreach (var (name, expectedValue) in expected.Flags)
        {
            if (flags.ValueKind != JsonValueKind.Object || !flags.TryGetProperty(name, out var flag)
                || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add($"flag {name} missing");
            else if (flag.GetBoolean() != expectedValue)
                errors.Add($"flag {name} = {flag.GetBoolean()} (expected {expectedValue})");
        }

        if (!actual.TryGetProperty("modelVersion", out var model) || string.IsNullOrWhiteSpace(model.GetString()))
            errors.Add("modelVersion missing");

        return errors;
    }
}
