using System.Text.Json.Nodes;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Bridge;

public sealed record DetectionResult(PatchOrigin Origin, List<ModuleSlug> CardinalOnlyModules, List<ModuleSlug> RackOnlyModules);

public static class PatchDetector
{
    public static DetectionResult Detect(PatchArchive patch)
    {
        List<ModuleSlug> cardinalHits = new List<ModuleSlug>();
        List<ModuleSlug> rackHits = new List<ModuleSlug>();

        foreach (JsonObject module in EnumerateModules(patch.Root))
        {
            string? plugin = PatchJson.String(module["plugin"]);
            string? model = PatchJson.String(module["model"]);
            if (plugin is null || model is null)
            {
                continue;
            }

            // The "Cardinal" plugin slug is bundled by, and only exists inside, Cardinal itself.
            // Any module instance carrying it proves the patch was (re)saved by Cardinal.
            if (plugin == ModuleMap.CardinalPlugin)
            {
                cardinalHits.Add(new ModuleSlug(plugin, model));
            }
            else if (plugin == ModuleMap.CorePlugin && ModuleMap.AllCoreModels.Contains(model))
            {
                rackHits.Add(new ModuleSlug(plugin, model));
            }
        }

        PatchOrigin origin = cardinalHits.Count > 0
            ? PatchOrigin.Cardinal
            : rackHits.Count > 0
                ? PatchOrigin.Rack
                : PatchOrigin.Ambiguous;

        return new DetectionResult(origin, cardinalHits, rackHits);
    }

    public static IEnumerable<JsonObject> EnumerateModules(JsonObject root)
    {
        if (root["modules"] is not JsonArray modules)
        {
            yield break;
        }

        foreach (JsonNode? m in modules)
        {
            if (m is JsonObject obj)
            {
                yield return obj;
            }
        }
    }
}