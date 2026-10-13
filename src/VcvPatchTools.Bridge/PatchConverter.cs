using System.Text.Json.Nodes;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Bridge;

public sealed class ConversionResult
{
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Converts the module list of a loaded patch between the VCV Rack ("Core" plugin) and
/// Cardinal ("Cardinal" plugin) representations of the host-dependent MIDI/Audio/etc. modules.
/// Mutates patch.Root in place.
/// </summary>
public static class PatchConverter
{
    /// <summary>Gate cells of Cardinal's HostMIDIGate; VCV Rack's MIDI-Gate and CV-Gate have 16.</summary>
    private const int cardinalGateCells = 18;

    private const int rackGateCells = 16;

    private const int learnedCcCells = 16;

    private static readonly Random idRandom = new();

    /// <exception cref="InvalidDataException">The patch is malformed: no "modules" array, a position that is not [x, y], a MIDI module without id.</exception>
    public static ConversionResult Convert(PatchArchive patch, PatchOrigin targetOrigin, bool remapCableColors = true)
    {
        ConversionResult result = new ConversionResult();

        JsonArray modulesArray = patch.Root["modules"] as JsonArray
            ?? throw new InvalidDataException("Patch has no \"modules\" array.");
        JsonArray cablesArray = patch.Root["cables"] switch
        {
            null => new JsonArray(),
            JsonArray array => array,
            _ => throw new InvalidDataException("The patch \"cables\" is not an array."),
        };
        patch.Root["cables"] ??= cablesArray;

        // Checked before anything is changed, so a rejected patch is left as it was.
        List<JsonObject> modules = new List<JsonObject>();
        for (int i = 0; i < modulesArray.Count; i++)
        {
            if (modulesArray[i] is JsonObject module)
            {
                PatchJson.Position(module);
                modules.Add(module);
            }
            else
            {
                result.Warnings.Add($"Module entry #{i + 1} is not an object: dropped.");
            }
        }

        if (remapCableColors)
        {
            RemapCableColors(cablesArray, targetOrigin);
        }

        // Detach everything from the original array up front so nodes are free to move around.
        modulesArray.Clear();

        List<JsonObject> converted = targetOrigin == PatchOrigin.Rack
            ? ConvertCardinalToRack(modules, cablesArray, result.Warnings)
            : ConvertRackToCardinal(modules, cablesArray, result.Warnings);

        foreach (JsonObject m in converted)
        {
            modulesArray.Add(m);
        }

        return result;
    }

    private static void RemapCableColors(JsonArray cables, PatchOrigin targetOrigin)
    {
        foreach (JsonObject cable in cables.OfType<JsonObject>())
        {
            if (PatchJson.String(cable["color"]) is not string color)
            {
                continue;
            }

            cable["color"] = targetOrigin == PatchOrigin.Rack
                ? CableColorMap.MapToRack(color)
                : CableColorMap.MapToCardinal(color);
        }
    }

    // ---------------------------------------------------------------- Cardinal -> Rack

    private static List<JsonObject> ConvertCardinalToRack(List<JsonObject> modules, JsonArray cables, List<string> warnings)
    {
        CompactHostMidiGateCells(modules, cables, warnings);

        List<JsonObject> output = new List<JsonObject>();
        Dictionary<long, (long? Leftmost, long? Rightmost)> replaced = new Dictionary<long, (long? Leftmost, long? Rightmost)>();
        Dictionary<JsonObject, int> newX = Reflow(modules, m => SplitWidthDelta(m, cables));

        foreach (JsonObject module in modules)
        {
            int x = newX[module];
            int y = PatchJson.Position(module).Y;

            string? plugin = Plugin(module);
            string? model = Model(module);

            if (plugin != ModuleMap.CardinalPlugin || model is null)
            {
                SetPos(module, x, y);
                output.Add(module);
                continue;
            }

            MidiSplitMapping? split = ModuleMap.FindByCardinalModel(model);
            if (split is not null)
            {
                (JsonObject? inModule, JsonObject? outModule) = SplitMidiModule(module, split, x, y, cables, warnings);
                long? inId = inModule is null ? null : RequiredId(inModule);
                long? outId = outModule is null ? null : RequiredId(outModule);
                replaced[RequiredId(module)] = (inId ?? outId, outId ?? inId);
                if (inModule is not null)
                {
                    CopyLink(module, "leftModuleId", inModule);
                    inModule["rightModuleId"] = outId;
                    output.Add(inModule);
                }
                if (outModule is not null)
                {
                    outModule["leftModuleId"] = inId;
                    CopyLink(module, "rightModuleId", outModule);
                    output.Add(outModule);
                }
                if (inModule is null && outModule is null)
                {
                    warnings.Add($"Cardinal module \"{model}\" (id {module["id"]}) has no cables connected: dropped instead of converted.");
                }
                continue;
            }

            if (model == ModuleMap.CardinalTextEditorModel)
            {
                SetPos(module, x, y);
                output.Add(RenameTextEditorToNotes(module));
                continue;
            }

            if (ModuleMap.CardinalToCoreSimple.TryGetValue(model, out string? coreModel))
            {
                SetPos(module, x, y);
                module["plugin"] = ModuleMap.CorePlugin;
                module["model"] = coreModel;
                output.Add(module);
                continue;
            }

            // No Rack equivalent: leave the module untouched (but still reflowed), warn.
            SetPos(module, x, y);
            warnings.Add(ModuleMap.CardinalNoEquivalent.Contains(model)
                ? $"Cardinal module \"{model}\" (id {module["id"]}) has no VCV Rack equivalent: copied as-is, will not load in Rack."
                : $"Cardinal module \"{model}\" (id {module["id"]}) is an unknown Cardinal module: copied as-is, will not load in Rack.");
            output.Add(module);
        }

        RelinkNeighbours(output, replaced);
        return output;
    }

    /// <summary>
    /// Splitting a duplex Cardinal MIDI module into two separate Rack modules almost always widens
    /// its footprint (e.g. HostMIDICC's 14 HP becomes 20 HP as MIDICCToCVInterface + CV-CC). Left
    /// alone, the extra width lands on top of whatever module used to sit right next to it, and
    /// Rack's own overlap resolution on load then shuffles unrelated modules around unpredictably.
    /// </summary>
    private static int SplitWidthDelta(JsonObject module, JsonArray cables)
    {
        if (Plugin(module) != ModuleMap.CardinalPlugin || Model(module) is not string model || ModuleMap.FindByCardinalModel(model) is not { } split)
        {
            return 0;
        }
        (bool usedIn, bool usedOut) = DetermineSplitUsage(RequiredId(module), split, cables);
        int newWidth = (usedIn ? split.RackInWidthHp : 0) + (usedOut ? split.RackOutWidthHp : 0);
        return newWidth - split.CardinalWidthHp;
    }

    /// <summary>
    /// Per row (same Y), how far every module must shift to keep the original touching/gap relationships
    /// intact once the modules that changed width (by <paramref name="widthDelta"/> HP) have grown or shrunk.
    /// </summary>
    private static Dictionary<JsonObject, int> Reflow(List<JsonObject> modules, Func<JsonObject, int> widthDelta)
    {
        Dictionary<JsonObject, int> newX = new Dictionary<JsonObject, int>();

        foreach (IGrouping<int, (JsonObject Module, (int X, int Y) Pos)> row in modules.Select(m => (Module: m, Pos: PatchJson.Position(m))).GroupBy(t => t.Pos.Y))
        {
            int shift = 0;
            foreach ((JsonObject module, (int x, int _)) in row.OrderBy(t => t.Pos.X))
            {
                newX[module] = x + shift;
                shift += widthDelta(module);
            }
        }

        return newX;
    }

    /// <summary>Whether HostMIDI/CC/Gate's MIDI-in half (outputs) and/or MIDI-out half (inputs) is actually cabled.</summary>
    private static (bool usedIn, bool usedOut) DetermineSplitUsage(long oldId, MidiSplitMapping split, JsonArray cables)
    {
        bool usedIn = false, usedOut = false;
        foreach (JsonObject cable in cables.OfType<JsonObject>())
        {
            if (OutputEnd(cable) is (long outModule, int outPort) && outModule == oldId && outPort < split.Outputs.Count)
            {
                usedIn = true;
            }
            if (InputEnd(cable) is (long inModule, int inPort) && inModule == oldId && inPort < split.Inputs.Count)
            {
                usedOut = true;
            }
        }
        return (usedIn, usedOut);
    }

    /// <summary>
    /// Cardinal's HostMIDIGate has 18 gate cells (id 0-17, shared between its MIDI-in and MIDI-out
    /// halves); VCV Rack's MIDITriggerToCVInterface/CV-Gate only have 16 each. Truncating anything at
    /// index >= 16 (as if cells were reserved 1:1 by index) silently drops whichever cells happen to
    /// sit at the high end, even when low-numbered cells are unused - and worse, on reload the kept
    /// cables no longer line up with the notes a patcher expects at that port. Instead, compact
    /// whichever cells are actually cabled down into Rack's 0..15 range, in ascending original-id
    /// order, carrying their notes and cable port indices along; only warn about cells that don't fit
    /// (i.e. this particular module has more than 16 cells in simultaneous use).
    /// </summary>
    private static void CompactHostMidiGateCells(List<JsonObject> modules, JsonArray cables, List<string> warnings)
    {
        foreach (JsonObject module in modules)
        {
            if (Plugin(module) != ModuleMap.CardinalPlugin || Model(module) != "HostMIDIGate")
            {
                continue;
            }

            long oldId = RequiredId(module);

            SortedSet<int> usedCells = new SortedSet<int>();
            foreach (JsonObject cable in cables.OfType<JsonObject>())
            {
                if (OutputEnd(cable) is (long outModule, int outPort) && outModule == oldId)
                {
                    usedCells.Add(outPort);
                }
                if (InputEnd(cable) is (long inModule, int inPort) && inModule == oldId)
                {
                    usedCells.Add(inPort);
                }
            }

            if (usedCells.Count == 0 || usedCells.Max < rackGateCells)
            {
                continue; // already fits Rack's range as-is, nothing to compact
            }

            List<int> orderedCells = usedCells.ToList(); // SortedSet enumerates ascending
            Dictionary<int, int> remap = new Dictionary<int, int>();
            for (int i = 0; i < orderedCells.Count && i < rackGateCells; i++)
            {
                remap[orderedCells[i]] = i;
            }

            foreach (int overflowCell in orderedCells.Skip(rackGateCells))
            {
                warnings.Add($"Cardinal module \"HostMIDIGate\" (id {oldId}) has more than {rackGateCells} gate cells in use; cell #{overflowCell + 1}'s cable(s) have no free VCV Rack slot and were dropped.");
            }

            foreach (JsonObject cable in cables.OfType<JsonObject>().ToList())
            {
                if (OutputEnd(cable) is (long outModule, int outPort) && outModule == oldId)
                {
                    if (remap.TryGetValue(outPort, out int newCell))
                    {
                        cable["outputId"] = newCell;
                    }
                    else
                    {
                        cables.Remove(cable);
                        continue;
                    }
                }
                if (InputEnd(cable) is (long inModule, int inPort) && inModule == oldId)
                {
                    if (remap.TryGetValue(inPort, out int newCell))
                    {
                        cable["inputId"] = newCell;
                    }
                    else
                    {
                        cables.Remove(cable);
                    }
                }
            }

            if (module["data"] is JsonObject data && data["notes"] is JsonArray oldNotes)
            {
                JsonArray newNotes = new JsonArray();
                for (int i = 0; i < rackGateCells; i++)
                {
                    newNotes.Add(-1);
                }
                foreach ((int oldCell, int newCell) in remap)
                {
                    newNotes[newCell] = oldCell < oldNotes.Count ? PatchJson.Int(oldNotes[oldCell]) ?? -1 : -1;
                }
                data["notes"] = newNotes;
            }
        }
    }

    private static (JsonObject? inModule, JsonObject? outModule) SplitMidiModule(
        JsonObject cardinalModule, MidiSplitMapping split, int x, int y, JsonArray cables, List<string> warnings)
    {
        long oldId = RequiredId(cardinalModule);
        (bool usedIn, bool usedOut) = DetermineSplitUsage(oldId, split, cables);

        long inId = usedIn ? NewModuleId() : 0;
        long outId = usedOut ? NewModuleId() : 0;

        JsonObject? inModule = usedIn ? NewModuleObject(ModuleMap.CorePlugin, split.RackInModel, inId, x, y) : null;
        JsonObject? outModule = usedOut
            ? NewModuleObject(ModuleMap.CorePlugin, split.RackOutModel, outId, x + (usedIn ? split.RackInWidthHp : 0), y)
            : null;

        foreach (JsonObject cable in cables.OfType<JsonObject>().ToList())
        {
            if (OutputEnd(cable) is (long outputModuleId, int outputPort) && outputModuleId == oldId)
            {
                if (outputPort < split.Outputs.Count)
                {
                    cable["outputModuleId"] = inId;
                }
                else
                {
                    string label = ExtraPortLabel(split.UnmappedExtraOutputs, outputPort, split.Outputs.Count);
                    warnings.Add($"Cable removed: output \"{label}\" of Cardinal module \"{split.CardinalModel}\" (id {oldId}) has no VCV Rack equivalent.");
                    cables.Remove(cable);
                    continue; // one warning per cable, even when its other end is on a dropped port too
                }
            }

            if (InputEnd(cable) is (long inputModuleId, int inputPort) && inputModuleId == oldId)
            {
                if (inputPort < split.Inputs.Count)
                {
                    cable["inputModuleId"] = outId;
                }
                else
                {
                    string label = ExtraPortLabel(split.UnmappedExtraInputs, inputPort, split.Inputs.Count);
                    warnings.Add($"Cable removed: input \"{label}\" of Cardinal module \"{split.CardinalModel}\" (id {oldId}) has no VCV Rack equivalent.");
                    cables.Remove(cable);
                }
            }
        }

        ApplyCardinalMidiData(cardinalModule, split, inModule, outModule);

        return (inModule, outModule);
    }

    /// <summary>
    /// Carries over the settings a Cardinal Host MIDI/CC/Gate module keeps in its "data" block
    /// (MIDI channel, learned CC numbers, learned gate notes, pitch wheel range...) onto the Rack module(s) replacing it.
    /// Field names are identical on both sides; only the MIDI channel needs converting (Cardinal input channel
    /// is 1-based with 0 = "All", Rack's midi::Port channel is 0-based with -1 = "All"; output channel is 0-based
    /// on both sides), and Cardinal's 18 gate cells are cut down to Rack's 16.
    /// </summary>
    private static void ApplyCardinalMidiData(JsonObject cardinalModule, MidiSplitMapping split, JsonObject? inModule, JsonObject? outModule)
    {
        JsonObject? data = cardinalModule["data"] as JsonObject;
        int inputChannel = PatchJson.Int(data?["inputChannel"]) ?? 0;
        int outputChannel = PatchJson.Int(data?["outputChannel"]) ?? 0;
        int rackInChannel = inputChannel <= 0 ? -1 : inputChannel - 1;

        JsonObject inData = new JsonObject();
        JsonObject outData = new JsonObject();
        switch (split.CardinalModel)
        {
            case "HostMIDI":
                CopyDouble(data, "pwRange", inData);
                CopyBool(data, "smooth", inData);
                CopyInt(data, "channels", inData);
                CopyInt(data, "polyMode", inData);
                CopyInt(data, "lastPitch", inData);
                CopyInt(data, "lastMod", inData);
                break;

            case "HostMIDICC":
                CopyIntArray(data, "ccs", inData, learnedCcCells);
                CopyIntArray(data, "values", inData);
                CopyBool(data, "smooth", inData);
                CopyBool(data, "mpeMode", inData);
                CopyBool(data, "lsbMode", inData);
                CopyIntArray(data, "ccs", outData, learnedCcCells);
                break;

            case "HostMIDIGate":
                CopyIntArray(data, "notes", inData, rackGateCells);
                CopyBool(data, "velocity", inData);
                CopyBool(data, "mpeMode", inData);
                CopyIntArray(data, "notes", outData, rackGateCells);
                CopyBool(data, "velocity", outData);
                break;
        }

        if (inModule is not null)
        {
            inData["midi"] = new JsonObject { ["channel"] = rackInChannel };
            inModule["data"] = inData;
        }
        if (outModule is not null)
        {
            outData["midi"] = new JsonObject { ["channel"] = outputChannel };
            outModule["data"] = outData;
        }
    }

    private static JsonObject RenameTextEditorToNotes(JsonObject module)
    {
        string text = PatchJson.String((module["data"] as JsonObject)?["etext"]) ?? "";
        (int x, int y) = PatchJson.Position(module);

        JsonObject notes = NewModuleObject(ModuleMap.CorePlugin, ModuleMap.CoreNotesModel, RequiredId(module), x, y);
        notes["data"] = new JsonObject { ["text"] = text };
        CopyLink(module, "leftModuleId", notes);
        CopyLink(module, "rightModuleId", notes);
        return notes;
    }

    // ---------------------------------------------------------------- Rack -> Cardinal

    private static List<JsonObject> ConvertRackToCardinal(List<JsonObject> modules, JsonArray cables, List<string> warnings)
    {
        HashSet<JsonObject> consumed = new HashSet<JsonObject>();
        List<JsonObject> output = new List<JsonObject>();
        Dictionary<long, (long? Leftmost, long? Rightmost)> replaced = new Dictionary<long, (long? Leftmost, long? Rightmost)>();
        Dictionary<JsonObject, int> widthDelta = new Dictionary<JsonObject, int>();

        foreach (MidiSplitMapping mapping in ModuleMap.MidiSplits)
        {
            List<JsonObject> inCandidates = modules.Where(m => !consumed.Contains(m) && IsModel(m, ModuleMap.CorePlugin, mapping.RackInModel)).ToList();

            foreach (JsonObject inModule in inCandidates)
            {
                JsonObject? outModule = FindAdjacent(inModule, modules, consumed, ModuleMap.CorePlugin, mapping.RackOutModel);
                consumed.Add(inModule);
                if (outModule is not null)
                {
                    consumed.Add(outModule);
                }
                else
                {
                    warnings.Add($"Rack module \"{mapping.RackInModel}\" (id {inModule["id"]}) converted alone into \"{mapping.CardinalModel}\": no adjacent \"{mapping.RackOutModel}\" found, the merged module's MIDI-out inputs will remain unconnected.");
                }

                JsonObject merged = MergeMidiModules(mapping, inModule, outModule, cables, replaced, warnings);
                widthDelta[merged] = mapping.CardinalWidthHp - mapping.RackInWidthHp - (outModule is null ? 0 : mapping.RackOutWidthHp);
                output.Add(merged);
            }

            // Any leftover "out" modules (CV-MIDI / CV-CC / CV-Gate) without a matching "in" pair.
            List<JsonObject> outLeftovers = modules.Where(m => !consumed.Contains(m) && IsModel(m, ModuleMap.CorePlugin, mapping.RackOutModel)).ToList();
            foreach (JsonObject outModule in outLeftovers)
            {
                consumed.Add(outModule);
                warnings.Add($"Rack module \"{mapping.RackOutModel}\" (id {outModule["id"]}) converted alone into \"{mapping.CardinalModel}\": no adjacent \"{mapping.RackInModel}\" found, the merged module's MIDI-in outputs will remain unconnected.");

                JsonObject merged = MergeMidiModules(mapping, null, outModule, cables, replaced, warnings);
                widthDelta[merged] = mapping.CardinalWidthHp - mapping.RackOutWidthHp;
                output.Add(merged);
            }
        }

        foreach (JsonObject module in modules)
        {
            if (consumed.Contains(module))
            {
                continue;
            }

            string? plugin = Plugin(module);
            string? model = Model(module);

            if (plugin != ModuleMap.CorePlugin || model is null)
            {
                output.Add(module);
                continue;
            }

            if (model == ModuleMap.CoreNotesModel)
            {
                output.Add(RenameNotesToTextEditor(module));
                continue;
            }

            if (ModuleMap.CoreToCardinalSimple.TryGetValue(model, out string? cardinalModel))
            {
                module["plugin"] = ModuleMap.CardinalPlugin;
                module["model"] = cardinalModel;
                output.Add(module);
                continue;
            }

            if (ModuleMap.CoreNoEquivalent.Contains(model))
            {
                warnings.Add($"VCV Rack module \"{model}\" (id {module["id"]}) has no Cardinal equivalent: copied as-is, will not load in Cardinal.");
            }

            output.Add(module);
        }

        // Cardinal's MIDI modules are not as wide as the Rack modules they replace (HostMIDICC is 14 HP where
        // CV-CC alone is 10, and MIDICCToCVInterface + CV-CC 20): same reflow as when splitting, the other way round.
        Dictionary<JsonObject, int> newX = Reflow(output, m => widthDelta.GetValueOrDefault(m));
        foreach (JsonObject module in output)
        {
            SetPos(module, newX[module], PatchJson.Position(module).Y);
        }

        RelinkNeighbours(output, replaced);
        return output;
    }

    /// <summary>
    /// One Cardinal module in place of a Rack MIDI-in module and/or its MIDI-out neighbour: placed where the
    /// leftmost of the two was, taking over their cables, neighbour links and settings.
    /// </summary>
    private static JsonObject MergeMidiModules(
        MidiSplitMapping mapping, JsonObject? inModule, JsonObject? outModule, JsonArray cables,
        Dictionary<long, (long? Leftmost, long? Rightmost)> replaced, List<string> warnings)
    {
        JsonObject[] halves = new[] { inModule, outModule }.OfType<JsonObject>().OrderBy(m => PatchJson.Position(m).X).ToArray();
        (int x, int y) = PatchJson.Position(halves[0]);
        long newId = NewModuleId();
        JsonObject merged = NewModuleObject(ModuleMap.CardinalPlugin, mapping.CardinalModel, newId, x, y);
        CopyLink(halves[0], "leftModuleId", merged);
        CopyLink(halves[^1], "rightModuleId", merged);

        long? inId = inModule is null ? null : RequiredId(inModule);
        long? outId = outModule is null ? null : RequiredId(outModule);
        foreach (JsonObject cable in cables.OfType<JsonObject>())
        {
            if (inId is not null && PatchJson.Long(cable["outputModuleId"]) == inId)
            {
                cable["outputModuleId"] = newId;
            }
            if (outId is not null && PatchJson.Long(cable["inputModuleId"]) == outId)
            {
                cable["inputModuleId"] = newId;
            }
        }
        foreach (long oldId in new[] { inId, outId }.OfType<long>())
        {
            replaced[oldId] = (newId, newId);
        }

        ApplyRackMidiData(mapping, inModule, outModule, merged, warnings);
        return merged;
    }

    /// <summary>
    /// The inverse of <see cref="ApplyCardinalMidiData"/>: Rack keeps the MIDI channel in a "midi" port object
    /// (0-based, -1 = "All" on input), Cardinal in "inputChannel" (1-based, 0 = "All") and "outputChannel" (0-based).
    /// Learned CCs and gate notes exist on both Rack halves: the MIDI-in ones win, with a warning if they differ.
    /// Cardinal's gate keeps 18 notes, the two Rack lacks left unassigned.
    /// </summary>
    private static void ApplyRackMidiData(MidiSplitMapping mapping, JsonObject? inModule, JsonObject? outModule, JsonObject merged, List<string> warnings)
    {
        JsonObject? inData = inModule?["data"] as JsonObject;
        JsonObject? outData = outModule?["data"] as JsonObject;
        JsonObject data = new JsonObject();

        if (PatchJson.Int(MidiChannel(inData)) is int inChannel)
        {
            data["inputChannel"] = inChannel < 0 ? 0 : inChannel + 1;
        }
        if (PatchJson.Int(MidiChannel(outData)) is int outChannel)
        {
            data["outputChannel"] = outChannel;
        }

        switch (mapping.CardinalModel)
        {
            case "HostMIDI":
                CopyDouble(inData, "pwRange", data);
                CopyBool(inData, "smooth", data);
                CopyInt(inData, "channels", data);
                CopyInt(inData, "polyMode", data);
                CopyInt(inData, "lastPitch", data);
                CopyInt(inData, "lastMod", data);
                break;

            case "HostMIDICC":
                CopyLearned(mapping, inModule, outModule, "ccs", "CC numbers", data, learnedCcCells, 0, warnings);
                CopyIntArray(inData, "values", data);
                CopyBool(inData, "smooth", data);
                CopyBool(inData, "mpeMode", data);
                CopyBool(inData, "lsbMode", data);
                break;

            case "HostMIDIGate":
                CopyLearned(mapping, inModule, outModule, "notes", "gate notes", data, cardinalGateCells, cardinalGateCells, warnings);
                CopyBool(inData?["velocity"] is not null ? inData : outData, "velocity", data);
                CopyBool(inData, "mpeMode", data);
                break;
        }

        if (data.Count > 0)
        {
            merged["data"] = data;
        }
    }

    private static JsonNode? MidiChannel(JsonObject? data) => (data?["midi"] as JsonObject)?["channel"];

    private static void CopyLearned(
        MidiSplitMapping mapping, JsonObject? inModule, JsonObject? outModule, string key, string label, JsonObject target, int maxCells, int minCells, List<string> warnings)
    {
        List<int>? fromIn = Integers((inModule?["data"] as JsonObject)?[key]);
        List<int>? fromOut = Integers((outModule?["data"] as JsonObject)?[key]);
        if (fromIn is not null && fromOut is not null && !fromIn.SequenceEqual(fromOut))
        {
            warnings.Add($"Rack modules \"{mapping.RackInModel}\" (id {inModule!["id"]}) and \"{mapping.RackOutModel}\" (id {outModule!["id"]}) disagree on their learned {label}: kept those of \"{mapping.RackInModel}\" for \"{mapping.CardinalModel}\".");
        }
        if ((fromIn ?? fromOut) is List<int> values)
        {
            JsonArray array = new JsonArray(values.Take(maxCells).Select(v => (JsonNode?)v).ToArray());
            while (array.Count < minCells)
            {
                array.Add(-1);
            }
            target[key] = array;
        }
    }

    private static JsonObject? FindAdjacent(JsonObject module, List<JsonObject> allModules, HashSet<JsonObject> consumed, string plugin, string model)
    {
        long? left = PatchJson.Long(module["leftModuleId"]);
        long? right = PatchJson.Long(module["rightModuleId"]);
        long moduleId = RequiredId(module);

        foreach (JsonObject candidate in allModules)
        {
            if (consumed.Contains(candidate) || !IsModel(candidate, plugin, model))
            {
                continue;
            }

            long candidateId = RequiredId(candidate);
            if (candidateId == left || candidateId == right)
            {
                return candidate;
            }

            // Also match the reverse link, in case only the neighbour records the adjacency.
            if (PatchJson.Long(candidate["leftModuleId"]) == moduleId || PatchJson.Long(candidate["rightModuleId"]) == moduleId)
            {
                return candidate;
            }
        }

        return null;
    }

    private static JsonObject RenameNotesToTextEditor(JsonObject module)
    {
        string text = PatchJson.String((module["data"] as JsonObject)?["text"]) ?? "";
        (int x, int y) = PatchJson.Position(module);

        JsonObject editor = NewModuleObject(ModuleMap.CardinalPlugin, ModuleMap.CardinalTextEditorModel, RequiredId(module), x, y);
        editor["data"] = new JsonObject
        {
            ["filepath"] = "",
            ["lang"] = "None",
            ["etext"] = text,
            ["width"] = 23,
        };
        CopyLink(module, "leftModuleId", editor);
        CopyLink(module, "rightModuleId", editor);
        return editor;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Points the left/right neighbour links that named a replaced module at what replaced it (the rightmost
    /// piece for a left neighbour, the leftmost for a right one), and drops any link left dangling.
    /// </summary>
    private static void RelinkNeighbours(List<JsonObject> modules, Dictionary<long, (long? Leftmost, long? Rightmost)> replaced)
    {
        HashSet<long> present = modules.Select(m => PatchJson.Long(m["id"])).OfType<long>().ToHashSet();
        foreach (JsonObject module in modules)
        {
            long? self = PatchJson.Long(module["id"]);
            Relink(module, "leftModuleId", self, r => r.Rightmost);
            Relink(module, "rightModuleId", self, r => r.Leftmost);
        }

        void Relink(JsonObject module, string key, long? self, Func<(long? Leftmost, long? Rightmost), long?> pick)
        {
            if (!module.ContainsKey(key))
            {
                return;
            }
            long? target = PatchJson.Long(module[key]);
            if (target is long id && replaced.TryGetValue(id, out (long? Leftmost, long? Rightmost) replacement))
            {
                target = pick(replacement);
            }
            if (target is long linked && present.Contains(linked) && linked != self)
            {
                module[key] = linked;
            }
            else
            {
                module.Remove(key);
            }
        }
    }

    private static void CopyLink(JsonObject source, string key, JsonObject target)
    {
        if (PatchJson.Long(source[key]) is long id)
        {
            target[key] = id;
        }
    }

    private static void CopyBool(JsonObject? source, string key, JsonObject target)
    {
        if (PatchJson.Bool(source?[key]) is bool value)
        {
            target[key] = value;
        }
    }

    private static void CopyInt(JsonObject? source, string key, JsonObject target)
    {
        if (PatchJson.Int(source?[key]) is int value)
        {
            target[key] = value;
        }
    }

    private static void CopyDouble(JsonObject? source, string key, JsonObject target)
    {
        if (PatchJson.Double(source?[key]) is double value)
        {
            target[key] = value;
        }
    }

    /// <summary>An array of integers, unreadable entries as -1 ("unassigned"), cut to <paramref name="maxCount"/> when given.</summary>
    private static void CopyIntArray(JsonObject? source, string key, JsonObject target, int maxCount = int.MaxValue)
    {
        if (Integers(source?[key]) is List<int> values)
        {
            target[key] = new JsonArray(values.Take(maxCount).Select(v => (JsonNode?)v).ToArray());
        }
    }

    private static List<int>? Integers(JsonNode? node) => node is JsonArray array ? array.Select(n => PatchJson.Int(n) ?? -1).ToList() : null;

    private static string? Plugin(JsonObject module) => PatchJson.String(module["plugin"]);

    private static string? Model(JsonObject module) => PatchJson.String(module["model"]);

    private static long RequiredId(JsonObject module) =>
        PatchJson.Long(module["id"]) ?? throw new InvalidDataException($"Module {PatchJson.Describe(module)} has no valid \"id\".");

    private static (long ModuleId, int PortId)? OutputEnd(JsonObject cable) =>
        PatchJson.Long(cable["outputModuleId"]) is long module && PatchJson.Int(cable["outputId"]) is int port ? (module, port) : null;

    private static (long ModuleId, int PortId)? InputEnd(JsonObject cable) =>
        PatchJson.Long(cable["inputModuleId"]) is long module && PatchJson.Int(cable["inputId"]) is int port ? (module, port) : null;

    private static bool IsModel(JsonObject module, string plugin, string model) =>
        Plugin(module) == plugin && Model(module) == model;

    /// <summary>Whole HP and rows, as Rack writes them, so the result reads back like a saved patch.</summary>
    private static void SetPos(JsonObject module, int x, int y) =>
        module["pos"] = new JsonArray { x, y };

    private static JsonObject NewModuleObject(string plugin, string model, long id, int x, int y) => new()
    {
        ["id"] = id,
        ["plugin"] = plugin,
        ["model"] = model,
        ["version"] = "2.0",
        ["params"] = new JsonArray(),
        ["pos"] = new JsonArray { x, y },
    };

    private static string ExtraPortLabel(string[]? labels, int index, int baseCount)
    {
        int extraIndex = index - baseCount;
        if (labels is not null && extraIndex >= 0 && extraIndex < labels.Length)
        {
            return labels[extraIndex];
        }
        return $"port #{index}";
    }

    private static long NewModuleId()
    {
        // Rack module ids are just unique 53-bit-ish integers; a random positive long is fine.
        Span<byte> buf = stackalloc byte[8];
        idRandom.NextBytes(buf);
        long value = BitConverter.ToInt64(buf) & 0x1F_FFFF_FFFF_FFFF; // keep it positive and JS-safe-ish
        return value == 0 ? 1 : value;
    }
}