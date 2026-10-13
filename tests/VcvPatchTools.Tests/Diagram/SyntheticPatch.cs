using System.Text;
using System.Text.Json.Nodes;

namespace VcvPatchTools.Tests;

/// <summary>A large, deterministic patch built in memory: common modules, cables between random ports.</summary>
internal static class SyntheticPatch
{
    private static readonly (string Model, int Inputs, int Outputs)[] models =
    {
        ("VCO", 4, 4), ("VCF", 4, 2), ("VCA", 6, 2), ("ADSR", 6, 1), ("LFO", 5, 4), ("VCMixer", 9, 5), ("SEQ3", 5, 16), ("Quantizer", 1, 1), ("Delay", 6, 2), ("Scope", 3, 2),
    };

    private static readonly string[] colors = { "#ff0000", "#ffb437", "#00b56e", "#3695ef", "#8b4ade", "#f3f3f3" };

    /// <summary>patch.json bytes (a plain JSON patch reads like an archive's).</summary>
    public static byte[] Bytes(int moduleCount, int cableCount, int seed = 42)
    {
        Random random = new Random(seed);
        JsonArray modules = new JsonArray();
        List<(long Id, int Inputs, int Outputs)> placed = new List<(long, int, int)>();
        for (int i = 0; i < moduleCount; i++)
        {
            (string model, int inputs, int outputs) = models[random.Next(models.Length)];
            long id = 1000 + i;
            modules.Add(new JsonObject
            {
                ["id"] = id,
                ["plugin"] = "Fundamental",
                ["model"] = model,
                ["version"] = "2.0",
                ["params"] = new JsonArray(),
                ["pos"] = new JsonArray { (i % 20) * 12, i / 20 },
            });
            placed.Add((id, inputs, outputs));
        }

        JsonArray cables = new JsonArray();
        for (int i = 0; i < cableCount; i++)
        {
            (long fromId, _, int outputs) = placed[random.Next(placed.Count)];
            (long toId, int inputs, _) = placed[random.Next(placed.Count)];
            cables.Add(new JsonObject
            {
                ["id"] = i + 1,
                ["outputModuleId"] = fromId,
                ["outputId"] = random.Next(outputs),
                ["inputModuleId"] = toId,
                ["inputId"] = random.Next(inputs),
                ["color"] = colors[random.Next(colors.Length)],
            });
        }

        JsonObject root = new JsonObject { ["version"] = "2.6.0", ["modules"] = modules, ["cables"] = cables };
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}