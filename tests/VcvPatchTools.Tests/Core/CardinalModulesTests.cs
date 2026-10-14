using VcvPatchTools.Core.Catalog;

namespace VcvPatchTools.Tests;

public class CardinalModulesTests
{
    private const string pluginsCpp = """
        static void initStatic__Cardinal()
        {
            const StaticPluginLoader spl(p, "Cardinal");
            if (spl.ok())
            {
                p->addModel(modelHostAudio2);
               #ifndef HEADLESS
                p->addModel(modelSassyScope);
               #else
                spl.removeModule("SassyScope");
               #endif
            }
        }

        static void initStatic__Fundamental()
        {
            const StaticPluginLoader spl(p, "Fundamental");
            if (spl.ok())
            {
                p->addModel(modelVCO);
                spl.removeModule("Unused");
            }
        }

        static void initStatic__Zam()
        {
            const StaticPluginLoader spl(p, "ZamAudio");
        }

        void initStaticPlugins()
        {
            initStatic__Cardinal();
            initStatic__Fundamental();
            // initStatic__Zam();
        }
        """;

    private const string overridePluginCpp = """
        static const std::map<std::string, std::string> pluginSlugFallbacks = {
            {"VultModulesFree", "VultModules"},
            // {"", ""},
        };
        static const std::map<PluginModuleSlug, PluginModuleSlug> moduleSlugFallbacks = {
            {{"Core", "AudioInterface2"}, {"Cardinal", "HostAudio2"}},
        };
        """;

    private static string? Manifest(string dir) => dir switch
    {
        "Cardinal" => """{ "slug": "Cardinal", "modules": [ { "slug": "HostAudio2" }, { "slug": "SassyScope" } ] }""",
        "Fundamental" => """{ "slug": "Fundamental", "modules": [ { "slug": "VCO" }, { "slug": "Unused" } ] }""",
        "ZamAudio" => """{ "slug": "ZamAudio", "modules": [ { "slug": "ZamComp" } ] }""",
        _ => null,
    };

    [Fact]
    public void Scan_keeps_manifest_modules_of_called_plugins_minus_removed_ones()
    {
        CardinalScan scan = CardinalScanner.Scan(pluginsCpp, overridePluginCpp, Manifest);

        Assert.Equal(new[] { "Cardinal/HostAudio2", "Cardinal/SassyScope", "Fundamental/VCO" }, scan.Modules.Order(StringComparer.Ordinal));
        Assert.Equal("Cardinal/HostAudio2", scan.ModuleFallbacks["Core/AudioInterface2"]);
        Assert.Equal("VultModules", scan.PluginFallbacks["VultModulesFree"]);
        Assert.Single(scan.PluginFallbacks);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void Check_tells_official_neilbgr_fallback_and_missing()
    {
        CardinalModules modules = new CardinalModules(
            new[] { "Fundamental/VCO", "Cardinal/HostAudio2", "VultModules/Lateralus" },
            new[] { "AmbientModules/LunarVCO" },
            new Dictionary<string, string> { ["Core/AudioInterface2"] = "Cardinal/HostAudio2" },
            new Dictionary<string, string> { ["VultModulesFree"] = "VultModules" });

        Assert.Equal(new CardinalSupport(CardinalAvailability.Official), modules.Check("Fundamental", "VCO"));
        Assert.Equal(new CardinalSupport(CardinalAvailability.Official, "Cardinal/HostAudio2"), modules.Check("Core", "AudioInterface2"));
        Assert.Equal(new CardinalSupport(CardinalAvailability.Official, "VultModules/Lateralus"), modules.Check("VultModulesFree", "Lateralus"));
        Assert.Equal(new CardinalSupport(CardinalAvailability.NeilbgrEdition), modules.Check("AmbientModules", "LunarVCO"));
        Assert.Equal(new CardinalSupport(CardinalAvailability.Missing), modules.Check("Commercial", "Thing"));
    }

    [Fact]
    public void FromScans_lists_in_neilbgr_only_what_upstream_lacks()
    {
        CardinalScan upstream = new CardinalScan(new HashSet<string> { "A/X", "CountModula/Y" }, new Dictionary<string, string>(), new Dictionary<string, string>(), Array.Empty<string>());
        CardinalScan fork = new CardinalScan(new HashSet<string> { "A/X", "Aluminium/Zones" }, new Dictionary<string, string>(), new Dictionary<string, string>(), Array.Empty<string>());

        CardinalModules modules = CardinalModules.FromJson(CardinalModules.FromScans(upstream, fork).ToJson());

        Assert.Equal(CardinalAvailability.Official, modules.Check("CountModula", "Y").Availability);
        Assert.Equal(CardinalAvailability.NeilbgrEdition, modules.Check("Aluminium", "Zones").Availability);
        Assert.Equal(1, modules.NeilbgrCount);
    }

    [Fact]
    public void Embedded_list_knows_official_and_neilbgr_modules()
    {
        CardinalModules modules = CardinalModules.Embedded;

        Assert.True(modules.IsKnown);
        Assert.Equal(CardinalAvailability.Official, modules.Check("Fundamental", "VCO").Availability);
        Assert.Equal(CardinalAvailability.Official, modules.Check("Core", "MIDIToCVInterface").Availability);
        Assert.Equal(CardinalAvailability.NeilbgrEdition, modules.Check("AmbientModules", "LunarVCO").Availability);
        Assert.Equal(CardinalAvailability.NeilbgrEdition, modules.Check("Aluminium", "Zones").Availability);
        Assert.Equal("Yes, as Cardinal Audio 8", CardinalModules.Label(modules.Check("Core", "AudioInterface")));
    }
}