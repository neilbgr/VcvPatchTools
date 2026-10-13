using System.Text;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;

namespace VcvPatchTools.Diagram.Render;

/// <summary>Mermaid flowchart export (renders in GitHub READMEs, Obsidian…): one subgraph per band.</summary>
public static class MermaidExporter
{
    public static string Export(DiagramLayout layout)
    {
        StringBuilder mmd = new StringBuilder();
        mmd.AppendLine("flowchart LR");

        int bandIndex = 0;
        foreach (DiagramBand band in layout.Bands)
        {
            mmd.AppendLine($"  subgraph b{bandIndex++}[\"{Label(band.Title)}\"]");
            foreach (DiagramNode node in layout.Members(band))
            {
                mmd.AppendLine($"    {Id(node.Key)}[\"{Label(node.Title)}{(node.Watchers.Count > 0 ? " 👁" : "")}<br/>{Label(node.Subtitle)}\"]:::{node.Role.ToString().ToLowerInvariant()}");
            }
            mmd.AppendLine("  end");
        }

        List<string> linkStyles = new List<string>();
        for (int i = 0; i < layout.Edges.Count; i++)
        {
            DiagramEdge edge = layout.Edges[i];
            // Arrow shape carries the signal type even without colors: ==> audio, --> pitch/modulation, -.-> time.
            string arrow = edge.Signal switch
            {
                SignalType.Audio => "==>",
                SignalType.Gate => "-.->",
                _ => "-->",
            };
            string label = edge.Intent.Length > 0 ? $"|\"{Label(edge.Intent)}\"|" : "";
            mmd.AppendLine($"  {Id(edge.From)} {arrow}{label} {Id(edge.To)}");
            string color = edge.Signal switch
            {
                SignalType.Audio => "#d64545",
                SignalType.Pitch => "#b97d00",
                SignalType.Gate => "#2f74c4",
                _ => "#2c9457",
            };
            linkStyles.Add($"  linkStyle {i} stroke:{color}");
        }
        mmd.AppendLine(string.Join(Environment.NewLine, linkStyles));

        foreach (DiagramNode node in layout.Nodes.Where(n => n.LibraryKey is not null))
        {
            mmd.AppendLine($"  click {Id(node.Key)} href \"{Core.Catalog.VcvLibrary.PageUrl(node.LibraryKey!)}\" _blank");
        }

        mmd.AppendLine("  classDef time stroke:#2f74c4,stroke-width:2px");
        mmd.AppendLine("  classDef pitch stroke:#b97d00,stroke-width:2px");
        mmd.AppendLine("  classDef controller stroke:#2c9457,stroke-width:2px");
        mmd.AppendLine("  classDef source stroke:#d64545,stroke-width:2px");
        mmd.AppendLine("  classDef modifier stroke:#c2611f,stroke-width:2px");
        mmd.AppendLine("  classDef mixer stroke:#7a52b3,stroke-width:2px");
        mmd.AppendLine("  classDef effect stroke:#b0479a,stroke-width:2px");
        mmd.AppendLine("  classDef io stroke:#6b6b70,stroke-width:2px");
        mmd.AppendLine("  classDef performance stroke:#138a8a,stroke-width:2px");
        mmd.AppendLine("  classDef monitor stroke:#9a958a,stroke-width:1px,stroke-dasharray:2 3");
        return mmd.ToString();
    }

    /// <summary>
    /// Text inside a quoted Mermaid label: markup characters as entity codes (Mermaid renders labels as HTML), and on
    /// one line (a line break would end the statement).
    /// </summary>
    private static string Label(string text) => OneLine(text)
        .Replace("&", "#amp;")
        .Replace("<", "#lt;")
        .Replace(">", "#gt;")
        .Replace("\"", "#quot;");

    /// <summary>Line breaks (CR, LF or both) as spaces.</summary>
    internal static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>"m123" / "g:voice-1" → a valid identifier.</summary>
    private static string Id(string key) => "n_" + new string(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}