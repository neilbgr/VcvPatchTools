namespace VcvPatchTools.Core.Patch;

/// <summary>One module instance placed in the rack. Column is in HP, Row is the rack row.</summary>
public sealed record PatchModule(long Id, string Plugin, string Model, int Column, int Row)
{
    public string CatalogKey => $"{Plugin}/{Model}";

    /// <summary>Knob/switch values by param id (only needed for modulation matrices, so empty by default).</summary>
    public IReadOnlyDictionary<int, double> Params { get; init; } = new Dictionary<int, double>();

    /// <summary>Modules docked on each side, as saved by Rack 2: what lets an expander talk to its base.</summary>
    public long? LeftModuleId { get; init; }

    public long? RightModuleId { get; init; }

    /// <summary>MIDI note (gate modules) or controller number (CC modules) learned by each cell, -1 when unassigned.</summary>
    public IReadOnlyList<int> LearnedNotes { get; init; } = Array.Empty<int>();

    public IReadOnlyList<int> LearnedCcs { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Labels typed on the panel, "" when left blank: one per fader (data.faders) or button (data.buttons), or the
    /// module's single data.label (Aluminium's PadX).
    /// </summary>
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
}

/// <summary>A port on a module: Output ports are cable sources, Input ports are cable destinations.</summary>
public sealed record PortRef(long ModuleId, int PortId);

/// <summary>A cable from an output port to an input port. Color is the raw "#rrggbb" string, if any.</summary>
public sealed record PatchCable(long Id, PortRef From, PortRef To, string? Color);

public sealed record PatchDocument(string? RackVersion, IReadOnlyList<PatchModule> Modules, IReadOnlyList<PatchCable> Cables)
{
    public PatchModule Module(long id) => Modules.First(m => m.Id == id);
}