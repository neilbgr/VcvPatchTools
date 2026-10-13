using System.Text.Json.Nodes;
using VcvPatchTools.Bridge;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Tests;

public class PatchConverterTests
{
    [Fact]
    public void Simple_rename_Cardinal_to_Rack()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            TestPatchBuilder.Module(1, "Cardinal", "HostAudio8"),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        JsonObject module = (JsonObject)patch.Root["modules"]![0]!;
        Assert.Equal("Core", module["plugin"]!.GetValue<string>());
        Assert.Equal("AudioInterface", module["model"]!.GetValue<string>());
    }

    [Fact]
    public void Simple_rename_Rack_to_Cardinal()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            TestPatchBuilder.Module(1, "Core", "AudioInterface2"),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        JsonObject module = (JsonObject)patch.Root["modules"]![0]!;
        Assert.Equal("Cardinal", module["plugin"]!.GetValue<string>());
        Assert.Equal("HostAudio2", module["model"]!.GetValue<string>());
    }

    [Fact]
    public void HostMIDI_splits_into_two_Rack_modules_and_rewires_cables()
    {
        const long hostMidiId = 100;
        const long downstreamId = 200; // fed by HostMIDI's Gate output
        const long upstreamId = 300;   // feeds HostMIDI's Pitch input

        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                TestPatchBuilder.Module(hostMidiId, "Cardinal", "HostMIDI"),
                TestPatchBuilder.Module(downstreamId, "Fundamental", "VCA"),
                TestPatchBuilder.Module(upstreamId, "Fundamental", "LFO"),
            },
            cables: new[]
            {
                // HostMIDI GATE_OUTPUT (index 1) -> VCA input 0
                TestPatchBuilder.Cable(1, hostMidiId, 1, downstreamId, 0),
                // LFO output 0 -> HostMIDI PITCH_INPUT (index 0)
                TestPatchBuilder.Cable(2, upstreamId, 0, hostMidiId, 0),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        JsonObject midiToCv = modules.Single(m => m["model"]!.GetValue<string>() == "MIDIToCVInterface");
        JsonObject cvToMidi = modules.Single(m => m["model"]!.GetValue<string>() == "CV-MIDI");
        Assert.Equal("Core", midiToCv["plugin"]!.GetValue<string>());
        Assert.Equal("Core", cvToMidi["plugin"]!.GetValue<string>());

        long midiToCvId = midiToCv["id"]!.GetValue<long>();
        long cvToMidiId = cvToMidi["id"]!.GetValue<long>();

        List<JsonObject> cables = patch.Root["cables"]!.AsArray().Cast<JsonObject>().ToList();
        Assert.Equal(2, cables.Count);

        JsonObject gateCable = cables.Single(c => c["inputModuleId"]!.GetValue<long>() == downstreamId);
        Assert.Equal(midiToCvId, gateCable["outputModuleId"]!.GetValue<long>());
        Assert.Equal(1, gateCable["outputId"]!.GetValue<int>());

        JsonObject pitchCable = cables.Single(c => c["outputModuleId"]!.GetValue<long>() == upstreamId);
        Assert.Equal(cvToMidiId, pitchCable["inputModuleId"]!.GetValue<long>());
        Assert.Equal(0, pitchCable["inputId"]!.GetValue<int>());

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void HostMIDICC_extra_pitchbend_port_has_no_Rack_target_and_is_dropped()
    {
        const long hostMidiCcId = 1;
        const long otherId = 2;

        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                TestPatchBuilder.Module(hostMidiCcId, "Cardinal", "HostMIDICC"),
                TestPatchBuilder.Module(otherId, "Fundamental", "VCA"),
            },
            cables: new[]
            {
                // output index 17 == CC_OUTPUT_PITCHBEND (16 CC cells [0-15] + ch.pressure[16] + pitchbend[17])
                TestPatchBuilder.Cable(1, hostMidiCcId, 17, otherId, 0),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Empty(patch.Root["cables"]!.AsArray());
        Assert.Contains(result.Warnings, w => w.Contains("Pitchbend"));
    }

    [Fact]
    public void No_equivalent_module_is_left_untouched_and_warns()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            TestPatchBuilder.Module(1, "Cardinal", "HostTime"),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        JsonObject module = (JsonObject)patch.Root["modules"]![0]!;
        Assert.Equal("Cardinal", module["plugin"]!.GetValue<string>());
        Assert.Equal("HostTime", module["model"]!.GetValue<string>());
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Splitting_a_Cardinal_MIDI_module_shifts_downstream_modules_to_avoid_overlap()
    {
        // HostMIDICC is 14 HP wide; it splits into MIDICCToCVInterface (10 HP) + CV-CC (10 HP) = 20 HP,
        // 6 HP more than before. Anything touching its right edge must be pushed right by that much,
        // or it ends up overlapping (and Rack's own de-overlap on load scrambles unrelated modules,
        // e.g. tearing a MindMeld mixer away from its expander).
        JsonObject hostMidiCc = TestPatchBuilder.Module(1, "Cardinal", "HostMIDICC");
        hostMidiCc["pos"] = new JsonArray { 44, 0 };

        JsonObject touchingNeighbour = TestPatchBuilder.Module(2, "MindMeldModular", "MixMasterJr");
        touchingNeighbour["pos"] = new JsonArray { 58, 0 }; // touches HostMIDICC's right edge (44+14)

        JsonObject otherRowModule = TestPatchBuilder.Module(3, "MindMeldModular", "MixMasterJr");
        otherRowModule["pos"] = new JsonArray { 58, 1 }; // same x, different row: must NOT move

        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { hostMidiCc, touchingNeighbour, otherRowModule },
            // Both halves must be cabled, or the unused one would be dropped (see the dedicated test for that).
            cables: new[]
            {
                TestPatchBuilder.Cable(1, 1, 0, 2, 0),
                TestPatchBuilder.Cable(2, 3, 0, 1, 0),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        JsonObject midiCcToCv = modules.Single(m => m["model"]!.GetValue<string>() == "MIDICCToCVInterface");
        JsonObject cvToMidiCc = modules.Single(m => m["model"]!.GetValue<string>() == "CV-CC");
        JsonObject neighbour = modules.Single(m => m["id"]!.GetValue<long>() == 2);
        JsonObject otherRow = modules.Single(m => m["id"]!.GetValue<long>() == 3);

        int InX(JsonObject m) => m["pos"]![0]!.GetValue<int>();

        Assert.Equal(44, InX(midiCcToCv));
        Assert.Equal(54, InX(cvToMidiCc)); // touches midiCcToCv's right edge (44 + 10 HP)
        Assert.Equal(64, InX(neighbour));  // touches cvToMidiCc's right edge (54 + 10 HP)
        Assert.Equal(58, InX(otherRow));   // different row: untouched
    }

    [Fact]
    public void Split_drops_the_unused_half_and_does_not_reserve_space_for_it()
    {
        JsonObject hostMidi = TestPatchBuilder.Module(1, "Cardinal", "HostMIDI");
        hostMidi["pos"] = new JsonArray { 0, 0 };
        JsonObject sink = TestPatchBuilder.Module(2, "Fundamental", "VCA");
        sink["pos"] = new JsonArray { 9, 0 }; // touches HostMIDI's right edge (0 + 9 HP)

        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { hostMidi, sink },
            // Only the input side (Pitch, index 0) is actually cabled; nothing feeds HostMIDI's
            // MIDI-out inputs, so CV-MIDI would just sit there disconnected.
            cables: new[] { TestPatchBuilder.Cable(1, hostMidi["id"]!.GetValue<long>(), 0, sink["id"]!.GetValue<long>(), 0) });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        Assert.DoesNotContain(modules, m => m["model"]?.GetValue<string>() == "CV-MIDI");
        JsonObject midiToCv = modules.Single(m => m["model"]!.GetValue<string>() == "MIDIToCVInterface");
        JsonObject sinkAfter = modules.Single(m => m["id"]!.GetValue<long>() == 2);

        Assert.Equal(0, midiToCv["pos"]![0]!.GetValue<int>());
        // MIDIToCVInterface alone is 8 HP (< HostMIDI's 9 HP), so the row shrinks: the sink moves left.
        Assert.Equal(8, sinkAfter["pos"]![0]!.GetValue<int>());
    }

    [Fact]
    public void Split_carries_over_MIDI_channel_CC_numbers_and_gate_notes()
    {
        JsonObject hostMidiCc = TestPatchBuilder.Module(1, "Cardinal", "HostMIDICC");
        hostMidiCc["data"] = new JsonObject
        {
            ["ccs"] = new JsonArray { 10, 20, 30 },
            ["smooth"] = true,
            ["inputChannel"] = 5,  // 1-based, "5" in the Cardinal UI
            ["outputChannel"] = 3, // 0-based, "channel 4" in the Cardinal UI
        };
        JsonObject sinkCc = TestPatchBuilder.Module(2, "Fundamental", "VCA");
        JsonObject sourceCc = TestPatchBuilder.Module(3, "Fundamental", "LFO");

        JsonObject hostMidiGate = TestPatchBuilder.Module(4, "Cardinal", "HostMIDIGate");
        hostMidiGate["pos"] = new JsonArray { 20, 0 };
        hostMidiGate["data"] = new JsonObject
        {
            ["notes"] = new JsonArray { 36, 37, -1 },
            ["velocity"] = true,
            ["inputChannel"] = 0, // "All" in the Cardinal UI
            ["outputChannel"] = 0,
        };
        JsonObject sinkGate = TestPatchBuilder.Module(5, "Fundamental", "VCA");
        JsonObject sourceGate = TestPatchBuilder.Module(6, "Fundamental", "LFO");

        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { hostMidiCc, sinkCc, sourceCc, hostMidiGate, sinkGate, sourceGate },
            cables: new[]
            {
                TestPatchBuilder.Cable(1, 1, 0, 2, 0),
                TestPatchBuilder.Cable(2, 3, 0, 1, 0),
                TestPatchBuilder.Cable(3, 4, 0, 5, 0),
                TestPatchBuilder.Cable(4, 6, 0, 4, 0),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        JsonObject ccIn = modules.Single(m => m["model"]!.GetValue<string>() == "MIDICCToCVInterface");
        JsonObject ccOut = modules.Single(m => m["model"]!.GetValue<string>() == "CV-CC");
        JsonObject gateIn = modules.Single(m => m["model"]!.GetValue<string>() == "MIDITriggerToCVInterface");
        JsonObject gateOut = modules.Single(m => m["model"]!.GetValue<string>() == "CV-Gate");

        Assert.Equal(new[] { 10, 20, 30 }, ccIn["data"]!["ccs"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(new[] { 10, 20, 30 }, ccOut["data"]!["ccs"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.True(ccIn["data"]!["smooth"]!.GetValue<bool>());
        Assert.Equal(4, ccIn["data"]!["midi"]!["channel"]!.GetValue<int>());  // inputChannel 5 (1-based) -> Rack channel 4 (0-based)
        Assert.Equal(3, ccOut["data"]!["midi"]!["channel"]!.GetValue<int>()); // outputChannel is already 0-based

        Assert.Equal(new[] { 36, 37, -1 }, gateIn["data"]!["notes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(new[] { 36, 37, -1 }, gateOut["data"]!["notes"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.True(gateIn["data"]!["velocity"]!.GetValue<bool>());
        Assert.Equal(-1, gateIn["data"]!["midi"]!["channel"]!.GetValue<int>()); // inputChannel 0 ("All") -> Rack channel -1
        Assert.Equal(0, gateOut["data"]!["midi"]!["channel"]!.GetValue<int>());
    }

    [Fact]
    public void Split_carries_over_HostMIDI_pitch_wheel_range_and_poly_settings()
    {
        JsonObject hostMidi = TestPatchBuilder.Module(1, "Cardinal", "HostMIDI");
        hostMidi["data"] = new JsonObject
        {
            ["pwRange"] = 12.0,
            ["smooth"] = true,
            ["channels"] = 4,
            ["polyMode"] = 2,
            ["lastPitch"] = 9000,
            ["lastMod"] = 64,
            ["inputChannel"] = 0,
            ["outputChannel"] = 0,
        };
        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { hostMidi, TestPatchBuilder.Module(2, "Fundamental", "VCA") },
            cables: new[] { TestPatchBuilder.Cable(1, 1, 0, 2, 0) });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);

        JsonObject midiToCv = patch.Root["modules"]!.AsArray().Cast<JsonObject>().Single(m => m["model"]!.GetValue<string>() == "MIDIToCVInterface");
        JsonObject data = midiToCv["data"]!.AsObject();
        Assert.Equal(12.0, data["pwRange"]!.GetValue<double>());
        Assert.True(data["smooth"]!.GetValue<bool>());
        Assert.Equal(4, data["channels"]!.GetValue<int>());
        Assert.Equal(2, data["polyMode"]!.GetValue<int>());
        Assert.Equal(9000, data["lastPitch"]!.GetValue<int>());
        Assert.Equal(64, data["lastMod"]!.GetValue<int>());
    }

    [Fact]
    public void Merge_carries_over_MIDI_channel_CC_numbers_and_gate_notes()
    {
        JsonObject ccIn = TestPatchBuilder.Module(10, "Core", "MIDICCToCVInterface", right: 11);
        ccIn["data"] = JsonNode.Parse("""
            { "ccs": [10, 20, 30, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1], "values": [0, 64],
              "midi": { "driver": 1, "deviceName": "Keystep", "channel": 4 }, "smooth": true, "mpeMode": false, "lsbMode": true }
            """)!.AsObject();
        JsonObject ccOut = TestPatchBuilder.Module(11, "Core", "CV-CC", left: 10);
        ccOut["pos"] = new JsonArray { 10, 0 };
        ccOut["data"] = JsonNode.Parse("""
            { "ccs": [10, 20, 30, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1], "midi": { "driver": 1, "channel": 3 } }
            """)!.AsObject();

        JsonObject gateIn = TestPatchBuilder.Module(20, "Core", "MIDITriggerToCVInterface");
        gateIn["pos"] = new JsonArray { 0, 1 };
        gateIn["data"] = JsonNode.Parse("""
            { "notes": [36, 37, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 51], "velocity": true,
              "midi": { "driver": 1, "channel": -1 }, "mpeMode": true }
            """)!.AsObject();

        JsonObject hostMidiOut = TestPatchBuilder.Module(30, "Core", "CV-MIDI");
        hostMidiOut["pos"] = new JsonArray { 0, 2 };
        hostMidiOut["data"] = JsonNode.Parse("""{ "midi": { "driver": 1, "channel": 7 } }""")!.AsObject();

        JsonObject hostMidiIn = TestPatchBuilder.Module(40, "Core", "MIDIToCVInterface");
        hostMidiIn["pos"] = new JsonArray { 0, 3 };
        hostMidiIn["data"] = JsonNode.Parse("""
            { "pwRange": 2.0, "smooth": false, "channels": 8, "polyMode": 1, "clockDivision": 24, "lastPitch": 8192, "lastMod": 3,
              "midi": { "driver": 1, "channel": 0 } }
            """)!.AsObject();

        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[] { ccIn, ccOut, gateIn, hostMidiOut, hostMidiIn }));

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        JsonObject cc = modules.Single(m => m["model"]!.GetValue<string>() == "HostMIDICC")["data"]!.AsObject();
        Assert.Equal(5, cc["inputChannel"]!.GetValue<int>());  // Rack channel 4 (0-based) -> Cardinal 5 (1-based)
        Assert.Equal(3, cc["outputChannel"]!.GetValue<int>()); // 0-based on both sides
        Assert.Equal(new[] { 10, 20, 30 }, cc["ccs"]!.AsArray().Take(3).Select(n => n!.GetValue<int>()));
        Assert.Equal(new[] { 0, 64 }, cc["values"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.True(cc["smooth"]!.GetValue<bool>());
        Assert.False(cc["mpeMode"]!.GetValue<bool>());
        Assert.True(cc["lsbMode"]!.GetValue<bool>());
        Assert.Null(cc["midi"]);

        JsonObject gate = modules.Single(m => m["model"]!.GetValue<string>() == "HostMIDIGate")["data"]!.AsObject();
        Assert.Equal(0, gate["inputChannel"]!.GetValue<int>()); // Rack -1 ("All") -> Cardinal 0 ("All")
        List<int> notes = gate["notes"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        Assert.Equal(18, notes.Count); // Cardinal's gate has 18 cells, the last two unassigned
        Assert.Equal(new[] { 36, 37 }, notes.Take(2));
        Assert.Equal(51, notes[15]);
        Assert.Equal(new[] { -1, -1 }, notes.Skip(16));
        Assert.True(gate["velocity"]!.GetValue<bool>());
        Assert.True(gate["mpeMode"]!.GetValue<bool>());

        JsonObject loneOut = modules.Single(m => m["model"]!.GetValue<string>() == "HostMIDI" && m["pos"]![1]!.GetValue<int>() == 2)["data"]!.AsObject();
        Assert.Equal(7, loneOut["outputChannel"]!.GetValue<int>());
        Assert.Null(loneOut["inputChannel"]);

        JsonObject loneIn = modules.Single(m => m["model"]!.GetValue<string>() == "HostMIDI" && m["pos"]![1]!.GetValue<int>() == 3)["data"]!.AsObject();
        Assert.Equal(1, loneIn["inputChannel"]!.GetValue<int>());
        Assert.Equal(2.0, loneIn["pwRange"]!.GetValue<double>());
        Assert.False(loneIn["smooth"]!.GetValue<bool>());
        Assert.Equal(8, loneIn["channels"]!.GetValue<int>());
        Assert.Equal(1, loneIn["polyMode"]!.GetValue<int>());
        Assert.Equal(8192, loneIn["lastPitch"]!.GetValue<int>());
        Assert.Equal(3, loneIn["lastMod"]!.GetValue<int>());
        Assert.Null(loneIn["clockDivision"]); // Cardinal's HostMIDI has no clock divider setting

        Assert.DoesNotContain(result.Warnings, w => w.Contains("disagree"));
    }

    [Fact]
    public void Merge_warns_when_the_two_halves_learned_different_CCs_and_keeps_the_MIDI_in_ones()
    {
        JsonObject ccIn = TestPatchBuilder.Module(10, "Core", "MIDICCToCVInterface", right: 11);
        ccIn["data"] = new JsonObject { ["ccs"] = new JsonArray { 1, 2 } };
        JsonObject ccOut = TestPatchBuilder.Module(11, "Core", "CV-CC", left: 10);
        ccOut["pos"] = new JsonArray { 10, 0 };
        ccOut["data"] = new JsonObject { ["ccs"] = new JsonArray { 7, 2 } };
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[] { ccIn, ccOut }));

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        JsonObject merged = Assert.Single(patch.Root["modules"]!.AsArray().Cast<JsonObject>());
        Assert.Equal(new[] { 1, 2 }, merged["data"]!["ccs"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Contains(result.Warnings, w => w.Contains("disagree") && w.Contains("CC"));
    }

    [Fact]
    public void Merge_takes_CCs_from_the_MIDI_out_half_when_it_is_alone()
    {
        JsonObject ccOut = TestPatchBuilder.Module(11, "Core", "CV-CC");
        ccOut["data"] = new JsonObject { ["ccs"] = new JsonArray { 7, 8 }, ["midi"] = new JsonObject { ["channel"] = 2 } };
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[] { ccOut }));

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        JsonObject merged = Assert.Single(patch.Root["modules"]!.AsArray().Cast<JsonObject>());
        Assert.Equal(new[] { 7, 8 }, merged["data"]!["ccs"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal(2, merged["data"]!["outputChannel"]!.GetValue<int>());
    }

    [Fact]
    public void MIDI_settings_survive_a_Cardinal_to_Rack_to_Cardinal_round_trip()
    {
        JsonObject hostMidiGate = TestPatchBuilder.Module(1, "Cardinal", "HostMIDIGate");
        hostMidiGate["data"] = JsonNode.Parse("""
            { "notes": [36, 37, 38, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, 50, -1, -1],
              "velocity": true, "mpeMode": false, "inputChannel": 10, "outputChannel": 9 }
            """)!.AsObject();
        JsonObject hostMidiCc = TestPatchBuilder.Module(2, "Cardinal", "HostMIDICC");
        hostMidiCc["pos"] = new JsonArray { 0, 1 };
        hostMidiCc["data"] = JsonNode.Parse("""
            { "ccs": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], "values": [1, 2, 3],
              "smooth": false, "mpeMode": true, "lsbMode": true, "inputChannel": 0, "outputChannel": 15 }
            """)!.AsObject();
        JsonObject hostMidi = TestPatchBuilder.Module(3, "Cardinal", "HostMIDI");
        hostMidi["pos"] = new JsonArray { 0, 2 };
        hostMidi["data"] = JsonNode.Parse("""
            { "pwRange": 7.0, "smooth": true, "channels": 3, "polyMode": 1, "lastPitch": 100, "lastMod": 5, "inputChannel": 16, "outputChannel": 2 }
            """)!.AsObject();
        JsonObject vca = TestPatchBuilder.Module(4, "Fundamental", "VCA");
        vca["pos"] = new JsonArray { 30, 0 };
        JsonObject lfo = TestPatchBuilder.Module(5, "Fundamental", "LFO");
        lfo["pos"] = new JsonArray { 40, 0 };

        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { hostMidiGate, hostMidiCc, hostMidi, vca, lfo },
            cables: new[]
            {
                // Both halves of each Cardinal module in use, so each splits into a Rack pair and merges back.
                TestPatchBuilder.Cable(1, 1, 0, 4, 0),
                TestPatchBuilder.Cable(2, 5, 0, 1, 0),
                TestPatchBuilder.Cable(3, 2, 0, 4, 1),
                TestPatchBuilder.Cable(4, 5, 1, 2, 0),
                TestPatchBuilder.Cable(5, 3, 0, 4, 2),
                TestPatchBuilder.Cable(6, 5, 2, 3, 0),
            });
        Dictionary<string, JsonObject> before = root["modules"]!.AsArray().Cast<JsonObject>()
            .Where(m => m["plugin"]!.GetValue<string>() == "Cardinal")
            .ToDictionary(m => m["model"]!.GetValue<string>(), m => (JsonObject)m["data"]!.DeepClone());
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Rack);
        PatchArchive rack = PatchArchive.Read(patch.Write());
        ConversionResult back = PatchConverter.Convert(rack, PatchOrigin.Cardinal);

        Assert.Empty(back.Warnings);
        foreach (JsonObject module in rack.Root["modules"]!.AsArray().Cast<JsonObject>().Where(m => m["plugin"]!.GetValue<string>() == "Cardinal"))
        {
            string model = module["model"]!.GetValue<string>();
            Assert.True(JsonNode.DeepEquals(before[model], module["data"]), $"{model}: {before[model].ToJsonString()} became {module["data"]!.ToJsonString()}");
        }
        Assert.Equal(3, rack.Root["modules"]!.AsArray().Cast<JsonObject>().Count(m => m["plugin"]!.GetValue<string>() == "Cardinal"));
    }

    [Fact]
    public void HostMIDIGate_compacts_its_18_cells_into_Rack_MIDI_Gate_16_slots()
    {
        // Cardinal's HostMIDIGate has 18 gate cells (id 0-17); Rack's MIDITriggerToCVInterface only
        // has 16 (id 0-15). A literal "drop anything at index >= 16" truncation silently discards
        // cells 16/17 even when plenty of the *lower* cells (here 0,1,2,8,14) are unused, and — worse —
        // whatever notes/cables those dropped cells DID have no longer line up with anything once the
        // patch is reopened. The correct behaviour is to compact only the cells actually in use (here
        // 9, 10, 11, 15, 16, 17 - six of eighteen) down into Rack's 0..15 range, in ascending order,
        // carrying their notes and cables along so nothing is lost or mismatched.
        JsonObject hostMidiGate = TestPatchBuilder.Module(1, "Cardinal", "HostMIDIGate");
        hostMidiGate["data"] = new JsonObject
        {
            ["notes"] = new JsonArray { -1, -1, -1, 39, 40, 41, 42, 43, -1, 46, 44, 36, 48, 49, -1, 47, 45, 37 },
            ["velocity"] = false,
            ["inputChannel"] = 10,
            ["outputChannel"] = 0,
        };
        JsonObject pads = TestPatchBuilder.Module(2, "AmbientModules", "LunarPads");

        JsonObject root = TestPatchBuilder.Root(
            modules: new[] { hostMidiGate, pads },
            cables: new[]
            {
                TestPatchBuilder.Cable(1, 1, 10, 2, 0), // cell 10 (note 44) -> pad 0
                TestPatchBuilder.Cable(2, 1, 9, 2, 1),  // cell 9  (note 46) -> pad 1
                TestPatchBuilder.Cable(3, 1, 11, 2, 2), // cell 11 (note 36) -> pad 2
                TestPatchBuilder.Cable(4, 1, 16, 2, 3), // cell 16 (note 45) -> pad 3 (beyond Rack's 16)
                TestPatchBuilder.Cable(5, 1, 17, 2, 4), // cell 17 (note 37) -> pad 4 (beyond Rack's 16)
                TestPatchBuilder.Cable(6, 1, 15, 2, 5), // cell 15 (note 47) -> pad 5
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Rack);

        Assert.Empty(result.Warnings); // all 6 used cells fit within Rack's 16 slots: nothing should be dropped

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        JsonObject gateIn = modules.Single(m => m["model"]!.GetValue<string>() == "MIDITriggerToCVInterface");
        long gateInId = gateIn["id"]!.GetValue<long>();

        List<int> notes = gateIn["data"]!["notes"]!.AsArray().Select(n => n!.GetValue<int>()).ToList();
        Assert.Equal(16, notes.Count);

        Dictionary<int, int> cables = patch.Root["cables"]!.AsArray().Cast<JsonObject>()
            .Where(c => c["outputModuleId"]!.GetValue<long>() == gateInId)
            .ToDictionary(c => c["inputId"]!.GetValue<int>(), c => c["outputId"]!.GetValue<int>());

        // Every surviving cable's port must point at the slot that now actually holds its note.
        Assert.Equal(44, notes[cables[0]]);
        Assert.Equal(46, notes[cables[1]]);
        Assert.Equal(36, notes[cables[2]]);
        Assert.Equal(45, notes[cables[3]]);
        Assert.Equal(37, notes[cables[4]]);
        Assert.Equal(47, notes[cables[5]]);

        // Ports get reused (compacted), not reserved-and-empty: every remaining slot is either one of
        // the six live notes above, or explicitly unassigned.
        Assert.Equal(6, notes.Count(n => n != -1));
    }

    [Fact]
    public void Rack_to_Cardinal_merges_adjacent_pair_via_leftRightModuleId()
    {
        const long midiToCvId = 10;
        const long cvMidiId = 11;
        const long sinkId = 20;
        const long sourceId = 21;

        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                TestPatchBuilder.Module(midiToCvId, "Core", "MIDIToCVInterface", right: cvMidiId),
                TestPatchBuilder.Module(cvMidiId, "Core", "CV-MIDI", left: midiToCvId),
                TestPatchBuilder.Module(sinkId, "Fundamental", "VCA"),
                TestPatchBuilder.Module(sourceId, "Fundamental", "LFO"),
            },
            cables: new[]
            {
                TestPatchBuilder.Cable(1, midiToCvId, 1, sinkId, 0),   // gate out -> VCA
                TestPatchBuilder.Cable(2, sourceId, 0, cvMidiId, 0),   // LFO -> pitch in
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        JsonObject merged = modules.Single(m => m["model"]!.GetValue<string>() == "HostMIDI");
        Assert.Equal("Cardinal", merged["plugin"]!.GetValue<string>());
        long mergedId = merged["id"]!.GetValue<long>();

        // The two original Rack modules should be gone, replaced by exactly one merged module.
        Assert.DoesNotContain(modules, m => m["model"]!.GetValue<string>() is "MIDIToCVInterface" or "CV-MIDI");

        List<JsonObject> cables = patch.Root["cables"]!.AsArray().Cast<JsonObject>().ToList();
        Assert.Contains(cables, c => c["outputModuleId"]!.GetValue<long>() == mergedId && c["outputId"]!.GetValue<int>() == 1);
        Assert.Contains(cables, c => c["inputModuleId"]!.GetValue<long>() == mergedId && c["inputId"]!.GetValue<int>() == 0);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Rack_to_Cardinal_converts_lone_unpaired_module_and_warns()
    {
        JsonObject root = TestPatchBuilder.Root(new[]
        {
            TestPatchBuilder.Module(1, "Core", "MIDIToCVInterface"),
        });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        ConversionResult result = PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        List<JsonObject> modules = patch.Root["modules"]!.AsArray().Cast<JsonObject>().ToList();
        Assert.Single(modules);
        Assert.Equal("HostMIDI", modules[0]["model"]!.GetValue<string>());
        Assert.Contains(result.Warnings, w => w.Contains("no adjacent"));
    }

    [Fact]
    public void Rack_to_Cardinal_remaps_cable_color_to_nearest_hue_by_default()
    {
        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                TestPatchBuilder.Module(1, "Fundamental", "VCO"),
                TestPatchBuilder.Module(2, "Fundamental", "VCA"),
            },
            cables: new[]
            {
                TestPatchBuilder.Cable(1, 1, 0, 2, 0, color: "#00b56e"),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal);

        JsonObject cable = (JsonObject)patch.Root["cables"]![0]!;
        Assert.Equal("#52ffbe", cable["color"]!.GetValue<string>());
    }

    [Fact]
    public void Cable_color_left_untouched_when_remapCableColors_is_false()
    {
        JsonObject root = TestPatchBuilder.Root(
            modules: new[]
            {
                TestPatchBuilder.Module(1, "Fundamental", "VCO"),
                TestPatchBuilder.Module(2, "Fundamental", "VCA"),
            },
            cables: new[]
            {
                TestPatchBuilder.Cable(1, 1, 0, 2, 0, color: "#00b56e"),
            });
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(root);

        PatchConverter.Convert(patch, PatchOrigin.Cardinal, remapCableColors: false);

        JsonObject cable = (JsonObject)patch.Root["cables"]![0]!;
        Assert.Equal("#00b56e", cable["color"]!.GetValue<string>());
    }
}