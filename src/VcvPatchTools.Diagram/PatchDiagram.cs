using System.Text.Json;
using System.Text.Json.Serialization;
using VcvPatchTools.Core.Patch;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;
using VcvPatchTools.Diagram.Render;

namespace VcvPatchTools.Diagram;

public enum DiagramFormat
{
    Html,
    Svg,
    Dot,
    Mermaid,
    Json,
}

/// <summary>Entry point shared by the CLI and the Blazor app: patch bytes in, diagram out.</summary>
public static class PatchDiagram
{
    private static readonly Lazy<PatchAnalyzer> analyzer = new Lazy<PatchAnalyzer>(PatchAnalyzer.CreateDefault);

    public static PatchAnalysis Analyze(byte[] patchBytes) => analyzer.Value.Analyze(PatchReader.Read(patchBytes));

    /// <summary>A patch already read (decompressed once, e.g. to also show its files).</summary>
    public static PatchAnalysis Analyze(PatchArchive archive) => analyzer.Value.Analyze(PatchReader.Parse(archive.Root));

    /// <param name="unfolded">Groups to show in detail (see <see cref="GroupKeys"/>); none by default: the overview.</param>
    /// <param name="monitors">How to show scopes and displays (hidden by default).</param>
    /// <param name="functionNames">Title boxes by function ("FILTER #1") rather than by module name.</param>
    /// <param name="portTabs">Name the ports on the box edges, where cables plug in (on by default).</param>
    /// <param name="libraryLinks">Put a link to its VCV Library page on each module box (on by default).</param>
    public static DiagramLayout Layout(PatchAnalysis analysis, string title, IReadOnlySet<string>? unfolded = null, IReadOnlyDictionary<long, string>? intents = null, MonitorView monitors = MonitorView.Hidden, bool functionNames = false, bool portTabs = true, bool libraryLinks = true) =>
        LayeredLayout.Build(analysis, title, unfolded, intents, monitors, functionNames, portTabs, libraryLinks);

    /// <summary>Keys of every foldable group, e.g. to unfold everything.</summary>
    public static IReadOnlySet<string> GroupKeys(PatchAnalysis analysis) => PatchGrouping.Build(analysis).Select(g => g.Key).ToHashSet();

    public static string Export(DiagramLayout layout, DiagramFormat format) => format switch
    {
        DiagramFormat.Html => HtmlRenderer.Render(layout),
        DiagramFormat.Svg => SvgRenderer.Render(layout, standalone: true),
        DiagramFormat.Dot => DotExporter.Export(layout),
        DiagramFormat.Mermaid => MermaidExporter.Export(layout),
        _ => JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }),
    };

    public static string Extension(DiagramFormat format) => format switch
    {
        DiagramFormat.Html => ".html",
        DiagramFormat.Svg => ".svg",
        DiagramFormat.Dot => ".dot",
        DiagramFormat.Mermaid => ".mmd",
        _ => ".json",
    };

    public static string MimeType(DiagramFormat format) => format switch
    {
        DiagramFormat.Html => "text/html",
        DiagramFormat.Svg => "image/svg+xml",
        DiagramFormat.Json => "application/json",
        _ => "text/plain",
    };

    public static bool TryParseFormat(string text, out DiagramFormat format)
    {
        string normalized = text.TrimStart('.').ToLowerInvariant();
        format = normalized switch
        {
            "html" => DiagramFormat.Html,
            "svg" => DiagramFormat.Svg,
            "dot" or "gv" => DiagramFormat.Dot,
            "mmd" or "mermaid" => DiagramFormat.Mermaid,
            "json" => DiagramFormat.Json,
            _ => (DiagramFormat)(-1),
        };
        return Enum.IsDefined(format);
    }
}