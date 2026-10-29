using System.Text.Json.Nodes;

namespace VcvPatchTools.Core.Patch;

/// <summary>
/// Reads a .vcv patch into a typed, read-only document of its modules and cables
/// (<see cref="PatchArchive"/> handles the file format, and writing a patch back).
/// </summary>
public static class PatchReader
{
    public static PatchDocument Read(Stream stream) => Parse(ReadJson(stream));

    public static PatchDocument Read(byte[] bytes) => Parse(ReadJson(bytes));

    public static JsonObject ReadJson(Stream stream)
    {
        using MemoryStream buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return ReadJson(buffer.ToArray());
    }

    public static JsonObject ReadJson(byte[] bytes) => PatchArchive.Read(bytes).Root;

    /// <exception cref="InvalidDataException">
    /// The patch is malformed: "modules" or "cables" is not an array, or a module has no id, a non-string plugin/model or an invalid position.
    /// A cable missing one of its ends is skipped, as Rack does.
    /// </exception>
    public static PatchDocument Parse(JsonObject root)
    {
        List<PatchModule> modules = new List<PatchModule>();
        foreach (JsonNode? node in Array(root, "modules"))
        {
            if (node is not JsonObject module)
            {
                continue;
            }

            long id = PatchJson.Long(module["id"])
                ?? throw new InvalidDataException($"A module has no valid \"id\": {Abbreviate(module)}.");
            (int column, int row) = PatchJson.Position(module);
            Dictionary<int, double> parameters = new Dictionary<int, double>();
            foreach (JsonNode? param in module["params"] as JsonArray ?? new JsonArray())
            {
                if (param is JsonObject p && PatchJson.Int(p["id"]) is int paramId && PatchJson.Double(p["value"]) is double value)
                {
                    parameters[paramId] = value;
                }
            }

            modules.Add(new PatchModule(id, Text(module, "plugin"), Text(module, "model"), column, row)
            {
                Params = parameters,
                LeftModuleId = PatchJson.Long(module["leftModuleId"]),
                RightModuleId = PatchJson.Long(module["rightModuleId"]),
                // A module's data is its own: an object for most, a string for some (Ildaeil keeps a Carla XML project).
                LearnedNotes = Integers((module["data"] as JsonObject)?["notes"]),
                LearnedCcs = Integers((module["data"] as JsonObject)?["ccs"]),
                Labels = Labels(module["data"] as JsonObject),
            });
        }

        List<PatchCable> cables = new List<PatchCable>();
        long fallbackId = 0;
        foreach (JsonNode? node in Array(root, "cables"))
        {
            if (node is not JsonObject cable)
            {
                continue;
            }

            fallbackId++;
            if (PatchJson.Long(cable["outputModuleId"]) is not long outputModuleId || PatchJson.Int(cable["outputId"]) is not int outputId
                || PatchJson.Long(cable["inputModuleId"]) is not long inputModuleId || PatchJson.Int(cable["inputId"]) is not int inputId)
            {
                continue;
            }
            cables.Add(new PatchCable(
                PatchJson.Long(cable["id"]) ?? fallbackId,
                new PortRef(outputModuleId, outputId),
                new PortRef(inputModuleId, inputId),
                PatchJson.String(cable["color"])));
        }

        JsonNode? version = root["version"];
        return version is null || PatchJson.String(version) is not null
            ? new PatchDocument(PatchJson.String(version), modules, cables)
            : throw new InvalidDataException($"The patch \"version\" is not a string: {version.ToJsonString()}.");
    }

    private static JsonArray Array(JsonObject root, string key) => root[key] switch
    {
        null => new JsonArray(),
        JsonArray array => array,
        JsonNode other => throw new InvalidDataException($"The patch \"{key}\" is not an array: {Abbreviate(other)}."),
    };

    private static string Text(JsonObject module, string key) => module[key] switch
    {
        null => "",
        JsonNode value => PatchJson.String(value)
            ?? throw new InvalidDataException($"Module {PatchJson.Describe(module)} has a \"{key}\" that is not a string: {Abbreviate(value)}."),
    };

    private static string Abbreviate(JsonNode node)
    {
        string json = node.ToJsonString();
        return json.Length <= 80 ? json : json[..77] + "...";
    }

    private static List<string> Labels(JsonObject? data)
    {
        if (data is null)
        {
            return new List<string>();
        }
        if (PatchJson.String(data["label"]) is string label)
        {
            return new List<string> { label };
        }
        JsonArray? channels = (data["faders"] ?? data["buttons"]) as JsonArray;
        return channels is null
            ? new List<string>()
            : channels.Select(c => PatchJson.String((c as JsonObject)?["label"]) ?? "").ToList();
    }

    private static List<int> Integers(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => PatchJson.Int(n) ?? -1).ToList() : new List<int>();
}