using System.Diagnostics;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;

namespace VcvPatchTools.Tests;

/// <summary>Guards against quadratic-or-worse slips: a big patch must still lay out in a moment (the app does it on every click).</summary>
/// <remarks>Alone, not next to the other tests: on a two-core CI runner they took its time.</remarks>
[Collection(nameof(PerformanceTests))]
public class PerformanceTests
{
    [Fact]
    public void ABigPatchLaysOutUnfoldedInAMoment()
    {
        byte[] bytes = SyntheticPatch.Bytes(800, 1100);
        // Warm-up: catalogs, JIT.
        PatchDiagram.Layout(PatchDiagram.Analyze(SyntheticPatch.Bytes(20, 30)), "warm-up");

        Stopwatch watch = Stopwatch.StartNew();
        PatchAnalysis analysis = PatchDiagram.Analyze(bytes);
        DiagramLayout layout = PatchDiagram.Layout(analysis, "Big", PatchDiagram.GroupKeys(analysis), monitors: MonitorView.Badge);
        watch.Stop();

        Assert.True(layout.Nodes.Count > 700);
        // Generous (a few hundred milliseconds on a laptop), so a slow test machine doesn't fail it.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Analysis and layout took {watch.ElapsedMilliseconds} ms");
    }
}

[CollectionDefinition(nameof(PerformanceTests), DisableParallelization = true)]
public class PerformanceTestsCollection;