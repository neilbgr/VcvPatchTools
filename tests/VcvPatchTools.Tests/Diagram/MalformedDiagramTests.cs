using System.Text;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;

namespace VcvPatchTools.Tests;

/// <summary>Patches a browser may hand the app (hand-edited, truncated, from a buggy tool): a clear error, never a crash deep inside.</summary>
public class MalformedDiagramTests
{
    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void TwoModulesWithOneIdAreReportedAsInvalidData()
    {
        byte[] patch = Json("""
            {"modules": [
              {"id": 7, "plugin": "Fundamental", "model": "VCO", "pos": [0, 0]},
              {"id": 7, "plugin": "Fundamental", "model": "VCF", "pos": [10, 0]}
            ], "cables": [
              {"id": 1, "outputModuleId": 7, "outputId": 0, "inputModuleId": 7, "inputId": 3}
            ]}
            """);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PatchDiagram.Analyze(patch));

        Assert.Contains("7", error.Message);
        Assert.Contains("more than one module", error.Message);
    }

    [Fact]
    public void CablesToModulesThatAreNotThereAreLeftOut()
    {
        byte[] patch = Json("""
            {"modules": [
              {"id": 1, "plugin": "Fundamental", "model": "VCO", "pos": [0, 0]},
              {"id": 2, "plugin": "Fundamental", "model": "VCF", "pos": [10, 0]}
            ], "cables": [
              {"id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 3},
              {"id": 2, "outputModuleId": 1, "outputId": 1, "inputModuleId": 99, "inputId": 0}
            ]}
            """);

        PatchAnalysis analysis = PatchDiagram.Analyze(patch);
        DiagramLayout layout = PatchDiagram.Layout(analysis, "Dangling", PatchDiagram.GroupKeys(analysis));

        Assert.Single(analysis.Cables);
        Assert.Single(layout.Edges);
    }

    [Fact]
    public void ANullIntentFallsBackToTheSuggestion()
    {
        PatchAnalysis analysis = Fixtures.AmbientJam;
        AnalyzedCable cable = analysis.Cables[0];
        Dictionary<long, string> intents = new Dictionary<long, string> { [cable.Cable.Id] = null! };

        DiagramLayout layout = PatchDiagram.Layout(analysis, "Ambient Jam", PatchDiagram.GroupKeys(analysis), intents);

        DiagramEdge edge = layout.Edges.Single(e => e.CableIds.SequenceEqual(new[] { cable.Cable.Id }));
        Assert.Equal(cable.SuggestedIntent, edge.Intent);
    }

    [Fact]
    public void AnArchiveAlreadyReadAnalyzesLikeItsBytes()
    {
        PatchAnalysis fromArchive = PatchDiagram.Analyze(Core.Patch.PatchArchive.Read(Fixtures.AmbientJamBytes));

        Assert.Equal(Fixtures.AmbientJam.Modules.Select(m => m.Title), fromArchive.Modules.Select(m => m.Title));
        Assert.Equal(Fixtures.AmbientJam.Cables.Select(c => c.SuggestedIntent), fromArchive.Cables.Select(c => c.SuggestedIntent));
    }

    [Fact]
    public void AnEmptyPatchLaysOutAsAnEmptyDiagram()
    {
        PatchAnalysis analysis = PatchDiagram.Analyze(Json("""{"modules": [], "cables": []}"""));

        DiagramLayout layout = PatchDiagram.Layout(analysis, "Empty", PatchDiagram.GroupKeys(analysis));

        Assert.Empty(layout.Nodes);
        Assert.Empty(layout.Edges);
        Assert.NotEmpty(PatchDiagram.Export(layout, DiagramFormat.Svg));
    }
}