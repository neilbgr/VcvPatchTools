using System.Text;
using System.Text.Json.Nodes;
using VcvPatchTools.Bridge;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Tests;

public class PatchConverterRobustnessTests
{
    private static List<JsonObject> Modules(PatchArchive patch) => patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();

    private static JsonObject ByModel(PatchArchive patch, string model) => Modules(patch).Single(m => m["model"]!.GetValue<string>() == model);

    private static JsonObject ById(PatchArchive patch, long id) => Modules(patch).Single(m => m["id"]!.GetValue<long>() == id);

    private static JsonObject At(JsonObject module, int x, int y)
    {
        module["pos"] = new JsonArray { x, y };
        return module;
    }

    private static int X(JsonObject module) => module["pos"]![0]!.GetValue<int>();

    private static long? Link(JsonObject module, string side) => module[side]?.GetValue<long>();

    [Fact]
    public void Merging_into_a_wider_Cardinal_module_pushes_its_row_neighbours_right()
    {
        // CV-Gate alone is 10 HP, HostMIDIGate 14 HP: whatever touched its right edge must move 4 HP right.
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            At(TestPatchBuilder.Module(1, "Core", "CV-Gate"), 0, 0),
            At(TestPatchBuilder.Module(2, "Fundamental", "VCA"), 10, 0),
            At(TestPatchBuilder.Module(3, "Fundamental", "VCA"), 10, 1),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        Assert.Equal(0, X(ByModel(patch, "HostMIDIGate")));
        Assert.Equal(14, X(ById(patch, 2)));
        Assert.Equal(10, X(ById(patch, 3))); // other row: untouched
    }

    [Fact]
    public void Merging_a_pair_into_a_narrower_Cardinal_module_pulls_its_row_neighbours_left()
    {
        // MIDICCToCVInterface + CV-CC take 20 HP, HostMIDICC only 14.
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            At(TestPatchBuilder.Module(1, "Core", "MIDICCToCVInterface", right: 2), 4, 0),
            At(TestPatchBuilder.Module(2, "Core", "CV-CC", left: 1), 14, 0),
            At(TestPatchBuilder.Module(3, "Fundamental", "VCA"), 24, 0),
            At(TestPatchBuilder.Module(4, "Fundamental", "VCA"), 0, 0),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        Assert.Equal(4, X(ByModel(patch, "HostMIDICC")));
        Assert.Equal(18, X(ById(patch, 3)));
        Assert.Equal(0, X(ById(patch, 4)));
    }

    [Fact]
    public void A_merged_pair_sits_where_its_leftmost_half_was()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            At(TestPatchBuilder.Module(1, "Core", "MIDIToCVInterface", left: 2), 8, 0),
            At(TestPatchBuilder.Module(2, "Core", "CV-MIDI", right: 1), 0, 0),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        Assert.Equal(0, X(ByModel(patch, "HostMIDI")));
    }

    [Theory]
    [InlineData(PatchOrigin.Rack)]
    [InlineData(PatchOrigin.Cardinal)]
    public void Converted_patches_parse_back_in_memory(PatchOrigin target)
    {
        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                At(TestPatchBuilder.Module(1, "Cardinal", "HostMIDI"), 3, 0),
                At(TestPatchBuilder.Module(2, "Core", "MIDIToCVInterface"), 3, 1),
                At(TestPatchBuilder.Module(3, "Fundamental", "VCA"), 20, 0),
            },
            cables: new[] { TestPatchBuilder.Cable(1, 1, 0, 3, 0) });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, target);
        PatchDocument document = PatchReader.Parse(patch.Root);

        Assert.All(Modules(patch), m => Assert.True(m["pos"]![0] is JsonValue v && v.TryGetValue(out int _), m["pos"]!.ToJsonString()));
        Assert.Contains(document.Modules, m => m.Row == 1 && m.Column == 3);
    }

    [Fact]
    public void Fractional_positions_are_rounded_to_whole_HP()
    {
        JsonObject vco = TestPatchBuilder.Module(1, "Fundamental", "VCO");
        vco["pos"] = new JsonArray { 5.6, 0.0 };
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[] { vco }));

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Equal(6, X(ById(patch, 1)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("[\"a\", 0]")]
    [InlineData("{}")]
    public void Invalid_positions_are_rejected(string pos)
    {
        JsonObject vco = TestPatchBuilder.Module(1, "Fundamental", "VCO");
        vco["pos"] = JsonNode.Parse(pos);
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[] { vco }));

        Assert.Throws<InvalidDataException>(() => PatchConverter.Convert(patch, PatchOrigin.Rack));
    }

    [Fact]
    public void Split_relinks_the_neighbours_of_the_replaced_module()
    {
        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                At(TestPatchBuilder.Module(5, "Fundamental", "VCA", right: 1), 0, 0),
                At(TestPatchBuilder.Module(1, "Cardinal", "HostMIDI", left: 5, right: 6), 10, 0),
                At(TestPatchBuilder.Module(6, "Fundamental", "LFO", left: 1), 19, 0),
            },
            cables: new[] { TestPatchBuilder.Cable(1, 1, 0, 5, 0), TestPatchBuilder.Cable(2, 6, 0, 1, 0) });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        long inId = ByModel(patch, "MIDIToCVInterface")["id"]!.GetValue<long>();
        long outId = ByModel(patch, "CV-MIDI")["id"]!.GetValue<long>();
        Assert.Equal(inId, Link(ById(patch, 5), "rightModuleId"));
        Assert.Equal(5, Link(ById(patch, inId), "leftModuleId"));
        Assert.Equal(outId, Link(ById(patch, inId), "rightModuleId"));
        Assert.Equal(inId, Link(ById(patch, outId), "leftModuleId"));
        Assert.Equal(6, Link(ById(patch, outId), "rightModuleId"));
        Assert.Equal(outId, Link(ById(patch, 6), "leftModuleId"));
    }

    [Fact]
    public void Dropping_an_uncabled_module_removes_the_links_to_it()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            At(TestPatchBuilder.Module(5, "Fundamental", "VCA", right: 1), 0, 0),
            At(TestPatchBuilder.Module(1, "Cardinal", "HostMIDI", left: 5, right: 6), 10, 0),
            At(TestPatchBuilder.Module(6, "Fundamental", "LFO", left: 1), 19, 0),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Null(ById(patch, 5)["rightModuleId"]);
        Assert.Null(ById(patch, 6)["leftModuleId"]);
    }

    [Fact]
    public void Merge_relinks_the_neighbours_of_the_replaced_pair()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            At(TestPatchBuilder.Module(5, "Fundamental", "VCA", right: 10), 0, 0),
            At(TestPatchBuilder.Module(10, "Core", "MIDIToCVInterface", left: 5, right: 11), 10, 0),
            At(TestPatchBuilder.Module(11, "Core", "CV-MIDI", left: 10, right: 6), 18, 0),
            At(TestPatchBuilder.Module(6, "Fundamental", "LFO", left: 11), 26, 0),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        JsonObject merged = ByModel(patch, "HostMIDI");
        long mergedId = merged["id"]!.GetValue<long>();
        Assert.Equal(mergedId, Link(ById(patch, 5), "rightModuleId"));
        Assert.Equal(mergedId, Link(ById(patch, 6), "leftModuleId"));
        Assert.Equal(5, Link(merged, "leftModuleId"));
        Assert.Equal(6, Link(merged, "rightModuleId"));
    }

    [Fact]
    public void A_cable_between_two_dropped_ports_of_the_same_module_warns_once()
    {
        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { TestPatchBuilder.Module(1, "Cardinal", "HostMIDICC"), TestPatchBuilder.Module(2, "Fundamental", "VCA") },
            cables: new[]
            {
                TestPatchBuilder.Cable(1, 1, 17, 1, 16), // pitchbend out -> own channel pressure in: neither exists in Rack
                TestPatchBuilder.Cable(2, 1, 0, 2, 0),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Single(result.Warnings);
        Assert.Single(patch.Root["cables"]!.AsArray());
    }

    [Fact]
    public void Gate_compaction_treats_a_null_note_as_unassigned()
    {
        JsonObject gate = TestPatchBuilder.Module(1, "Cardinal", "HostMIDIGate");
        gate["data"] = JsonNode.Parse("""{ "notes": [-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, null, "x"] }""");
        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { gate, TestPatchBuilder.Module(2, "Fundamental", "VCA") },
            cables: new[] { TestPatchBuilder.Cable(1, 1, 16, 2, 0), TestPatchBuilder.Cable(2, 1, 17, 2, 1) });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        List<int> notes = ByModel(patch, "MIDITriggerToCVInterface")["data"]!["notes"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        Assert.Equal(16, notes.Count);
        Assert.All(notes, n => Assert.Equal(-1, n));
    }

    [Fact]
    public void Non_object_module_entries_are_dropped_with_a_warning()
    {
        PatchArchive patch = PatchArchive.Read(Encoding.UTF8.GetBytes("""
            { "modules": [null, 3, { "id": 1, "plugin": "Cardinal", "model": "HostAudio8" }], "cables": [{}, null] }
            """));

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Equal("AudioInterface", Assert.Single(Modules(patch))["model"]!.GetValue<string>());
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("not an object")));
    }

    [Theory]
    [InlineData(PatchOrigin.Rack)]
    [InlineData(PatchOrigin.Cardinal)]
    public void Incomplete_cables_do_not_stop_the_conversion(PatchOrigin target)
    {
        PatchArchive patch = PatchArchive.Read(Encoding.UTF8.GetBytes("""
            { "modules": [{ "id": 1, "plugin": "Cardinal", "model": "HostMIDIGate", "pos": [0, 0] },
                          { "id": 2, "plugin": "Core", "model": "CV-MIDI", "pos": [0, 1] },
                          { "id": 3, "plugin": 5, "model": ["x"] }],
              "cables": [{}, { "outputModuleId": 1 }, { "inputModuleId": "1", "inputId": 0 }, { "outputModuleId": 1, "outputId": 0, "inputModuleId": 1, "inputId": 0 }] }
            """));

        PatchConverter.Convert(patch, target);

        Assert.Equal(4, patch.Root["cables"]!.AsArray().Count);
    }

    [Fact]
    public void A_MIDI_module_without_an_id_is_rejected()
    {
        PatchArchive patch = PatchArchive.Read(Encoding.UTF8.GetBytes("""{ "modules": [{ "plugin": "Cardinal", "model": "HostMIDI" }] }"""));

        Assert.Throws<InvalidDataException>(() => PatchConverter.Convert(patch, PatchOrigin.Rack));
    }

    [Fact]
    public void Unknown_Cardinal_modules_get_their_own_warning()
    {
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[]
        {
            TestPatchBuilder.Module(1, "Cardinal", "HostTime"),
            TestPatchBuilder.Module(2, "Cardinal", "SomethingNew"),
        }));

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Contains(result.Warnings, w => w.Contains("\"HostTime\"") && w.Contains("no VCV Rack equivalent"));
        Assert.Contains(result.Warnings, w => w.Contains("\"SomethingNew\"") && w.Contains("unknown Cardinal module"));
    }

    [Fact]
    public void Detection_ignores_modules_whose_plugin_is_not_a_string()
    {
        PatchArchive patch = PatchArchive.Read(Encoding.UTF8.GetBytes("""{ "modules": [{ "id": 1, "plugin": 5, "model": true }, { "id": 2, "plugin": "Core", "model": "CV-MIDI" }] }"""));

        Assert.Equal(PatchOrigin.Rack, PatchDetector.Detect(patch).Origin);
    }
}