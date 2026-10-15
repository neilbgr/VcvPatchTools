using System.Text;
using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Core.Patch;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;
using VcvPatchTools.Diagram.Render;

namespace VcvPatchTools.Tests;

public class AnalyzerTests
{
    private static readonly PatchAnalysis analysis = Fixtures.AmbientJam;

    private static List<string> Chain(Voice voice) => voice.ModuleIds.Select(id => analysis.Module(id).Title).ToList();

    [Fact]
    public void DetectsTheTwoVoicesInSignalOrder()
    {
        Assert.Equal(2, analysis.Voices.Count);
        Voice keyboard = analysis.Voices.Single(v => v.PitchOrigin == "Host MIDI");
        Voice sequenced = analysis.Voices.Single(v => v.PitchOrigin == "ADDR-SEQ");
        Assert.Equal(new[] { "PULSE", "VCF #1", "Waveshaper", "EQ" }, Chain(keyboard));
        Assert.Equal(new[] { "LVCO", "VCF #2" }, Chain(sequenced));
    }

    [Theory]
    [InlineData("Host MIDI", Band.External)]
    [InlineData("Host MIDI Gate", Band.External)]
    [InlineData("Clkd", Band.Time)]
    [InlineData("Quantizer", Band.Pitch)]
    [InlineData("LLFO", Band.Modulation)]
    [InlineData("MIX4", Band.Bus)]
    [InlineData("Plateau", Band.Bus)]
    [InlineData("Audio 2", Band.Bus)]
    public void PutsModulesInTheirBand(string title, Band band) =>
        Assert.Equal(band, analysis.Modules.Single(m => m.Title == title).Band);

    [Fact]
    public void ModulesWithoutCablesAreLeftOut() => Assert.DoesNotContain(analysis.Modules, m => m.Title == "ASX");

    [Fact]
    public void TypesEveryCableFromNeilsPalette()
    {
        Dictionary<SignalType, int> counts = analysis.Cables.GroupBy(c => c.Signal).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(12, counts[SignalType.Audio]);
        Assert.Equal(5, counts[SignalType.Pitch]);
        Assert.Equal(11, counts[SignalType.Cv]);
        Assert.Equal(10, counts[SignalType.Gate]);
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void NamesSurgeModulationInputsByTheKnobsTheyMove()
    {
        AnalyzedCable cc4 = analysis.Cables.Single(c => analysis.Module(c.Cable.From.ModuleId).Title == "Host MIDI CC" && c.FromPort == "Cell 4 · CC 60");

        Assert.Equal("Mod 3 → Frequency +40%, Pre-Filter Gain +7%", cc4.ToPort);
        Assert.Equal(new[] { "Frequency", "Pre-Filter Gain" }, cc4.Routes.Select(r => r.Target));
        Assert.Equal("hand control of frequency & pre-filter gain", cc4.SuggestedIntent);
    }

    [Fact]
    public void FlagsAModulationInputWithoutTarget()
    {
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Bogaudio", "model": "Bogaudio-LLFO", "pos": [0, 0] },
                { "id": 2, "plugin": "SurgeXTRack", "model": "SurgeXTVCF", "pos": [10, 0], "params": [ { "id": 5, "value": 0.5 } ] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 3, "color": "#52ff7d" }
              ]
            }
            """;

        PatchAnalysis lonely = PatchDiagram.Analyze(Encoding.UTF8.GetBytes(json));

        Assert.Contains(lonely.Diagnostics, d => d.Contains("does nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void TellsVoiceProcessingFromBusProcessingAndInserts()
    {
        PatchAnalysis meditations = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("MeditationsOnDeath.vcv")));
        AnalyzedModule Find(string title) => meditations.Modules.Single(m => m.Title == title);

        // Reverb right after a voice's filter, before any mixer: part of that voice.
        Assert.Equal(Band.Voice, Find("Plateau #1").Band);
        Assert.Equal(Find("LunarPapaSrapa #1").Voice, Find("Plateau #1").Voice);
        // Filter fed by a mixer: bus processing, with the EQ patched into its insert send/return.
        Assert.Equal(Band.Bus, Find("LunarFilter").Band);
        Assert.Null(Find("LunarFilter").InsertOf);
        Assert.Equal(Find("LunarFilter").Module.Id, Find("EQ").InsertOf);
        // Reverb on an aux send/return of the mixer (its docked AuxSpander is part of it).
        Assert.Equal(Find("MixMasterJr").Module.Id, Find("Plateau #2").InsertOf);
        Assert.Equal(Band.Bus, Find("Plateau #2").Band);
    }

    [Fact]
    public void FindsFeedbackLoopsButNotEffectLoops()
    {
        PatchAnalysis feedback = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("FeedbackTest.vcv")));
        List<string> Titles(PatchAnalysis patch, IReadOnlyList<long> loop) => loop.Select(id => patch.Module(id).Title).Order().ToList();

        Assert.Equal(2, feedback.Loops.Count);
        Assert.Contains(feedback.Loops, l => Titles(feedback, l).SequenceEqual(new[] { "FM-OP #1", "FM-OP #2" }));
        Assert.Contains(feedback.Loops, l => Titles(feedback, l).SequenceEqual(new[] { "VCO #1", "VCO #2" }));
        // An EQ in a filter's insert, a reverb on a mixer's aux send/return: effect loops, not feedback.
        Assert.Empty(PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("MeditationsOnDeathRack.vcv"))).Loops);
    }

    [Fact]
    public void DockedExpandersAreFoldedIntoTheirBase()
    {
        PatchAnalysis drone = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("NotBoringDrone3.vcv")));

        Assert.DoesNotContain(drone.Modules, m => m.Module.Model is "MixPan" or "MixMute" or "MixFade");
        AnalyzedModule mixer = drone.Modules.Single(m => m.Title == "VCA Mix 4 Stereo");
        Assert.Contains("Mix Pan", mixer.Expanders);
        Assert.Contains(drone.Cables, c => c.Cable.To.ModuleId == mixer.Module.Id && c.ToPort == "Mix Pan › Pan CV 2");
    }

    [Fact]
    public void AnExpanderNeedsABaseOfItsOwnFamilyDockedToIt()
    {
        PatchModule mixer = new PatchModule(1, "Venom", "Mix4", 0, 0) { RightModuleId = 2 };
        PatchModule pan = new PatchModule(2, "Venom", "MixPan", 5, 0) { LeftModuleId = 1, RightModuleId = 3 };
        PatchModule mute = new PatchModule(3, "Venom", "MixMute", 8, 0) { LeftModuleId = 2, RightModuleId = 4 };
        PatchModule stranger = new PatchModule(4, "Bogaudio", "Bogaudio-LLFO", 11, 0) { LeftModuleId = 3 };
        PatchModule alone = new PatchModule(5, "Venom", "MixSolo", 30, 0);
        PatchModule wrongFamily = new PatchModule(6, "Venom", "MixSend", 40, 0) { LeftModuleId = 4 };

        Dictionary<long, long> baseOf = ExpanderChains.BaseOf(new[] { mixer, pan, mute, stranger, alone, wrongFamily }, PortCatalog.LoadEmbedded());

        Assert.Equal(new Dictionary<long, long> { [2] = 1, [3] = 1 }, baseOf);
    }

    [Theory]
    [InlineData(null, Role.Performance, Band.External)]
    [InlineData("Core/MIDIToCVInterface", Role.Performance, Band.External)]
    [InlineData("AmbientModules/LunarSequencer", Role.Time, Band.Time)]
    public void PadsArePerformanceUnlessAnotherModulePlaysThem(string? driver, Role role, Band band)
    {
        List<PatchModule> modules = new List<PatchModule>
        {
            new PatchModule(1, "AmbientModules", "LunarPads", 0, 0),
            new PatchModule(2, "Fundamental", "ADSR", 10, 0),
        };
        List<PatchCable> cables = new List<PatchCable> { new PatchCable(1, new PortRef(1, 0), new PortRef(2, 4), "#52beff") };
        if (driver is not null)
        {
            string[] key = driver.Split('/');
            modules.Add(new PatchModule(3, key[0], key[1], 20, 0));
            cables.Add(new PatchCable(2, new PortRef(3, 1), new PortRef(1, 0), "#52beff"));
        }

        PatchAnalysis pads = PatchAnalyzer.CreateDefault().Analyze(new PatchDocument("2.6.6", modules, cables));

        Assert.Equal(role, pads.Module(1).Role);
        Assert.Equal(band, pads.Module(1).Band);
    }

    [Fact]
    public void APianoKeyboardIsPlayedLiveOrOnlyShowsTheNotes()
    {
        List<PatchModule> modules = new List<PatchModule>
        {
            new PatchModule(1, "Core", "MIDIToCVInterface", 0, 0),
            new PatchModule(2, "unless_modules", "pianoid", 10, 0),
            new PatchModule(3, "Fundamental", "VCO", 20, 0),
        };
        PatchCable midiIn = new PatchCable(1, new PortRef(1, 0), new PortRef(2, 0), "#ffd452");
        PatchCable played = new PatchCable(2, new PortRef(2, 0), new PortRef(3, 0), "#ffd452");
        PatchAnalyzer analyzer = PatchAnalyzer.CreateDefault();

        AnalyzedModule player = analyzer.Analyze(new PatchDocument("2.6.6", modules, new[] { midiIn, played })).Module(2);
        AnalyzedModule display = analyzer.Analyze(new PatchDocument("2.6.6", modules, new[] { midiIn })).Module(2);

        Assert.Equal((Role.Performance, Band.External), (player.Role, player.Band));
        Assert.Equal((Role.Monitor, Band.Monitor), (display.Role, display.Band));
    }

    [Fact]
    public void MidiGateAndCcPortsAreNamedByTheirLearnedNoteAndController()
    {
        List<string> ports = analysis.Cables.Select(c => c.FromPort).ToList();

        Assert.Contains("Gate 13 · A2", ports);
        Assert.Contains("Gate 7 · G#2", ports);
        Assert.Contains("Cell 1 · CC 61", ports);
        Assert.Contains("Cell 4 · CC 60", ports);
    }

    [Theory]
    [InlineData("Core", "MIDITriggerToCVInterface", 0, "Gate 1", "Gate 1 · C4")]
    [InlineData("Core", "MIDICCToCVInterface", 1, "Cell 2", "Cell 2 · CC 74 (cutoff)")]
    [InlineData("Core", "CV-Gate", 0, "Cell 1", "Cell 1 · C4")]
    [InlineData("Cardinal", "HostMIDICC", 2, "Cell 3", "Cell 3")]
    [InlineData("Cardinal", "HostMIDICC", 16, "Channel pressure", "Channel pressure")]
    [InlineData("Bogaudio", "Bogaudio-ADSR", 0, "Gate 1", "Gate 1")]
    public void LearnedMidiNumbersNameTheCells(string plugin, string model, int index, string port, string expected)
    {
        PatchModule module = new PatchModule(1, plugin, model, 0, 0)
        {
            LearnedNotes = model.Contains("CC", StringComparison.Ordinal) ? Array.Empty<int>() : new[] { 60 },
            LearnedCcs = model.Contains("CC", StringComparison.Ordinal) ? new[] { 1, 74, -1 } : Array.Empty<int>(),
        };

        Assert.Equal(expected, MidiLearn.Name(module, index, port));
    }

    [Fact]
    public void ALearnedControllerIsTypedByWhereItGoesNotByItsUsualMeaning()
    {
        // CC 11 is "expression" in General MIDI, CC 12 has no usual meaning: both clock a switch, both are gates.
        string json = """
            {
              "version": "2.6.0",
              "modules": [
                { "id": 1, "plugin": "Core", "model": "MIDICCToCVInterface", "pos": [0, 0], "data": { "ccs": [12, 11] } },
                { "id": 2, "plugin": "Fundamental", "model": "SequentialSwitch2", "pos": [10, 0] },
                { "id": 3, "plugin": "Fundamental", "model": "SequentialSwitch2", "pos": [20, 0] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 0, "color": "#3695ef" },
                { "id": 2, "outputModuleId": 1, "outputId": 1, "inputModuleId": 3, "inputId": 0, "color": "#3695ef" }
              ]
            }
            """;

        PatchAnalysis patch = PatchDiagram.Analyze(Encoding.UTF8.GetBytes(json));

        Assert.All(patch.Cables, c => Assert.Equal(SignalType.Gate, c.Signal));
        Assert.Contains(patch.Cables, c => c.FromPort == "Cell 2 · CC 11 (expression)");
        // Its "Clock" input keeps its panel name, but each edge moves on to the next channel, it doesn't set a tempo.
        Assert.All(patch.Cables, c => Assert.Equal(("Clock", "steps to the next channel"), (c.ToPort, c.SuggestedIntent)));
    }

    [Fact]
    public void SurgeOscillatorPortsAndModulationAreNamedAndTyped()
    {
        // Aluminium's Subsequent37-Duo demo: Surge XT Classic VCOs, registered as VCOWidget<ot_classic>::M.
        PatchAnalysis duo = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("Subsequent37-Duo.vcv")));
        AnalyzedCable Into(string title, string port) => duo.Cables.Single(c => duo.Module(c.Cable.To.ModuleId).Title == title && c.ToPort.StartsWith(port, StringComparison.Ordinal));

        Assert.Equal(SignalType.Pitch, Into("Classic VCO #2", "V/Oct").Signal);
        // A vibrato into a modulation input is CV, even though the knob it moves is the pitch.
        AnalyzedCable vibrato = Into("Classic VCO #1", "Mod 1 → Pitch");
        Assert.Equal(SignalType.Cv, vibrato.Signal);
        Assert.DoesNotContain(duo.Cables, c => c.ToPort.StartsWith("in#", StringComparison.Ordinal) || c.FromPort.StartsWith("out#", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("VCF #1", ModuleFunction.Filter)]
    [InlineData("ADSR #2", ModuleFunction.Envelope)]
    [InlineData("LLFO", ModuleFunction.Lfo)]
    [InlineData("Random", ModuleFunction.Random)]
    [InlineData("Clkd", ModuleFunction.Clock)]
    [InlineData("ADDR-SEQ", ModuleFunction.Sequencer)]
    [InlineData("Quantizer", ModuleFunction.Quantizer)]
    [InlineData("Plateau", ModuleFunction.Reverb)]
    [InlineData("Delay Plus Stereo Fx", ModuleFunction.Delay)]
    [InlineData("MIX4", ModuleFunction.Mixer)]
    [InlineData("PULSE", ModuleFunction.Oscillator)]
    [InlineData("Host MIDI", ModuleFunction.Midi)]
    public void ModulesGetTheFunctionATeacherWouldName(string title, ModuleFunction function)
    {
        Assert.Equal(function, analysis.Modules.Single(m => m.Title == title).Function);
    }

    [Fact]
    public void FunctionNamesTitleTheBoxesAndKeepTheModuleUnder()
    {
        DiagramLayout byFunction = PatchDiagram.Layout(analysis, "Ambient Jam", PatchDiagram.GroupKeys(analysis), functionNames: true);
        DiagramNode filter = byFunction.Nodes.Single(n => n.ModuleId is long id && analysis.Module(id).Title == "VCF #1");
        DiagramNode midi = byFunction.Nodes.Single(n => n.ModuleId is long id && analysis.Module(id).Title == "Host MIDI");

        Assert.Equal(("FILTER #1", "VCF #1 · SurgeXTRack"), (filter.Title, filter.Subtitle));
        Assert.Equal("Host MIDI", midi.Title);
        Assert.Contains(byFunction.Edges, e => e.ToTitle == "VCF #1");
    }

    [Fact]
    public void LunarVoicesAreSourcesEvenWithAMiscoloredCable()
    {
        PatchAnalysis meditations = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("MeditationsOnDeath.vcv")));

        Assert.Equal(8, meditations.Voices.Count);
        Assert.Equal(4, meditations.Voices.Count(v => v.Sources.StartsWith("Lunar50Drone", StringComparison.Ordinal)));
        Assert.Contains(meditations.Diagnostics, d => d.Contains("treated as audio", StringComparison.Ordinal));
    }

    [Fact]
    public void ModulatorsServingOnlyOneVoiceBelongToIt()
    {
        IReadOnlyList<PatchGroup> groups = PatchGrouping.Build(analysis);
        PatchGroup Owner(string title) => groups.Single(g => g.Members.Contains(analysis.Modules.Single(m => m.Title == title).Module.Id));

        Assert.Equal(Owner("PULSE"), Owner("LLFO"));
        Assert.Equal(Owner("PULSE"), Owner("SLEW"));
        Assert.Equal(Owner("LVCO"), Owner("Quantizer"));
        // Random pans MIX4's channel 2, which only carries the LVCO voice.
        Assert.Equal(Owner("LVCO"), Owner("Random"));
        Assert.Equal("time", Owner("ADDR-SEQ").Key);
        Assert.Equal("external", Owner("Host MIDI").Key);
    }

    [Fact]
    public void IdenticalOneModuleVoicesFoldTogether()
    {
        PatchAnalysis meditations = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("MeditationsOnDeathRack.vcv")));

        PatchGroup drones = Assert.Single(PatchGrouping.Build(meditations), g => g.Title.Contains("Lunar50Drone ×4", StringComparison.Ordinal));
        Assert.Equal(4, drones.Count);
        Assert.Empty(meditations.Diagnostics);
    }

    [Theory]
    [InlineData(true, Band.External)]
    [InlineData(false, Band.Pitch)]
    public void KeyboardZonesRelayTheOutsideOnlyWhenFedByHostMidi(bool fromHostMidi, Band expected)
    {
        string source = fromHostMidi
            ? """{ "id": 1, "plugin": "Cardinal", "model": "HostMIDI", "pos": [0, 0] }"""
            : """{ "id": 1, "plugin": "Bogaudio", "model": "Bogaudio-AddrSeq", "pos": [0, 0] }""";
        string json = $$"""
            {
              "modules": [
                {{source}},
                { "id": 2, "plugin": "Aluminium", "model": "Zones", "pos": [10, 0] },
                { "id": 3, "plugin": "Bogaudio", "model": "Bogaudio-LVCO", "pos": [20, 0] },
                { "id": 4, "plugin": "JW-Modules", "model": "FullScope", "pos": [30, 0] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 0, "color": "#ffd452" },
                { "id": 2, "outputModuleId": 2, "outputId": 0, "inputModuleId": 3, "inputId": 0, "color": "#ffd452" },
                { "id": 3, "outputModuleId": 3, "outputId": 0, "inputModuleId": 4, "inputId": 0, "color": "#ff5252" }
              ]
            }
            """;

        PatchAnalysis patch = PatchDiagram.Analyze(Encoding.UTF8.GetBytes(json));

        Assert.Equal(expected, patch.Modules.Single(m => m.Module.Model == "Zones").Band);
        Assert.Equal(Band.Monitor, patch.Modules.Single(m => m.Module.Model == "FullScope").Band);
        Assert.DoesNotContain(PatchDiagram.Layout(patch, "t").Nodes, n => n.Group == "monitor" || n.Watchers.Count > 0);
        DiagramLayout withScopes = PatchDiagram.Layout(patch, "t", PatchDiagram.GroupKeys(patch), monitors: MonitorView.Badge);
        Assert.DoesNotContain(withScopes.Nodes, n => n.Group == "monitor");
        Assert.Equal("Full Scope 'X' ← LVCO 'Signal'", Assert.Single(withScopes.Nodes.Single(n => n.Title == "LVCO").Watchers));
    }

    [Fact]
    public void ScopesAsModulesGoInAColumnOnTheRightNextToWhatTheyWatch()
    {
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Fundamental", "model": "LFO", "pos": [0, 0] },
                { "id": 2, "plugin": "Fundamental", "model": "VCO", "pos": [10, 0] },
                { "id": 3, "plugin": "Fundamental", "model": "VCF", "pos": [20, 0] },
                { "id": 4, "plugin": "Fundamental", "model": "Scope", "pos": [30, 0] },
                { "id": 5, "plugin": "JW-Modules", "model": "FullScope", "pos": [40, 0] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 0, "color": "#52ff7d" },
                { "id": 2, "outputModuleId": 2, "outputId": 0, "inputModuleId": 3, "inputId": 3, "color": "#ff5252" },
                { "id": 3, "outputModuleId": 1, "outputId": 0, "inputModuleId": 4, "inputId": 0, "color": "#52ff7d" },
                { "id": 4, "outputModuleId": 3, "outputId": 0, "inputModuleId": 5, "inputId": 0, "color": "#ff5252" }
              ]
            }
            """;
        PatchAnalysis patch = PatchDiagram.Analyze(Encoding.UTF8.GetBytes(json));

        DiagramLayout layout = PatchDiagram.Layout(patch, "t", PatchDiagram.GroupKeys(patch), monitors: MonitorView.Modules);

        List<DiagramNode> scopes = layout.Nodes.Where(n => n.Band == Band.Monitor).OrderBy(n => n.Y).ToList();
        Assert.Equal(new[] { "Scope", "Full Scope" }, scopes.Select(n => n.Title));
        double flowRight = layout.Nodes.Where(n => n.Band != Band.Monitor).Max(n => n.X + n.Width);
        Assert.All(scopes, n => Assert.True(n.X > flowRight));
        Assert.True(scopes[1].Y >= scopes[0].Y + scopes[0].Height, "scopes don't overlap");
        DiagramBand column = Assert.Single(layout.Bands, b => b.IsVertical);
        Assert.True(column.X > flowRight && column.X + column.Width <= layout.Width);
        Assert.All(layout.Bands.Where(b => !b.IsVertical), b => Assert.True(layout.Extent(b).X + layout.Extent(b).Width < column.X));
        Assert.Equal(2, layout.Edges.Count(e => e.IsMonitor));
        Assert.Contains("monitor", SvgRenderer.EdgeClass(layout.Edges.First(e => e.IsMonitor)));
        Assert.DoesNotContain(layout.Nodes, n => n.Watchers.Count > 0);
        Assert.Equal("VCF", layout.Node(layout.Edges.Single(e => e.To == scopes[1].Key).From).Title);

        // The overview folds the two into one box, still in the column.
        DiagramLayout overview = PatchDiagram.Layout(patch, "t", monitors: MonitorView.Modules);
        DiagramNode folded = Assert.Single(overview.Nodes, n => n.Band == Band.Monitor);
        Assert.True(folded.IsFolded);
        Assert.Equal(folded, Assert.Single(overview.Members(overview.Bands.Single(b => b.IsVertical))));
    }

    [Fact]
    public void ReportsStackedCablesOnOneInput()
    {
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Fundamental", "model": "LFO", "pos": [0, 0] },
                { "id": 2, "plugin": "Fundamental", "model": "LFO", "pos": [10, 0] },
                { "id": 3, "plugin": "Fundamental", "model": "VCF", "pos": [20, 0] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 3, "inputId": 0 },
                { "id": 2, "outputModuleId": 2, "outputId": 0, "inputModuleId": 3, "inputId": 0 }
              ]
            }
            """;

        PatchAnalysis stacked = PatchDiagram.Analyze(Encoding.UTF8.GetBytes(json));

        Assert.Contains(stacked.Diagnostics, d => d.Contains("2 stacked cables", StringComparison.Ordinal));
    }

    [Fact]
    public void TopologicalOrderSurvivesFeedbackLoops()
    {
        List<long> order = GraphOrder.Topological(new long[] { 1, 2, 3 }, new[] { (1L, 2L), (2L, 3L), (3L, 2L) });

        Assert.Equal(new long[] { 1, 2, 3 }, order);
    }

    [Fact]
    public void KeyboardZonesMakeOneVoicePerZoneThroughTheirOwnMixer()
    {
        PatchAnalysis zones = Fixtures.Zones;
        List<string> Titles(Voice voice) => voice.ModuleIds.Select(id => zones.Module(id).Title).ToList();

        Assert.Equal(2, zones.Voices.Count);
        Assert.Equal("Zones › Pitch Zone A", zones.Voices[0].PitchOrigin);
        Assert.Equal("Zones › Pitch Zone B", zones.Voices[1].PitchOrigin);
        Assert.Equal(new[] { "Mix #1", "VCF #1" }, Titles(zones.Voices[0]).TakeLast(2));
        Assert.Equal(new[] { "Mix #2", "VCF #2" }, Titles(zones.Voices[1]).TakeLast(2));
        Assert.Equal(Band.Bus, zones.Modules.Single(m => m.Title == "VCA Mix").Band);
    }

    [Fact]
    public void KeyboardZoneLanesTakeTheirEnvelopesAndVelocity()
    {
        PatchAnalysis zones = Fixtures.Zones;
        IReadOnlyList<PatchGroup> groups = PatchGrouping.Build(zones);
        List<string> Members(string key) => groups.Single(g => g.Key == key).Members.Select(id => zones.Module(id).Title).ToList();

        Assert.Contains("ADSR #1", Members("voice-1"));
        Assert.Equal(new[] { "ADSR #2", "Octave", "VCA" }, Members("voice-2").Intersect(new[] { "ADSR #2", "Octave", "VCA" }).Order());
        Assert.Equal(new[] { "Audio 2", "VCA Mix" }, Members("bus").Order());
    }

    [Fact]
    public void VelocityStaysCvThroughAnOctaveShifter()
    {
        PatchAnalysis zones = Fixtures.Zones;
        string Title(long id) => zones.Module(id).Title;

        Assert.Equal(SignalType.Cv, zones.Cables.Single(c => c.FromPort == "Velocity Zone B").Signal);
        Assert.Equal(SignalType.Cv, zones.Cables.Single(c => Title(c.Cable.From.ModuleId) == "Octave").Signal);
    }

    [Fact]
    public void AMixerFedByASingleVoiceStaysOnTheBus()
    {
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Core", "model": "MIDIToCVInterface", "pos": [0, 0] },
                { "id": 2, "plugin": "Fundamental", "model": "VCO", "pos": [10, 0] },
                { "id": 3, "plugin": "Fundamental", "model": "VCF", "pos": [20, 0] },
                { "id": 4, "plugin": "Fundamental", "model": "Mixer", "pos": [30, 0] },
                { "id": 5, "plugin": "Core", "model": "AudioInterface2", "pos": [40, 0] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 0, "inputModuleId": 2, "inputId": 0, "color": "#ffd452" },
                { "id": 2, "outputModuleId": 2, "outputId": 2, "inputModuleId": 3, "inputId": 3, "color": "#ff5252" },
                { "id": 3, "outputModuleId": 3, "outputId": 0, "inputModuleId": 4, "inputId": 0, "color": "#ff5252" },
                { "id": 4, "outputModuleId": 4, "outputId": 0, "inputModuleId": 5, "inputId": 0, "color": "#ff5252" }
              ]
            }
            """;

        PatchAnalysis single = PatchDiagram.Analyze(Encoding.UTF8.GetBytes(json));

        Assert.Equal(Band.Bus, single.Modules.Single(m => m.Module.Model == "Mixer").Band);
        Assert.Equal(Band.Voice, single.Modules.Single(m => m.Module.Model == "VCF").Band);
    }

    [Fact]
    public void ASwitchTakesTheRoleOfWhatItRoutes()
    {
        PatchAnalysis sub37 = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("Subsequent37.vcv")));
        AnalyzedModule Module(string title) => sub37.Modules.Single(m => m.Title == title);

        // Picks the octave of a voice's pitch: a pitch processor.
        Assert.Equal(Role.Pitch, Module("Sequential Switch 4 to 1 #1").Role);
        // Mutes the sub oscillator: part of its voice, and what comes out of it is still audio.
        Assert.Equal(Role.Modifier, Module("Sequential Switch 4 to 1 #2").Role);
        Assert.Equal(SignalType.Audio, sub37.Cables.Single(c => c.FromPort == "Square").Signal);
        Assert.Equal(SignalType.Audio, sub37.Cables.Single(c => sub37.Module(c.Cable.From.ModuleId).Title == "Sequential Switch 4 to 1 #2").Signal);
    }

    [Fact]
    public void AnOscillatorAndItsMutableSubMakeOneVoice()
    {
        PatchAnalysis sub37 = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("Subsequent37.vcv")));
        Voice zoneA = sub37.Voices.Single(v => v.PitchOrigin == "Zones › Pitch Zone A");

        Assert.Equal(
            new[] { "Mix #2", "Sequential Switch 4 to 1 #2", "VCA #3", "VCO #2", "VCO #3" },
            zoneA.ModuleIds.Select(id => sub37.Module(id).Title).Order());
    }

    [Fact]
    public void EveryVoiceHasItsOwnLaneFoldedOrNot()
    {
        List<string> VoiceLanes(IReadOnlySet<string>? unfold) => PatchDiagram.Layout(analysis, "t", unfold).Bands
            .Where(b => b.Band == Band.Voice).OrderBy(b => b.Y).Select(b => b.Group ?? b.Title).ToList();

        // Unfolding a voice opens it in its own lane, where it already stood.
        Assert.Equal(new[] { "Voice 1", "Voice 2" }, VoiceLanes(null));
        Assert.Equal(new[] { "voice-1", "Voice 2" }, VoiceLanes(new HashSet<string> { "voice-1" }));
        Assert.Equal(new[] { "Voice 1", "voice-2" }, VoiceLanes(new HashSet<string> { "voice-2" }));
    }

}