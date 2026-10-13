using System.Text;
using System.Xml.Linq;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;

namespace VcvPatchTools.Tests;

/// <summary>Module names and intents are text from the patch or the user: every export must keep them as text.</summary>
public class ExportEscapingTests
{
    // An unknown module is titled by its model slug, so the slug carries the troublesome characters.
    private const string oddModel = "A<b>&\"c\"\nd";
    private const string oddIntent = "x < y & \"z\"\r\nw";

    private static DiagramLayout OddLayout()
    {
        string model = oddModel.Replace("\"", "\\\"").Replace("\n", "\\n");
        byte[] patch = Encoding.UTF8.GetBytes($$"""
            {"modules": [
              {"id": 1, "plugin": "Odd", "model": "{{model}}", "pos": [0, 0]},
              {"id": 2, "plugin": "Fundamental", "model": "VCF", "pos": [10, 0]}
            ], "cables": [
              {"id": 5, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 3}
            ]}
            """);
        PatchAnalysis analysis = PatchDiagram.Analyze(patch);
        return PatchDiagram.Layout(analysis, "Odd \"patch\" <1>", PatchDiagram.GroupKeys(analysis), new Dictionary<long, string> { [5] = oddIntent });
    }

    [Fact]
    public void MermaidLabelsUseEntityCodesAndStayOnOneLine()
    {
        DiagramLayout layout = OddLayout();
        string mmd = PatchDiagram.Export(layout, DiagramFormat.Mermaid);

        DiagramNode odd = layout.Nodes.Single(n => n.Title.StartsWith("A<b>", StringComparison.Ordinal));
        string line = mmd.Split('\n').Single(l => l.TrimStart().StartsWith("n_" + odd.Key + "[", StringComparison.Ordinal));
        Assert.Contains("A#lt;b#gt;#amp;#quot;c#quot; d", line);
        Assert.EndsWith(":::" + odd.Role.ToString().ToLowerInvariant(), line.TrimEnd());
        Assert.Contains("|\"x #lt; y #amp; #quot;z#quot; w\"|", mmd);
        // Only the line break between title and subtitle remains as markup.
        Assert.DoesNotContain("<b>", mmd);
        Assert.DoesNotContain("x < y", mmd);
    }

    [Fact]
    public void DotStringsAreQuotedAndStayOnOneLine()
    {
        DiagramLayout layout = OddLayout();
        string dot = PatchDiagram.Export(layout, DiagramFormat.Dot);

        Assert.Contains("digraph \"Odd \\\"patch\\\" <1>\" {", dot);
        Assert.Contains("[label=\"A<b>&\\\"c\\\" d\\n", dot);
        Assert.Contains("label=\"x < y & \\\"z\\\" w\"", dot);
        foreach (string line in dot.Split('\n').Where(l => l.Length > 0))
        {
            // A string never runs past its line: every quote that isn't escaped is paired.
            int quotes = line.Replace("\\\\", "").Replace("\\\"", "").Count(c => c == '"');
            Assert.True(quotes % 2 == 0, $"Unbalanced quotes: {line}");
        }
    }

    [Fact]
    public void SvgStaysWellFormedAndKeepsTheText()
    {
        DiagramLayout layout = OddLayout();
        string svg = PatchDiagram.Export(layout, DiagramFormat.Svg);

        XDocument document = XDocument.Parse(svg);
        XNamespace ns = "http://www.w3.org/2000/svg";
        Assert.Equal("Odd \"patch\" <1>", document.Root!.Attribute("aria-label")!.Value);
        Assert.Contains(document.Descendants(ns + "text"), t => (string?)t.Attribute("class") == "title" && t.Value.StartsWith("A<b>&\"c\"", StringComparison.Ordinal));
        Assert.Contains(document.Descendants(ns + "text"), t => (string?)t.Attribute("class") == "label" && t.Value.StartsWith("x < y & \"z\"", StringComparison.Ordinal));
    }

    [Fact]
    public void HtmlStaysWellFormedAroundTheText()
    {
        string html = PatchDiagram.Export(OddLayout(), DiagramFormat.Html);

        Assert.Contains("<title>Odd &quot;patch&quot; &lt;1&gt;</title>", html);
        Assert.DoesNotContain("A<b>", html);
    }
}