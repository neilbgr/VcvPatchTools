namespace VcvPatchTools.Bridge;

public enum PatchOrigin
{
    Cardinal,
    Rack,
    Ambiguous
}

/// <summary>Identifies a module by its plugin+model slug, e.g. ("Core", "AudioInterface2").</summary>
public readonly record struct ModuleSlug(string Plugin, string Model);

/// <summary>
/// A pair of matching Rack ports (used to remap cables when splitting/merging a module).
/// Index i on the Cardinal side maps to index i on the Rack side.
/// </summary>
public sealed record PortRange(int Count);

/// <summary>
/// Describes how one Cardinal MIDI module (combining MIDI-in and MIDI-out in one instance)
/// maps to the two separate Rack Core modules it replaces.
/// </summary>
public sealed record MidiSplitMapping(
    string CardinalModel,
    string RackInModel,   // Core module receiving MIDI -> outputs CV (e.g. MIDIToCVInterface)
    string RackOutModel,  // Core module sending MIDI <- inputs CV (e.g. CV-MIDI)
    int CardinalWidthHp,  // panel width of CardinalModel, in HP
    int RackInWidthHp,    // panel width of RackInModel, in HP
    int RackOutWidthHp,   // panel width of RackOutModel, in HP
    PortRange Outputs,    // Cardinal outputs <-> RackIn outputs
    PortRange Inputs,     // Cardinal inputs  <-> RackOut inputs
    string[]? UnmappedExtraOutputs = null, // Cardinal-only output port names with no Rack target
    string[]? UnmappedExtraInputs = null   // Cardinal-only input port names with no Rack target
);

public static class ModuleMap
{
    public const string CardinalPlugin = "Cardinal";
    public const string CorePlugin = "Core";

    /// <summary>Simple 1:1 renames, same port layout on both sides (order-preserving).</summary>
    public static readonly Dictionary<string, string> CoreToCardinalSimple = new()
    {
        ["AudioInterface2"] = "HostAudio2",
        ["AudioInterface"] = "HostAudio8",
        ["MIDI-Map"] = "HostMIDIMap",
        ["Blank"] = "Blank",
    };

    public static readonly Dictionary<string, string> CardinalToCoreSimple =
        CoreToCardinalSimple.ToDictionary(kv => kv.Value, kv => kv.Key);

    // Notes <-> TextEditor is handled specially (different data schema: "text" vs "etext").
    public const string CoreNotesModel = "Notes";
    public const string CardinalTextEditorModel = "TextEditor";

    /// <summary>Core module with no Cardinal equivalent at all.</summary>
    public static readonly HashSet<string> CoreNoEquivalent = new()
    {
        "AudioInterface16",
    };

    /// <summary>Cardinal modules with no Rack equivalent at all (any other Cardinal model is one this tool does not know).</summary>
    public static readonly HashSet<string> CardinalNoEquivalent = new()
    {
        "HostCV",
        "HostParameters",
        "HostParametersMap",
        "HostTime",
        "CardinalBlank",
        "AudioToCVPitch",
        "Carla",
        "Ildaeil",
        "AidaX",
        "AudioFile",
        "GlBars",
        "SassyScope",
        "MPV",
        "ExpanderInputMIDI",
        "ExpanderOutputMIDI",
    };

    /// <summary>All Core-plugin model slugs used for detection (superset of every table above).</summary>
    public static readonly HashSet<string> AllCoreModels = new(
        CoreToCardinalSimple.Keys
            .Concat(new[] { CoreNotesModel })
            .Concat(CoreNoEquivalent)
            .Concat(new[]
            {
                "MIDIToCVInterface", "CV-MIDI",
                "MIDICCToCVInterface", "CV-CC",
                "MIDITriggerToCVInterface", "CV-Gate",
            }));

    public static readonly MidiSplitMapping[] MidiSplits =
    {
        new MidiSplitMapping(
            CardinalModel: "HostMIDI",
            RackInModel: "MIDIToCVInterface",
            RackOutModel: "CV-MIDI",
            CardinalWidthHp: 9,  // HostMIDI.svg: 45.72mm / 5.08mm per HP
            RackInWidthHp: 8,    // MIDI_CV.svg: 120px / 15px per HP
            RackOutWidthHp: 8,   // CV_MIDI.svg: 120px / 15px per HP
            Outputs: new PortRange(12), // Pitch, Gate, Velocity, Aftertouch, Pitchbend, ModWheel, Retrigger, Clock, ClockDiv, Start, Stop, Continue
            Inputs: new PortRange(12)   // Pitch, Gate, Velocity, Aftertouch, Pitchbend, ModWheel, Clock, Volume, Pan, Start, Stop, Continue
        ),
        new MidiSplitMapping(
            CardinalModel: "HostMIDICC",
            RackInModel: "MIDICCToCVInterface",
            RackOutModel: "CV-CC",
            CardinalWidthHp: 14, // HostMIDICC.svg: 71.12mm / 5.08mm per HP
            RackInWidthHp: 10,   // MIDICC_CV.svg: 150px / 15px per HP
            RackOutWidthHp: 10,  // CV_MIDICC.svg: 150px / 15px per HP
            Outputs: new PortRange(16),
            Inputs: new PortRange(16),
            UnmappedExtraOutputs: new[] { "Channel pressure", "Pitchbend" }, // indices 16,17 on Cardinal side only
            UnmappedExtraInputs: new[] { "Channel pressure", "Pitchbend" }
        ),
        new MidiSplitMapping(
            CardinalModel: "HostMIDIGate",
            RackInModel: "MIDITriggerToCVInterface",
            RackOutModel: "CV-Gate",
            CardinalWidthHp: 14, // HostMIDIGate.svg: 71.12mm / 5.08mm per HP
            RackInWidthHp: 10,   // MIDI_Gate.svg: 150px / 15px per HP
            RackOutWidthHp: 10,  // Gate_MIDI.svg: 150px / 15px per HP
            Outputs: new PortRange(16),
            Inputs: new PortRange(16)
        ),
    };

    public static MidiSplitMapping? FindByCardinalModel(string model) =>
        MidiSplits.FirstOrDefault(m => m.CardinalModel == model);
}