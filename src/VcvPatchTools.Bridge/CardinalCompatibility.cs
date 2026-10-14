using System.Text.Json.Nodes;
using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Bridge;

/// <summary>A module of the patch that official Cardinal can't load, <paramref name="Count"/> instances of it.</summary>
public sealed record CardinalGap(ModuleSlug Module, CardinalAvailability Availability, int Count);

/// <summary>Which modules of a patch official Cardinal would drop when loading it, and which only the neilbgr edition bundles.</summary>
public static class CardinalCompatibility
{
    /// <summary>Empty when the embedded module list wasn't generated (nothing to tell then).</summary>
    public static List<CardinalGap> Find(JsonObject root, CardinalModules? modules = null)
    {
        CardinalModules known = modules ?? CardinalModules.Embedded;
        if (!known.IsKnown)
        {
            return new List<CardinalGap>();
        }

        return PatchDetector.EnumerateModules(root)
            .Select(m => (Plugin: PatchJson.String(m["plugin"]), Model: PatchJson.String(m["model"])))
            .Where(m => m.Plugin is not null && m.Model is not null)
            .Select(m => new ModuleSlug(m.Plugin!, m.Model!))
            .GroupBy(m => m)
            .Select(g => new CardinalGap(g.Key, known.Check(g.Key.Plugin, g.Key.Model).Availability, g.Count()))
            .Where(g => g.Availability != CardinalAvailability.Official)
            .OrderBy(g => g.Module.Plugin, StringComparer.Ordinal)
            .ThenBy(g => g.Module.Model, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One line per kind of gap, the way the converter reports them.</summary>
    public static IEnumerable<string> Warnings(IReadOnlyList<CardinalGap> gaps)
    {
        List<CardinalGap> missing = gaps.Where(g => g.Availability == CardinalAvailability.Missing).ToList();
        if (missing.Count > 0)
        {
            yield return $"Not bundled with Cardinal, dropped when it loads the patch: {List(missing)}.";
        }
        List<CardinalGap> neilbgr = gaps.Where(g => g.Availability == CardinalAvailability.NeilbgrEdition).ToList();
        if (neilbgr.Count > 0)
        {
            yield return $"Only in the neilbgr edition of Cardinal ({CardinalModules.NeilbgrEditionUrl}), dropped by official Cardinal: {List(neilbgr)}.";
        }
    }

    private static string List(IEnumerable<CardinalGap> gaps) =>
        string.Join(", ", gaps.Select(g => g.Count > 1 ? $"{g.Module.Plugin}/{g.Module.Model} ×{g.Count}" : $"{g.Module.Plugin}/{g.Module.Model}"));
}