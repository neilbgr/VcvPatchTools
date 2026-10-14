using System.Text.Json.Nodes;
using VcvPatchTools.Bridge;
using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Tests;

public class CardinalCompatibilityTests
{
    private static JsonObject MixedPatch() => TestPatchBuilder.Root(new[]
    {
        TestPatchBuilder.Module(1, "Fundamental", "VCO"),
        TestPatchBuilder.Module(2, "Core", "AudioInterface2"),
        TestPatchBuilder.Module(3, "AmbientModules", "LunarVCO"),
        TestPatchBuilder.Module(4, "AmbientModules", "LunarVCO"),
        TestPatchBuilder.Module(5, "SomeCommercialPlugin", "Thing"),
    });

    [Fact]
    public void Find_groups_what_official_Cardinal_would_drop()
    {
        List<CardinalGap> gaps = CardinalCompatibility.Find(MixedPatch());

        Assert.Equal(new[]
        {
            new CardinalGap(new ModuleSlug("AmbientModules", "LunarVCO"), CardinalAvailability.NeilbgrEdition, 2),
            new CardinalGap(new ModuleSlug("SomeCommercialPlugin", "Thing"), CardinalAvailability.Missing, 1),
        }, gaps);
    }

    [Fact]
    public void Converting_to_Cardinal_warns_about_missing_and_neilbgr_only_modules()
    {
        ConversionResult result = PatchConverter.Convert(TestPatchBuilder.ToPatchArchive(MixedPatch()), PatchOrigin.Cardinal);

        Assert.Contains(result.Warnings, w => w.StartsWith("Not bundled with Cardinal", StringComparison.Ordinal) && w.Contains("SomeCommercialPlugin/Thing"));
        Assert.Contains(result.Warnings, w => w.Contains("neilbgr edition") && w.Contains("AmbientModules/LunarVCO ×2"));
    }

    [Fact]
    public void Converting_to_Rack_doesnt_check_Cardinal_modules()
    {
        ConversionResult result = PatchConverter.Convert(TestPatchBuilder.ToPatchArchive(MixedPatch()), PatchOrigin.Rack);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("Cardinal"));
    }
}