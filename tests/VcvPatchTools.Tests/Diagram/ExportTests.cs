using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;

namespace VcvPatchTools.Tests;

/// <summary>
/// Golden files in tests/golden. After an intended change, regenerate them with
/// UPDATE_GOLDEN=1 dotnet test, then review the diff before keeping it.
/// </summary>
public class ExportTests
{
    private static readonly PatchAnalysis analysis = Fixtures.AmbientJam;
    private static readonly DiagramLayout layout = PatchDiagram.Layout(analysis, "Ambient Jam", PatchDiagram.GroupKeys(analysis));
    private static readonly DiagramLayout overview = PatchDiagram.Layout(analysis, "Ambient Jam");

    [Theory]
    [InlineData(DiagramFormat.Dot, false)]
    [InlineData(DiagramFormat.Mermaid, false)]
    [InlineData(DiagramFormat.Svg, false)]
    [InlineData(DiagramFormat.Dot, true)]
    [InlineData(DiagramFormat.Mermaid, true)]
    [InlineData(DiagramFormat.Svg, true)]
    public void MatchesGoldenFile(DiagramFormat format, bool folded)
    {
        string actual = PatchDiagram.Export(folded ? overview : layout, format).ReplaceLineEndings("\n");
        string goldenPath = Path.Combine(GoldenDir(), (folded ? "AmbientJam.overview" : "AmbientJam") + PatchDiagram.Extension(format));

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(goldenPath, actual);
        }

        Assert.True(File.Exists(goldenPath), $"Missing golden file {goldenPath}: run UPDATE_GOLDEN=1 dotnet test");
        Assert.Equal(File.ReadAllText(goldenPath).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void HtmlIsSelfContained()
    {
        string html = PatchDiagram.Export(layout, DiagramFormat.Html);

        Assert.Contains("<svg", html);
        Assert.Contains("data-layer-toggle", html);
        Assert.DoesNotContain("<script src", html);
        Assert.DoesNotContain("<link", html);
    }

    [Fact]
    public void PagesDeclareTheirOwnDarkThemeSoPhonesDoNotForceTheirs()
    {
        string html = PatchDiagram.Export(layout, DiagramFormat.Html);

        Assert.Contains("<meta name=\"color-scheme\" content=\"light dark\">", html);
        Assert.Contains("color-scheme: light dark;", PatchDiagram.Export(layout, DiagramFormat.Svg));
    }

    [Fact]
    public void LabelsAreDrawnOverCablesAndBoxes()
    {
        string svg = PatchDiagram.Export(layout, DiagramFormat.Svg);
        int edges = svg.IndexOf("<g class=\"vpd-edges\">", StringComparison.Ordinal);
        int nodes = svg.IndexOf("<g class=\"vpd-nodes\">", StringComparison.Ordinal);
        int labels = svg.IndexOf("<g class=\"vpd-labels\">", StringComparison.Ordinal);

        Assert.True(edges < nodes && nodes < labels);
        Assert.DoesNotContain("class=\"label\"", svg[edges..labels]);
        Assert.Equal(layout.Edges.Count(e => e.Intent.Length > 0), svg[labels..].Split("class=\"label\"").Length - 1);
    }

    [Theory]
    [InlineData("AmbientJam.vcv")]
    [InlineData("MeditationsOnDeath.vcv")]
    [InlineData("NotBoringDrone3.vcv")]
    [InlineData("Solar42f16.vcv")]
    public void LabelsDoNotCoverEachOther(string fixture)
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path(fixture)));
        DiagramLayout unfolded = PatchDiagram.Layout(patch, fixture, PatchDiagram.GroupKeys(patch));
        List<(string Text, (double Left, double Top, double Right, double Bottom) Box)> labels = unfolded.Edges
            .Where(e => e.Intent.Length > 0 && !e.IsFeedback)
            .Select(e => (e.Label, LabelPlacement.Bounds(e.Label, e.LabelX, e.LabelY)))
            .ToList();

        List<string> overlaps = labels.SelectMany((a, i) => labels.Skip(i + 1)
                .Where(b => a.Box.Left < b.Box.Right && b.Box.Left < a.Box.Right && a.Box.Top < b.Box.Bottom && b.Box.Top < a.Box.Bottom)
                .Select(b => $"'{a.Text}' / '{b.Text}'"))
            .ToList();

        Assert.Empty(overlaps);
    }

    [Fact]
    public void AStandaloneSvgCarriesItsLegend()
    {
        string svg = PatchDiagram.Export(overview, DiagramFormat.Svg);
        string html = PatchDiagram.Export(overview, DiagramFormat.Html);

        Assert.Contains("class=\"vpd-legend\"", svg);
        Assert.Contains(">gate / trig / clock</text>", svg);
        Assert.Contains(">performance</text>", svg);
        Assert.DoesNotContain(">monitor</text>", svg);
        Assert.DoesNotContain("vpd-legend\"", html);
    }

    [Theory]
    [InlineData("1V/octave pitch", SignalType.Pitch, "V/oct")]
    [InlineData("Filter R cutoff CV (sums onto Filter L instead when Link is on)", SignalType.Cv, "Filter R…")]
    [InlineData("Gate 13 · A2", SignalType.Gate, "A2")]
    [InlineData("Cell 2 · CC 74 (cutoff)", SignalType.Cv, "CC 74")]
    [InlineData("Mod 3 → Frequency +40%, Pre-Filter Gain +7%", SignalType.Cv, "Mod 3")]
    [InlineData("Mix Pan › Pan CV 2", SignalType.Cv, "Pan CV 2")]
    [InlineData("Left", SignalType.Audio, "L")]
    public void PortTabsShowShortNames(string port, SignalType signal, string expected)
    {
        Assert.Equal(expected, PortTabs.Short(port, signal));
    }

    [Theory]
    [InlineData(new[] { "Mix Aux Send › Left send", "Mix Aux Send › Right send" }, "L/R send")]
    [InlineData(new[] { "Right Mix", "Left Mix" }, "L/R Mix")]
    [InlineData(new[] { "Left channel 2", "Right channel 2" }, "L/R Ch 2")]
    [InlineData(new[] { "Right", "Left" }, "L/R")]
    [InlineData(new[] { "Left", "Right channel 2" }, "L, R Ch 2")]
    [InlineData(new[] { "Left channel 1", "Right channel 1", "Left channel 2", "Right channel 2" }, "4 × audio")]
    public void StereoPairsShareOneTabName(string[] ports, string expected) => Assert.Equal(expected, PortTabs.Text(ports, SignalType.Audio));

    [Fact]
    public void CablesPlugIntoNamedTabs()
    {
        DiagramNode vcf = layout.Nodes.Single(n => n.Title == "VCF #1");
        DiagramEdge fromPulse = layout.Edges.Single(e => e.FromTitle == "PULSE" && e.ToTitle == "VCF #1");
        PortTab audioIn = vcf.Tabs.Single(t => !t.Output && t.Signal == SignalType.Audio);

        Assert.Equal("L", audioIn.Text);
        Assert.EndsWith($"H {vcf.X - audioIn.Width - LayeredLayout.ArrowLength:0.#}", fromPulse.Path);
        Assert.Contains(layout.Nodes.Single(n => n.Title == "Host MIDI").Tabs, t => t.Output && t.Text == "V/oct");
    }

    [Fact]
    public void LibraryLinksAreBuiltFromPluginAndModelSlugs()
    {
        Assert.Equal("https://library.vcvrack.com/AmbientModules/LunarFilter", VcvLibrary.PageUrl("AmbientModules/LunarFilter"));
        Assert.Equal("https://library.vcvrack.com/screenshots/200/Fundamental/VCF.webp", VcvLibrary.ScreenshotUrl("Fundamental/VCF", 200));
        Assert.Equal("https://library.vcvrack.com/Some%20Plugin/A%26B", VcvLibrary.PageUrl("Some Plugin/A&B"));
        Assert.False(VcvLibrary.IsListed("Cardinal"));
        Assert.True(VcvLibrary.IsListed("Fundamental"));
    }

    [Fact]
    public void ModuleBoxesLinkToTheirLibraryPage()
    {
        DiagramNode vcf = layout.Nodes.Single(n => n.Title == "VCF #1");
        string svg = PatchDiagram.Export(layout, DiagramFormat.Svg);

        Assert.Equal("SurgeXTRack/SurgeXTVCF", vcf.LibraryKey);
        Assert.Contains("<a class=\"lib\" href=\"https://library.vcvrack.com/SurgeXTRack/SurgeXTVCF\"", svg);
        // Cardinal-only modules have no page, folded groups are no single module.
        Assert.Null(layout.Nodes.Single(n => n.Title == "Host MIDI").LibraryKey);
        Assert.All(overview.Nodes.Where(n => n.IsFolded), n => Assert.Null(n.LibraryKey));
        Assert.All(PatchDiagram.Layout(analysis, "Ambient Jam", PatchDiagram.GroupKeys(analysis), libraryLinks: false).Nodes, n => Assert.Null(n.LibraryKey));
    }

    [Fact]
    public void FoldedGroupsPreviewEachModuleOnceWithItsCount()
    {
        foreach (DiagramNode group in overview.Nodes.Where(n => n.IsFolded))
        {
            Assert.Equal(group.LibraryPanels.Select(p => p.Key).Distinct().Count(), group.LibraryPanels.Count);
            Assert.Equal(group.Details.Count, group.LibraryPanels.Sum(p => p.Count) + group.UnlistedCount);
        }
        DiagramNode voice = overview.Nodes.Single(n => n.IsFolded && n.LibraryPanels.Any(p => p.Key == "Fundamental/ADSR"));
        string svg = PatchDiagram.Export(overview, DiagramFormat.Svg);

        Assert.Contains(new LibraryPanel("Fundamental/ADSR", 2), voice.LibraryPanels);
        Assert.Contains("Fundamental/ADSR*2", svg);
        // Host MIDI and friends are Cardinal-only: counted, not previewed.
        Assert.Contains(overview.Nodes, n => n.IsFolded && n.UnlistedCount > 0);
        Assert.DoesNotContain(overview.Nodes, n => n.IsFolded && n.LibraryKey is not null);
    }

    [Fact]
    public void PanelPreviewsAreOnlyFetchedOnHover()
    {
        string html = PatchDiagram.Export(layout, DiagramFormat.Html);
        string markup = html[..html.IndexOf("<script>", StringComparison.Ordinal)];

        Assert.Contains("data-library=\"SurgeXTRack/SurgeXTVCF\"", markup);
        Assert.DoesNotContain("screenshots/", markup);
        Assert.Contains("vpdLibraryPreview", html);
    }

    [Fact]
    public void CablesFromOnePortLeaveAsOneTrunk()
    {
        List<DiagramEdge> pitch = layout.Edges.Where(e => e.FromTitle == "Host MIDI" && e.Signal == SignalType.Pitch).ToList();
        string Start(DiagramEdge e) => string.Join(" ", e.Path.Split(' ').Take(3));

        Assert.True(pitch.Count >= 2);
        Assert.Single(pitch.Select(Start).Distinct());
        Assert.Single(layout.Nodes.Single(n => n.Title == "Host MIDI").Tabs, t => t.Output && t.Signal == SignalType.Pitch);
    }

    [Fact]
    public void ASendReturnLoopHasItsOwnTabsOnItsHost()
    {
        // Venom's MixSend expander, docked on the mixer: its send and return ports have the same ids as the mixer's own.
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Fundamental", "model": "VCO", "pos": [0, 0] },
                { "id": 2, "plugin": "Venom", "model": "Mix4Stereo", "pos": [10, 0], "rightModuleId": 3 },
                { "id": 3, "plugin": "Venom", "model": "MixSend", "pos": [20, 0], "leftModuleId": 2, "rightModuleId": 4 },
                { "id": 4, "plugin": "Valley", "model": "Plateau", "pos": [25, 0], "leftModuleId": 3 },
                { "id": 5, "plugin": "Cardinal", "model": "HostAudio2", "pos": [40, 0] }
              ],
              "cables": [
                { "id": 1, "outputModuleId": 1, "outputId": 2, "inputModuleId": 2, "inputId": 0, "color": "#ff5252" },
                { "id": 2, "outputModuleId": 2, "outputId": 0, "inputModuleId": 5, "inputId": 0, "color": "#ff5252" },
                { "id": 3, "outputModuleId": 3, "outputId": 0, "inputModuleId": 4, "inputId": 0, "color": "#ff5252" },
                { "id": 4, "outputModuleId": 4, "outputId": 0, "inputModuleId": 3, "inputId": 0, "color": "#ff5252" }
              ]
            }
            """;
        PatchAnalysis patch = PatchDiagram.Analyze(System.Text.Encoding.UTF8.GetBytes(json));
        DiagramLayout loop = PatchDiagram.Layout(patch, "Send", PatchDiagram.GroupKeys(patch));
        DiagramNode mixer = loop.Nodes.Single(n => n.ModuleId == 2);
        DiagramEdge send = loop.Edges.Single(e => e.ToTitle == "Plateau");
        DiagramEdge back = loop.Edges.Single(e => e.FromTitle == "Plateau");

        PortTab mix = mixer.Tabs.Single(t => t.Output && t.Tooltip == "Left Mix");
        PortTab sendTab = mixer.Tabs.Single(t => t.Output && t.Tooltip == "Mix Aux Send › Left send");
        PortTab returnTab = mixer.Tabs.Single(t => !t.Output && t.Tooltip == "Mix Aux Send › Left return");
        Assert.NotEqual(mix.Y, sendTab.Y);
        Assert.StartsWith($"M {mixer.X + mixer.Width + sendTab.Width:0.#} {sendTab.Y:0.#} ", send.Path);
        Assert.EndsWith($"{returnTab.Y:0.#} H {mixer.X - returnTab.Width - LayeredLayout.ArrowLength:0.#}", back.Path);
        // Square, like every other cable.
        Assert.DoesNotContain(" C ", send.Path + back.Path);
    }

    [Fact]
    public void CustomIntentReplacesSuggestion()
    {
        long cableId = layout.Edges.First(e => e.FromTitle == "LLFO").CableIds.Single();
        DiagramLayout custom = PatchDiagram.Layout(analysis, "Ambient Jam", PatchDiagram.GroupKeys(analysis), new Dictionary<long, string> { [cableId] = "the LFO makes the pulse width breathe" });

        Assert.Equal("the LFO makes the pulse width breathe", custom.Edges.Single(e => e.CableIds.Contains(cableId)).Intent);
        Assert.Contains("the LFO makes the pulse width breathe", PatchDiagram.Export(custom, DiagramFormat.Mermaid));
    }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "golden");

    [Fact]
    public void OverviewShowsOneBoxPerGroupAndMergesCables()
    {
        Assert.Equal(overview.Groups.Count, overview.Nodes.Count);
        Assert.All(overview.Nodes, n => Assert.True(n.IsFolded));
        Assert.Equal(overview.Edges.Count, overview.Edges.Select(e => (e.From, e.To, e.Signal)).Distinct().Count());
        Assert.Contains(overview.Edges, e => !e.IsSingleCable);
    }

    [Fact]
    public void UnfoldingOneGroupOnlyDetailsThatGroup()
    {
        DiagramLayout oneVoice = PatchDiagram.Layout(analysis, "Ambient Jam", new HashSet<string> { "voice-2" });

        Assert.Contains(oneVoice.Nodes, n => n.Title == "PULSE" && !n.IsFolded);
        Assert.DoesNotContain(oneVoice.Nodes, n => n.Title == "LVCO");
        Assert.Single(oneVoice.Bands, b => b.Group == "voice-2");
    }

    [Theory]
    [InlineData("AmbientJam.vcv", false)]
    [InlineData("AmbientJam.vcv", true)]
    [InlineData("MeditationsOnDeathRack.vcv", false)]
    [InlineData("MeditationsOnDeathRack.vcv", true)]
    [InlineData("Solar42f16.vcv", false)]
    [InlineData("Solar42f16.vcv", true)]
    [InlineData("NotBoringDrone3.vcv", false)]
    [InlineData("NotBoringDrone3.vcv", true)]
    [InlineData("FeedbackTest.vcv", false)]
    [InlineData("FeedbackTest.vcv", true)]
    public void HorizontalRunsNeverGoThroughAnotherBox(string fixture, bool unfoldAll)
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path(fixture)));
        DiagramLayout diagram = PatchDiagram.Layout(patch, fixture, unfoldAll ? PatchDiagram.GroupKeys(patch) : null);
        foreach (DiagramEdge edge in diagram.Edges.Where(e => e.Path.Contains(" H ", StringComparison.Ordinal)))
        {
            foreach ((double y, double fromX, double toX) in HorizontalRuns(edge.Path))
            {
                DiagramNode? crossed = diagram.Nodes.FirstOrDefault(n => n.Key != edge.From && n.Key != edge.To
                    && y > n.Y && y < n.Y + n.Height && n.X < Math.Max(fromX, toX) && n.X + n.Width > Math.Min(fromX, toX));
                Assert.True(crossed is null, $"{edge.FromTitle} → {edge.ToTitle} runs through {crossed?.Title}");
            }
        }
    }

    [Theory]
    [InlineData("AmbientJam.vcv", false)]
    [InlineData("AmbientJam.vcv", true)]
    [InlineData("MeditationsOnDeathRack.vcv", true)]
    [InlineData("Solar42f16.vcv", true)]
    [InlineData("NotBoringDrone3.vcv", false)]
    public void ArrowsIntoABoxKeepRoomBetweenThem(string fixture, bool unfoldAll)
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path(fixture)));
        DiagramLayout diagram = PatchDiagram.Layout(patch, fixture, unfoldAll ? PatchDiagram.GroupKeys(patch) : null);
        foreach (IGrouping<string, DiagramEdge> target in diagram.Edges.Where(e => e.Path.Contains(" H ", StringComparison.Ordinal)).GroupBy(e => e.To))
        {
            List<double> arrows = target.Select(e => HorizontalRuns(e.Path).Last().Y).Order().ToList();
            for (int i = 1; i < arrows.Count; i++)
            {
                Assert.True(arrows[i] - arrows[i - 1] >= 12, $"arrows into {diagram.Node(target.Key).Title} are {arrows[i] - arrows[i - 1]:0.#} px apart");
            }
        }
    }

    [Fact]
    public void FeedbackCablesTurnBackToTheirTarget()
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path("FeedbackTest.vcv")));
        DiagramLayout diagram = PatchDiagram.Layout(patch, "FeedbackTest", PatchDiagram.GroupKeys(patch));

        List<DiagramEdge> feedback = diagram.Edges.Where(e => e.IsFeedback).ToList();
        Assert.Equal(2, feedback.Count);
        foreach (DiagramEdge edge in feedback)
        {
            // Out on the right of its source, back leftwards, in on the left of its target.
            List<(double Y, double FromX, double ToX)> runs = HorizontalRuns(edge.Path).ToList();
            Assert.True(runs[0].ToX > runs[0].FromX);
            Assert.Contains(runs, r => r.ToX < r.FromX);
            Assert.True(runs[^1].ToX > runs[^1].FromX);
            Assert.True(runs[^1].ToX < diagram.Node(edge.To).X);
            // Straight back: just the two U-turns, no jog in between.
            Assert.Equal(2, VerticalChannels(edge.Path).Count());
        }
    }

    [Theory]
    [InlineData("AmbientJam.vcv")]
    [InlineData("MeditationsOnDeathRack.vcv")]
    [InlineData("Solar42f16.vcv")]
    [InlineData("NotBoringDrone3.vcv")]
    [InlineData("FeedbackTest.vcv")]
    public void OnlyGroupsOfSeveralModulesAreFolded(string fixture)
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path(fixture)));
        DiagramLayout overview = PatchDiagram.Layout(patch, fixture, null);

        Assert.All(overview.Nodes.Where(n => n.IsFolded), n => Assert.True(overview.Groups.Single(g => g.Key == n.Group).Members.Count > 1, $"{n.Title} folds a single module"));
        foreach (PatchGroup single in overview.Groups.Where(g => g.Members.Count == 1))
        {
            DiagramNode node = overview.Node(LayeredLayout.ModuleKey(single.Members[0]));
            Assert.False(node.IsFolded);
            // Still in its band's shared lane, not in a lane of its own.
            Assert.Contains(overview.Bands, b => b.Group is null && b.Band == single.Band && node.Y > b.Y && node.Y < b.Y + b.Height);
        }
    }

    [Fact]
    public void BarycenterUntanglesCrossedLinks()
    {
        List<List<string>> columns = new List<List<string>> { new List<string> { "a", "b" }, new List<string> { "x", "y" } };
        List<(string U, string V)> segments = new List<(string U, string V)> { ("a", "y"), ("b", "x") };
        Assert.Equal(1, Barycenter.Crossings(columns, segments));

        List<List<string>> ordered = Barycenter.Order(columns, segments, _ => 0);

        Assert.Equal(0, Barycenter.Crossings(ordered, segments));
    }

    [Theory]
    [InlineData("NotBoringDrone3.vcv")]
    [InlineData("Solar42f16.vcv")]
    [InlineData("MeditationsOnDeathRack.vcv")]
    public void BusyGapsWidenSoChannelsKeepApart(string fixture)
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path(fixture)));
        DiagramLayout diagram = PatchDiagram.Layout(patch, fixture, PatchDiagram.GroupKeys(patch));
        List<double> channels = diagram.Edges.SelectMany(e => VerticalChannels(e.Path)).Distinct().Order().ToList();
        for (int i = 1; i < channels.Count; i++)
        {
            Assert.True(channels[i] - channels[i - 1] >= 8, $"channels at x={channels[i - 1]} and x={channels[i]} are too close");
        }
    }

    [Theory]
    [InlineData("Subsequent37.vcv", "external", MonitorView.Modules)]
    [InlineData("Subsequent37.vcv", null, MonitorView.Hidden)]
    [InlineData("AmbientJam.vcv", null, MonitorView.Hidden)]
    [InlineData("Zones.vcv", null, MonitorView.Modules)]
    [InlineData("NotBoringDrone3.vcv", null, MonitorView.Modules)]
    [InlineData("MeditationsOnDeathRack.vcv", null, MonitorView.Hidden)]
    public void CablesDontRunAlongsideEachOther(string fixture, string? unfold, MonitorView monitors)
    {
        PatchAnalysis patch = PatchDiagram.Analyze(File.ReadAllBytes(Fixtures.Path(fixture)));
        IReadOnlySet<string> unfolded = unfold is null ? PatchDiagram.GroupKeys(patch) : new HashSet<string> { unfold };
        DiagramLayout diagram = PatchDiagram.Layout(patch, fixture, unfolded, monitors: monitors);
        List<(string Edge, double Y, double FromX, double ToX)> runs = diagram.Edges
            .SelectMany(e => HorizontalRuns(e.Path).Select(r => (e.Key, r.Y, Math.Min(r.FromX, r.ToX), Math.Max(r.FromX, r.ToX))))
            .ToList();

        // Two cables a few pixels apart over some length read as one: either they share a height, or they keep apart.
        foreach ((string Edge, double Y, double FromX, double ToX) a in runs)
        {
            foreach ((string Edge, double Y, double FromX, double ToX) b in runs.Where(b => b.Edge != a.Edge))
            {
                double gap = Math.Abs(a.Y - b.Y);
                double alongside = Math.Min(a.ToX, b.ToX) - Math.Max(a.FromX, b.FromX);
                Assert.False(gap is > 0.5 and < 8 && alongside > 12, $"{a.Edge} at y={a.Y} and {b.Edge} at y={b.Y} run alongside over {alongside:0}px");
            }
        }
    }

    /// <summary>x of every vertical run ("… Q x y x y V y …") of a path.</summary>
    private static IEnumerable<double> VerticalChannels(string path)
    {
        string[] tokens = path.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 5 < tokens.Length; i++)
        {
            if (tokens[i] == "Q" && tokens[i + 5] == "V")
            {
                yield return Parse(tokens[i + 1]);
            }
        }
    }

    /// <summary>Horizontal segments of a "M x y H x … Q … x y H x" path.</summary>
    private static IEnumerable<(double Y, double FromX, double ToX)> HorizontalRuns(string path)
    {
        string[] tokens = path.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double x = 0;
        double y = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i])
            {
                case "M":
                    x = Parse(tokens[++i]);
                    y = Parse(tokens[++i]);
                    break;
                case "H":
                    double nx = Parse(tokens[++i]);
                    yield return (y, x, nx);
                    x = nx;
                    break;
                case "V":
                    y = Parse(tokens[++i]);
                    break;
                case "Q":
                    i += 2;
                    x = Parse(tokens[++i]);
                    y = Parse(tokens[++i]);
                    break;
            }
        }
    }

    private static double Parse(string text) => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
}