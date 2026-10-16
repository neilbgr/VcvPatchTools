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

    /// <summary>A single voice with no mixer: VCO → Surge EG x VCA → VCF → Delay → Plateau → Audio 2.</summary>
    public static PatchAnalysis MatriarchJam => PatchDiagram.Analyze(File.ReadAllBytes(Path("MatriarchJam.vcv")));
}