using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;
using VcvPatchTools.Diagram.Render;
using VcvPatchTools.Web.Components;
using StaticRenderer = Microsoft.AspNetCore.Components.Web.HtmlRenderer;

namespace VcvPatchTools.Tests;

/// <summary>
/// The app draws the diagram in Razor (to handle clicks in C#), the exports with <see cref="SvgRenderer"/>: both must
/// produce the same boxes, tabs, cables and labels, which diagram.css and the shared scripts rely on.
/// The app's own extras are left out of the comparison: the lanes' titles (drawn on top, following the scroll),
/// the click hints in tooltips, and the state classes (selected, pinned).
/// </summary>
public class PatchDiagramViewTests
{
    private static readonly XNamespace svg = "http://www.w3.org/2000/svg";

    public static TheoryData<string> Layouts => new TheoryData<string> { "overview", "unfolded", "badges", "watchers" };

    private static DiagramLayout Layout(string name)
    {
        PatchAnalysis analysis = name == "watchers" ? PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("Solar42f16.vcv"))) : Fixtures.AmbientJam;
        return name switch
        {
            "overview" => PatchDiagram.Layout(analysis, "Patch"),
            "badges" => PatchDiagram.Layout(analysis, "Patch", PatchDiagram.GroupKeys(analysis), monitors: MonitorView.Badge),
            "watchers" => PatchDiagram.Layout(analysis, "Patch", PatchDiagram.GroupKeys(analysis), monitors: MonitorView.Modules),
            _ => PatchDiagram.Layout(analysis, "Patch", PatchDiagram.GroupKeys(analysis)),
        };
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task TheAppDrawsWhatTheExportsDraw(string name)
    {
        DiagramLayout layout = Layout(name);
        XElement exported = XElement.Parse(SvgRenderer.Render(layout, standalone: false));
        XElement app = SvgOf(await RenderAsync(layout));

        foreach (string part in new[] { "vpd-edges", "vpd-nodes", "vpd-labels" })
        {
            List<string> expected = Flatten(Part(exported, part)).ToList();
            List<string> actual = Flatten(Part(app, part)).ToList();
            Assert.True(expected.Count > 0, $"{part} is empty");
            Assert.Equal(expected, actual);
        }
        Assert.Equal(Flatten(exported.Element(svg + "defs")!), Flatten(app.Element(svg + "defs")!));
    }

    private static XElement Part(XElement root, string cssClass) => root.Elements(svg + "g").Single(g => (string?)g.Attribute("class") == cssClass);

    /// <summary>Every element in document order, with its attributes (classes as a set) and its text, tooltips aside.</summary>
    private static IEnumerable<string> Flatten(XElement part) => part.Descendants().Select(e =>
    {
        IEnumerable<string> attributes = e.Attributes()
            .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .Select(a => a.Name.LocalName == "class" ? $"class={string.Join(" ", ClassesOf(a.Value))}" : $"{a.Name.LocalName}={a.Value}");
        string text = e.Name.LocalName == "text" ? e.Value : "";
        return $"{e.Name.LocalName}[{string.Join(", ", attributes)}]{text}";
    });

    /// <summary>The classes, in order, without the app's state classes.</summary>
    private static IEnumerable<string> ClassesOf(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(c => c is not ("hl" or "selected"));

    private static XElement SvgOf(string html)
    {
        int start = html.IndexOf("<svg", StringComparison.Ordinal);
        int end = html.IndexOf("</svg>", start, StringComparison.Ordinal) + "</svg>".Length;
        // The app's markup is HTML: the svg element gets its namespace from the page, add it for the XML parser.
        // Event modifiers (@onclick:stopPropagation) show as bare marker attributes in static rendering: not markup.
        string markup = Regex.Replace(html[start..end], @" __internal_\w+", "").Replace("<svg ", $"<svg xmlns=\"{svg.NamespaceName}\" ", StringComparison.Ordinal);
        return XElement.Parse(markup);
    }

    private static async Task<string> RenderAsync(DiagramLayout layout)
    {
        ServiceCollection services = new ServiceCollection();
        services.AddSingleton<IJSRuntime, NoJsRuntime>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using StaticRenderer renderer = new StaticRenderer(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<PatchDiagramView>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Layout"] = layout }));
            return root.ToHtmlString();
        });
    }

    /// <summary>Static rendering runs no after-render code, so the view never calls the browser.</summary>
    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException($"Unexpected call to {identifier}");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new InvalidOperationException($"Unexpected call to {identifier}");
    }
}