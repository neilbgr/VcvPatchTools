using System.Runtime.CompilerServices;
using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Diagram.Analysis;

/// <summary>Horizontal bands of the diagram, top to bottom: the order a patch is explained in, reversed.</summary>
public enum Band
{
    /// <summary>Performance: host MIDI/CV/audio inputs and controls played live (pads, joystick): what the musician (or the DAW) sends into the patch, and what relays it (keyboard zones…).</summary>
    External,
    Time,
    Pitch,
    Modulation,
    Voice,
    Bus,
    /// <summary>Scopes and displays: last, out of the signal flow.</summary>
    Monitor,
}

/// <param name="InsertOf">Module this one is patched into as an insert or send/return effect (drawn under it).</param>
public sealed record AnalyzedModule(
    PatchModule Module,
    string Title,
    string Plugin,
    Role Role,
    Band Band,
    int? Voice,
    long? InsertOf)
{
    /// <summary>Docked expanders folded into this module (see <see cref="ExpanderChains"/>), by name.</summary>
    public IReadOnlyList<string> Expanders { get; init; } = Array.Empty<string>();

    /// <summary>What it does within its role: filter, VCA, envelope, LFO…</summary>
    public ModuleFunction Function { get; init; }
}

/// <param name="Layer">Which teaching layer shows this cable: audio, pitch, modulation or time.</param>
public sealed record AnalyzedCable(
    PatchCable Cable,
    string FromPort,
    string ToPort,
    SignalType Signal,
    string SuggestedIntent)
{
    /// <summary>For a programmable modulation input: the knobs it moves and how much.</summary>
    public IReadOnlyList<ModRoute> Routes { get; init; } = Array.Empty<ModRoute>();
}

/// <summary>One cell of a modulation matrix: the knob moved and the attenuverter depth (-1..1).</summary>
public sealed record ModRoute(string Target, double Depth)
{
    public override string ToString() => $"{Target} {Depth * 100:+0;-0}%";
}

/// <param name="PitchOrigin">Title of the module the voice's notes ultimately come from (sequencer, MIDI…), if any.</param>
public sealed record Voice(int Index, IReadOnlyList<long> ModuleIds, string Sources, string? PitchOrigin)
{
    public string Name => PitchOrigin is null ? $"Voice {Index + 1}: {Sources}" : $"Voice {Index + 1}: {Sources} ← {PitchOrigin}";
}

public sealed record PatchAnalysis(
    PatchDocument Patch,
    IReadOnlyList<AnalyzedModule> Modules,
    IReadOnlyList<AnalyzedCable> Cables,
    IReadOnlyList<Voice> Voices,
    IReadOnlyList<string> Diagnostics)
{
    // Built once per module list, outside the record so equality and "with" copies are unaffected.
    private static readonly ConditionalWeakTable<IReadOnlyList<AnalyzedModule>, Dictionary<long, AnalyzedModule>> indexes = new ConditionalWeakTable<IReadOnlyList<AnalyzedModule>, Dictionary<long, AnalyzedModule>>();

    public AnalyzedModule Module(long id) =>
        indexes.GetValue(Modules, Index).TryGetValue(id, out AnalyzedModule? module) ? module : throw new KeyNotFoundException($"No module {id} in the analysis");

    private static Dictionary<long, AnalyzedModule> Index(IReadOnlyList<AnalyzedModule> modules)
    {
        Dictionary<long, AnalyzedModule> index = new Dictionary<long, AnalyzedModule>(modules.Count);
        foreach (AnalyzedModule module in modules)
        {
            index.TryAdd(module.Module.Id, module);
        }
        return index;
    }

    /// <summary>Feedback loops (cross-modulation, audio feedback…): modules that all feed each other, in rack order.</summary>
    public IReadOnlyList<IReadOnlyList<long>> Loops { get; init; } = Array.Empty<IReadOnlyList<long>>();
}