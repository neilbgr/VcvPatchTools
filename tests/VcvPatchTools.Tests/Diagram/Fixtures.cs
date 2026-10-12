using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;

namespace VcvPatchTools.Tests;

internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    public static byte[] AmbientJamBytes => File.ReadAllBytes(Path("AmbientJam.vcv"));

    public static PatchAnalysis AmbientJam => PatchDiagram.Analyze(AmbientJamBytes);

    /// <summary>Aluminium's Zones demo: a keyboard split into two lanes, two VCOs each, velocity on lane B.</summary>
    public static PatchAnalysis Zones => PatchDiagram.Analyze(File.ReadAllBytes(Path("Zones.vcv")));
}