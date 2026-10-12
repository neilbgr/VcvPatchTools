using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Diagram.Analysis;

namespace VcvPatchTools.Diagram.Layout;

/// <summary>A box in the diagram: a module of an unfolded group, or a whole folded group.</summary>
/// <param name="Key">"m{moduleId}" for a module, "g:{groupKey}" for a folded group.</param>
/// <param name="Group">Key of the group this box belongs to (or is).</param>
/// <param name="Details">Tooltip lines: ports and cables for a module, member modules for a folded group.</param>
public sealed record DiagramNode(
    string Key,
    long? ModuleId,
    string Group,
    bool IsFolded,
    string Title,
    string Subtitle,
    Role Role,
    Band Band,
    bool IsInsert,
    IReadOnlyList<string> Details,
    double X,
    double Y,
    double Width,
    double Height)
{
    /// <summary>Scopes/displays watching a signal of this box, drawn as a badge instead of boxes and cables.</summary>
    public IReadOnlyList<string> Watchers { get; init; } = Array.Empty<string>();

    /// <summary>What the module does (drawn as an icon); null for a folded group.</summary>
    public ModuleFunction? Function { get; init; }

    /// <summary>"plugin/model" of a module listed in the VCV Library, to link its page; null for folded groups and Cardinal-only modules.</summary>
    public string? LibraryKey { get; init; }

    /// <summary>A folded group's modules listed in the VCV Library, for a preview of their panels (no link).</summary>
    public IReadOnlyList<LibraryPanel> LibraryPanels { get; init; } = Array.Empty<LibraryPanel>();

    /// <summary>A folded group's modules the Library has no page for (Cardinal-only), mentioned under its preview.</summary>
    public int UnlistedCount { get; init; }

    /// <summary>Port names on the box edges, where cables plug in.</summary>
    public IReadOnlyList<PortTab> Tabs { get; init; } = Array.Empty<PortTab>();

    /// <summary>Widest tab on each side (0 without tabs): cables start and end beyond them.</summary>
    public double InTabsWidth => Tabs.Where(t => !t.Output).Select(t => t.Width).DefaultIfEmpty(0).Max();

    public double OutTabsWidth => Tabs.Where(t => t.Output).Select(t => t.Width).DefaultIfEmpty(0).Max();
}

/// <summary>A port name on a box edge: Output on the right side, input on the left; Y is the cable's height.</summary>
/// <param name="Key">"plugin/model" (see <see cref="Core.Catalog.VcvLibrary"/>).</param>
/// <param name="Count">How many of this module the group holds.</param>
public sealed record LibraryPanel(string Key, int Count);

public sealed record PortTab(bool Output, double Y, double Width, string Text, SignalType Signal, string Tooltip);

/// <summary>A drawn cable, or several cables of the same signal type merged between two boxes when a group is folded.</summary>
public sealed record DiagramEdge(
    string Key,
    string From,
    string To,
    IReadOnlyList<long> CableIds,
    string FromTitle,
    string FromPort,
    string ToTitle,
    string ToPort,
    SignalType Signal,
    string Intent,
    string Path,
    double LabelX,
    double LabelY)
{
    private const int labelMaxChars = 36;

    /// <summary>The intent as drawn on the cable: long ones are shortened, the tooltip keeps them whole.</summary>
    public string Label => Intent.Length <= labelMaxChars ? Intent : Intent[..(labelMaxChars - 1)].TrimEnd() + "…";

    /// <summary>Teaching layer: audio first, then pitch, modulation, and gate/trig/clock last.</summary>
    public string Layer => Signal switch
    {
        SignalType.Audio => "audio",
        SignalType.Pitch => "pitch",
        SignalType.Gate => "gate",
        _ => "modulation",
    };

    /// <summary>One real cable: its intent can be edited.</summary>
    public bool IsSingleCable => CableIds.Count == 1;

    /// <summary>Goes back against the flow (cross-modulation, feedback loop): drawn turning back to its target.</summary>
    public bool IsFeedback { get; init; }

    /// <summary>Goes to a scope or a display: it only watches the signal (drawn dashed).</summary>
    public bool IsMonitor { get; init; }
}

/// <param name="Group">The unfolded group shown in this lane, or null for a lane of folded groups.</param>
public sealed record DiagramBand(Band Band, string? Group, string Title, double Y, double Height)
{
    /// <summary>Left edge; null for a horizontal band, across the whole diagram.</summary>
    public double? X { get; init; }

    /// <summary>Width of a band with an <see cref="X"/>; horizontal bands span the whole diagram.</summary>
    public double? Width { get; init; }

    /// <summary>The column of scopes and displays, on the right of everything else.</summary>
    public bool IsVertical => X is not null;
}

/// <summary>How scopes and displays show: they explain nothing about the sound and watch the flow anywhere.</summary>
public enum MonitorView
{
    /// <summary>Not at all.</summary>
    Hidden,

    /// <summary>As a badge on the box whose signal they watch.</summary>
    Badge,

    /// <summary>As boxes in a column on the right, each next to what it watches, with dashed cables.</summary>
    Modules,
}

/// <summary>Positions of everything in the diagram: the single source of truth for every renderer and exporter.</summary>
public sealed record DiagramLayout(
    string Title,
    double Width,
    double Height,
    IReadOnlyList<DiagramBand> Bands,
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    IReadOnlyList<PatchGroup> Groups,
    IReadOnlyList<string> Diagnostics)
{
    public static IReadOnlyList<string> Layers { get; } = new[] { "audio", "pitch", "modulation", "gate" };

    public static string LayerLabel(string layer) => layer == "gate" ? "gate / trig / clock" : layer;

    public DiagramNode Node(string key) => Nodes.First(n => n.Key == key);

    /// <summary>Boxes drawn in a band.</summary>
    public IEnumerable<DiagramNode> Members(DiagramBand band) =>
        // A band can hold several lanes of the same kind (one per voice): a horizontal band holds what's drawn inside it.
        Nodes.Where(n => band.IsVertical ? n.Band == band.Band : n.Y >= band.Y && n.Y < band.Y + band.Height);

    /// <summary>Left edge and width of a band as drawn: horizontal bands span the diagram, with a small margin.</summary>
    public (double X, double Width) Extent(DiagramBand band) => band.IsVertical ? (band.X!.Value, band.Width!.Value) : (8, HorizontalBandsWidth - 16);

    /// <summary>Width horizontal bands span: up to the column of scopes when there is one.</summary>
    public double HorizontalBandsWidth => Bands.FirstOrDefault(b => b.IsVertical) is DiagramBand column ? column.X!.Value : Width;
}