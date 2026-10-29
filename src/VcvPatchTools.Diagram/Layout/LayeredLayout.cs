using System.Globalization;
using VcvPatchTools.Diagram.Analysis;
using EdgeKey = (string From, string To, VcvPatchTools.Diagram.Analysis.SignalType Signal);

namespace VcvPatchTools.Diagram.Layout;

/// <summary>
/// Layered drawing after the Sugiyama framework (Sugiyama, Tagawa &amp; Toda, 1981), the method behind Graphviz dot
/// and ELK Layered, adapted to horizontal bands (external control, time, pitch, modulation, voices, mix &amp; effects):
/// <list type="number">
/// <item>Layering: each box gets a column by longest signal path, so the patch reads left to right.</item>
/// <item>Virtual nodes: a cable spanning several columns gets a thin "track" in every column it passes, in its
/// source's band, like a metro line. Its horizontal runs then never go through a box, by construction.</item>
/// <item>Crossing reduction: within each band, the order of boxes and tracks in a column follows the
/// barycenter heuristic (see <see cref="Barycenter"/>).</item>
/// <item>Coordinates: boxes stack from the top of their band; a box is as tall as its busiest side needs.</item>
/// <item>Orthogonal routing: every bend happens in the free gap between two columns, each cable in its own
/// vertical channel, ordered so cables of one gap don't cross (as in ELK's orthogonal edge router).
/// A gap is as wide as its channels need, so busy gaps widen and quiet ones stay compact.</item>
/// </list>
/// Folded groups are one box each, sharing their band's lane (a voice has a lane of its own); an unfolded group gets its own lane with its
/// modules (inserts right under their host). Cables between the same two boxes with the same signal merge.
/// Scopes and displays, when drawn as boxes, sit in one more column on the right, out of the flow: each as close as
/// it can be to the height of what it watches, its cables running there on tracks like any other.
/// </summary>
public static class LayeredLayout
{
    public const double NodeWidth = 170;
    public const double NodeHeight = 46;
    private const double minGap = 130;
    private const double channelPitch = 12;
    private const double gapPadding = 32;
    private const double rowSpacing = 16;
    private const double trackSpacing = 16;
    // Closer than this, two parallel cables read as one.
    private const double minRunSpacing = 8;
    private const double portSpacing = 14;

    /// <summary>Length of the arrowhead drawn past the end of every cable (renderers draw it from the line's end), so thick lines never show past its tip.</summary>
    public const double ArrowLength = 11;
    private const double bandHeaderHeight = 30;
    private const double bandPadding = 12;
    private const double margin = 24;

    /// <param name="unfolded">Keys of the unfolded groups; null or empty shows the overview, everything folded.</param>
    /// <param name="monitors">
    /// Scopes and displays explain nothing about the sound and can tap the flow anywhere: hidden by default, they show
    /// as a badge on the box whose signal they watch, or as boxes in a column on the right.
    /// </param>
    /// <param name="functionNames">Title module boxes by what they do ("FILTER #1"), the module's own name going under it.</param>
    /// <param name="portTabs">Name the ports on the box edges (see <see cref="PortTabs"/>).</param>
    /// <param name="libraryLinks">Link module boxes to their VCV Library page (see <see cref="Core.Catalog.VcvLibrary"/>).</param>
    public static DiagramLayout Build(PatchAnalysis analysis, string title, IReadOnlySet<string>? unfolded, IReadOnlyDictionary<long, string>? intents = null, MonitorView monitors = MonitorView.Hidden, bool functionNames = false, bool portTabs = true, bool libraryLinks = true)
    {
        Folding folding = new Folding(analysis, unfolded, monitors);
        Stacks stacks = StackBoxes(analysis, folding);
        List<IGrouping<EdgeKey, AnalyzedCable>> merged = MergeCables(folding, stacks);
        Columns columns = AssignColumns(stacks, merged);
        List<Lane> lanes = AssignLanes(folding, stacks.Boxes);
        Dictionary<string, int> laneOf = LaneOf(lanes);
        Routing routing = PlanTracks(merged, stacks, columns, laneOf);
        List<List<string>> order = Barycenter.Order(InitialOrder(stacks.Boxes, columns, routing.TrackColumn), routing.Segments, e => laneOf[e]);

        // Cables leaving from the same output port(s) form a trunk: one exit on the box, one first turn, then they branch.
        // A docked expander's cables leave from its base with the expander's port ids: its port name tells them apart.
        Dictionary<EdgeKey, string> trunkOf = merged.ToDictionary(
            g => g.Key,
            g => $"{g.Key.From}|{g.Key.Signal}|" + string.Join(",", g.Select(c => $"{c.Cable.From.ModuleId}:{c.Cable.From.PortId}:{c.FromPort}").Distinct().Order(StringComparer.Ordinal)));
        Dictionary<string, double> heightOf = BoxHeights(stacks.AnchorOf.Keys, merged, trunkOf);
        Placement placement = PlaceRows(lanes, laneOf, order, routing, stacks, columns, heightOf, merged);
        Ports ports = PlacePorts(merged, trunkOf, routing.Plans, placement);
        List<Turn> turns = FindTurns(routing.Plans, ports.Levels);
        turns.AddRange(SideLoopTurns(merged, routing.Plans, ports, placement.Positions));
        Tabs tabs = new Tabs(merged, routing.Plans, ports, portTabs, placement.Positions);
        Gaps gaps = new Gaps(turns, tabs, columns.Drawn, turn => turn.Part == 0 && !turn.Feedback ? trunkOf[turn.Key] : null);

        List<DiagramBand> bands = placement.Bands;
        // The watchers' column, clear of their port tabs; the horizontal bands stop short of it.
        if (stacks.Watchers.Count > 0)
        {
            PatchGroup watchersGroup = folding.Groups.First(g => g.Band == Band.Monitor);
            double left = gaps.ColumnX[columns.Count] - tabs.InWidth(columns.Count) - 12;
            bands.Add(new DiagramBand(Band.Monitor, folding.HasOwnLane(watchersGroup) ? watchersGroup.Key : null, PatchGrouping.BoxTitle(Band.Monitor), margin, placement.WatchersBottom - margin)
            {
                X = left,
                Width = gaps.ColumnX[columns.Count] + NodeWidth + 16 - left,
            });
        }

        List<DiagramNode> nodes = MakeNodes(analysis, folding, placement, gaps.ColumnX, tabs, monitors, functionNames, libraryLinks);
        Dictionary<string, DiagramNode> nodeByKey = nodes.ToDictionary(n => n.Key);
        Dictionary<string, List<LabelPlacement.Run>> runs = new Dictionary<string, List<LabelPlacement.Run>>();
        List<DiagramEdge> edges = DrawEdges(analysis, merged, nodeByKey, routing.Plans, ports.Levels, ports.OutY, ports.InY, gaps.Channels, intents, runs, tabs.OutTabWidth, tabs.InTabWidth);
        edges = LabelPlacement.Place(edges, runs, nodes);
        double width = gaps.Right(columns.Drawn - 1) + margin;
        return new DiagramLayout(title, width, placement.Y - 8 + margin, bands, nodes, edges, folding.Groups, analysis.Diagnostics);
    }

    /// <summary>
    /// What is drawn and how: the groups shown (scopes and displays only as boxes), and which of them show their
    /// modules. Folded groups are one box each.
    /// </summary>
    private sealed class Folding
    {
        private readonly IReadOnlySet<string>? unfolded;
        private readonly Dictionary<long, string> boxOf;

        public Folding(PatchAnalysis analysis, IReadOnlySet<string>? unfolded, MonitorView monitors)
        {
            this.unfolded = unfolded;
            Groups = PatchGrouping.Build(analysis).Where(g => monitors == MonitorView.Modules || g.Band != Band.Monitor).ToList();
            Visible = Groups.SelectMany(g => g.Members).ToHashSet();
            Cables = analysis.Cables.Where(c => Visible.Contains(c.Cable.From.ModuleId) && Visible.Contains(c.Cable.To.ModuleId)).ToList();
            GroupOf = Groups.SelectMany(g => g.Members.Select(id => (id, g))).ToDictionary(p => p.id, p => p.g);
            // Visible box of each module: itself if its group is unfolded, the group's box otherwise.
            boxOf = GroupOf.ToDictionary(kv => kv.Key, kv => ShowsModules(kv.Value) ? ModuleKey(kv.Key) : GroupKey(kv.Value.Key));
        }

        public IReadOnlyList<PatchGroup> Groups { get; }

        public HashSet<long> Visible { get; }

        public List<AnalyzedCable> Cables { get; }

        public Dictionary<long, PatchGroup> GroupOf { get; }

        public bool IsUnfolded(PatchGroup group) => unfolded is not null && unfolded.Contains(group.Key);

        /// <summary>A group of one module is never folded: a folded box promises more than it holds. It stays in its band's lane.</summary>
        public bool ShowsModules(PatchGroup group) => IsUnfolded(group) || group.Members.Count == 1;

        public bool HasOwnLane(PatchGroup group) => IsUnfolded(group) && group.Members.Count > 1;

        public string BoxOf(long moduleId) => boxOf[moduleId];

        /// <summary>The one box of a group that has no lane of its own: its module, or the folded group.</summary>
        public string SingleBox(PatchGroup group) => ShowsModules(group) ? ModuleKey(group.Members[0]) : GroupKey(group.Key);
    }

    /// <param name="Boxes">Boxes in the flow, in the groups' order.</param>
    /// <param name="Watchers">Scopes and displays drawn as boxes, out of the flow.</param>
    /// <param name="StackOf">Each placed box and the inserts stacked right under it, which move as one block.</param>
    /// <param name="AnchorOf">The placed box each box moves with (itself, or the host of an insert).</param>
    private sealed record Stacks(List<string> Boxes, List<string> Watchers, HashSet<string> Watching, Dictionary<string, List<string>> StackOf, Dictionary<string, string> AnchorOf);

    private static Stacks StackBoxes(PatchAnalysis analysis, Folding folding)
    {
        // Boxes in the flow, and the scopes and displays (watchers), out of it.
        List<string> boxes = new List<string>();
        List<string> watchers = new List<string>();
        foreach (PatchGroup group in folding.Groups)
        {
            if (group.Band == Band.Monitor)
            {
                watchers.AddRange(folding.ShowsModules(group) ? group.Members.Select(ModuleKey) : new[] { GroupKey(group.Key) });
            }
            else if (folding.ShowsModules(group))
            {
                boxes.AddRange(group.Members.Where(id => analysis.Module(id).InsertOf is null || !group.Members.Contains(analysis.Module(id).InsertOf!.Value)).Select(ModuleKey));
            }
            else
            {
                boxes.Add(GroupKey(group.Key));
            }
        }

        // A placed box and the inserts stacked right under it move as one block.
        HashSet<string> placed = boxes.ToHashSet();
        ILookup<long, AnalyzedModule> insertsOf = analysis.Modules.Where(m => m.InsertOf is not null).ToLookup(m => m.InsertOf!.Value);
        Dictionary<string, List<string>> stackOf = new Dictionary<string, List<string>>();
        Dictionary<string, string> anchorOf = new Dictionary<string, string>();
        foreach (string box in boxes)
        {
            List<string> stack = new List<string> { box };
            if (box.StartsWith('m'))
            {
                // Inserts of a module, in rack order.
                stack.AddRange(insertsOf[ModuleIdOf(box)].OrderBy(m => m.Module.Row).ThenBy(m => m.Module.Column)
                    .Select(m => ModuleKey(m.Module.Id)).Where(k => !anchorOf.ContainsKey(k) && !placed.Contains(k)));
            }
            stackOf[box] = stack;
            foreach (string key in stack)
            {
                anchorOf.TryAdd(key, box);
            }
        }
        foreach (string watcher in watchers)
        {
            stackOf[watcher] = new List<string> { watcher };
            anchorOf[watcher] = watcher;
        }
        return new Stacks(boxes, watchers, watchers.ToHashSet(), stackOf, anchorOf);
    }

    /// <summary>Cables between the same two boxes with the same signal merge into one line.</summary>
    private static List<IGrouping<EdgeKey, AnalyzedCable>> MergeCables(Folding folding, Stacks stacks) => folding.Cables
        .Where(c => folding.BoxOf(c.Cable.From.ModuleId) != folding.BoxOf(c.Cable.To.ModuleId) && !stacks.Watching.Contains(folding.BoxOf(c.Cable.From.ModuleId))
            && stacks.AnchorOf.ContainsKey(folding.BoxOf(c.Cable.From.ModuleId)) && stacks.AnchorOf.ContainsKey(folding.BoxOf(c.Cable.To.ModuleId)))
        .GroupBy(c => (folding.BoxOf(c.Cable.From.ModuleId), folding.BoxOf(c.Cable.To.ModuleId), c.Signal))
        .ToList();

    /// <param name="Of">Column of each box in the flow.</param>
    /// <param name="Count">Columns of the flow.</param>
    /// <param name="Drawn">Columns drawn: the watchers' column comes after the last one.</param>
    private sealed record Columns(Dictionary<string, int> Of, int Count, int Drawn);

    private static Columns AssignColumns(Stacks stacks, List<IGrouping<EdgeKey, AnalyzedCable>> merged)
    {
        List<(string From, string To)> links = merged
            .Select(g => (stacks.AnchorOf[g.Key.From], stacks.AnchorOf[g.Key.To]))
            .Where(l => l.Item1 != l.Item2 && !stacks.Watching.Contains(l.Item2))
            .Distinct().ToList();
        Dictionary<string, int> column = Depths(stacks.Boxes, links);
        int count = column.Values.DefaultIfEmpty(0).Max() + 1;
        return new Columns(column, count, count + (stacks.Watchers.Count > 0 ? 1 : 0));
    }

    private sealed record Lane(Band Band, string? Group, string Title, List<string> Boxes);

    /// <summary>
    /// Lanes: per band, in the groups' order, an unfolded group gets its own lane where it stands, and so does every
    /// voice even folded; the other folded groups and single modules around them share a lane.
    /// </summary>
    private static List<Lane> AssignLanes(Folding folding, List<string> boxes)
    {
        List<Lane> lanes = new List<Lane>();
        foreach (Band band in Enum.GetValues<Band>().Where(b => b != Band.Monitor))
        {
            List<string> folded = new List<string>();
            void CloseFolded()
            {
                if (folded.Count > 0)
                {
                    lanes.Add(new Lane(band, null, PatchGrouping.BandTitle(band, shared: false), folded));
                    folded = new List<string>();
                }
            }
            foreach (PatchGroup group in folding.Groups.Where(g => g.Band == band))
            {
                if (folding.HasOwnLane(group))
                {
                    CloseFolded();
                    HashSet<long> members = group.Members.ToHashSet();
                    lanes.Add(new Lane(band, group.Key, group.Title, boxes.Where(b => b.StartsWith('m') && members.Contains(ModuleIdOf(b))).ToList()));
                }
                else if (band == Band.Voice)
                {
                    // Every voice has its own lane from the start, folded or not: unfolding one doesn't move the others.
                    CloseFolded();
                    lanes.Add(new Lane(band, null, VoiceLaneTitle(group.Title), new List<string> { folding.SingleBox(group) }));
                }
                else
                {
                    folded.Add(folding.SingleBox(group));
                }
            }
            CloseFolded();
        }
        return lanes;
    }

    private static Dictionary<string, int> LaneOf(List<Lane> lanes)
    {
        Dictionary<string, int> laneOf = new Dictionary<string, int>();
        for (int lane = 0; lane < lanes.Count; lane++)
        {
            foreach (string box in lanes[lane].Boxes)
            {
                laneOf[box] = lane;
            }
        }
        return laneOf;
    }

    /// <param name="Plans">Tracks and gaps of every cable line between two placed boxes.</param>
    /// <param name="TrackColumn">Column of each track (virtual node).</param>
    /// <param name="Segments">Links between boxes and tracks of adjacent columns, feedback cables left aside.</param>
    /// <param name="PredecessorOf">The box or track each track comes from.</param>
    private sealed record Routing(Dictionary<EdgeKey, Plan> Plans, Dictionary<string, int> TrackColumn, List<(string U, string V)> Segments, HashSet<string> FeedbackTracks, Dictionary<string, string> PredecessorOf);

    /// <summary>
    /// Virtual nodes: a cable gets one track per column it passes, in its source's lane (added to laneOf). A feedback
    /// cable (going back to an earlier or the same column) leaves its source on the right, turns back along tracks in
    /// every column from its source's down to its target's, and turns again to enter its target from the left.
    /// </summary>
    private static Routing PlanTracks(List<IGrouping<EdgeKey, AnalyzedCable>> merged, Stacks stacks, Columns columns, Dictionary<string, int> laneOf)
    {
        Dictionary<EdgeKey, Plan> plans = new Dictionary<EdgeKey, Plan>();
        Dictionary<string, int> trackColumn = new Dictionary<string, int>();
        List<(string U, string V)> segments = new List<(string U, string V)>();
        foreach (IGrouping<EdgeKey, AnalyzedCable> edge in merged)
        {
            string source = stacks.AnchorOf[edge.Key.From];
            string target = stacks.AnchorOf[edge.Key.To];
            if (source == target)
            {
                continue;
            }
            int first = columns.Of[source];
            int last = stacks.Watching.Contains(target) ? columns.Count : columns.Of[target];
            bool feedback = last <= first;
            List<int> trackColumns = feedback ? Enumerable.Range(last, first - last + 1).Reverse().ToList() : Enumerable.Range(first + 1, last - first - 1).ToList();
            List<int> gaps = feedback ? Enumerable.Range(last - 1, first - last + 2).Reverse().ToList() : Enumerable.Range(first, last - first).ToList();
            List<string> tracks = new List<string>();
            foreach (int col in trackColumns)
            {
                string track = $"t{plans.Count}:{col}";
                tracks.Add(track);
                trackColumn[track] = col;
                laneOf[track] = laneOf[source];
            }
            plans[edge.Key] = new Plan(tracks, gaps, feedback);

            if (feedback)
            {
                continue;
            }
            List<string> chain = new List<string> { source };
            chain.AddRange(tracks);
            if (!stacks.Watching.Contains(target))
            {
                chain.Add(target);
            }
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                segments.Add((chain[i], chain[i + 1]));
            }
        }

        HashSet<string> feedbackTracks = plans.Values.Where(p => p.Feedback).SelectMany(p => p.Tracks).ToHashSet();
        Dictionary<string, string> predecessorOf = segments.Where(s => trackColumn.ContainsKey(s.V)).ToDictionary(s => s.V, s => s.U);
        return new Routing(plans, trackColumn, segments, feedbackTracks, predecessorOf);
    }

    /// <summary>Boxes then tracks of each column, before crossing reduction.</summary>
    private static List<List<string>> InitialOrder(List<string> boxes, Columns columns, Dictionary<string, int> trackColumn)
    {
        ILookup<int, string> boxesIn = boxes.ToLookup(b => columns.Of[b]);
        ILookup<int, string> tracksIn = trackColumn.ToLookup(t => t.Value, t => t.Key);
        return Enumerable.Range(0, columns.Count).Select(col => boxesIn[col].Concat(tracksIn[col]).ToList()).ToList();
    }

    /// <summary>Box height: enough room for one arrow every portSpacing on its busiest side.</summary>
    private static Dictionary<string, double> BoxHeights(IEnumerable<string> keys, List<IGrouping<EdgeKey, AnalyzedCable>> merged, Dictionary<EdgeKey, string> trunkOf)
    {
        Dictionary<string, int> leaving = merged.GroupBy(g => g.Key.From).ToDictionary(s => s.Key, s => s.Select(g => trunkOf[g.Key]).Distinct().Count());
        Dictionary<string, int> arriving = merged.GroupBy(g => g.Key.To).ToDictionary(s => s.Key, s => s.Count());
        return keys.ToDictionary(k => k, k => Math.Max(NodeHeight, (Math.Max(leaving.GetValueOrDefault(k), arriving.GetValueOrDefault(k)) + 1) * portSpacing));
    }

    /// <param name="Bands">The lanes, top to bottom (the watchers' column is added later).</param>
    /// <param name="Positions">Column and top of every box.</param>
    /// <param name="TrackY">Height of every track.</param>
    /// <param name="Y">Bottom of the drawing so far.</param>
    private sealed record Placement(List<DiagramBand> Bands, Dictionary<string, (int Column, double Y)> Positions, Dictionary<string, double> TrackY, Dictionary<string, double> HeightOf, double Y, double WatchersBottom)
    {
        public double Center(string key) => Positions[key].Y + (HeightOf[key] / 2);
    }

    /// <summary>
    /// Coordinates: boxes stack from the top of their lane, tracks under them; a feedback cable runs back under
    /// everything. Then the watchers, top to bottom by the height of what they watch.
    /// </summary>
    private static Placement PlaceRows(List<Lane> lanes, Dictionary<string, int> laneOf, List<List<string>> order, Routing routing, Stacks stacks, Columns columns, Dictionary<string, double> heightOf, List<IGrouping<EdgeKey, AnalyzedCable>> merged)
    {
        List<DiagramBand> bands = new List<DiagramBand>();
        Dictionary<string, (int Column, double Y)> positions = new Dictionary<string, (int Column, double Y)>();
        Dictionary<string, double> trackY = new Dictionary<string, double>();
        Placement Snapshot(double y, double watchersBottom) => new Placement(bands, positions, trackY, heightOf, y, watchersBottom);
        List<ILookup<int, string>> laneElements = order.Select(c => c.Where(e => !routing.FeedbackTracks.Contains(e)).ToLookup(e => laneOf[e])).ToList();
        ILookup<int, Plan> feedbackPlans = routing.Plans.Values.Where(p => p.Feedback && p.Tracks.Count > 0).ToLookup(p => laneOf[p.Tracks[0]]);
        double y = margin;
        for (int lane = 0; lane < lanes.Count; lane++)
        {
            double top = y + bandHeaderHeight;
            double bottom = top + NodeHeight;
            double[] free = new double[columns.Count];
            for (int col = 0; col < columns.Count; col++)
            {
                List<string> here = laneElements[col][lane].ToList();
                int lastBox = here.FindLastIndex(e => !routing.TrackColumn.ContainsKey(e));
                double cursor = top;
                for (int i = 0; i < here.Count; i++)
                {
                    string element = here[i];
                    if (routing.TrackColumn.ContainsKey(element))
                    {
                        // Trailing tracks keep the height they come in at, so a line runs straight when it can.
                        double line = cursor;
                        if (routing.PredecessorOf.TryGetValue(element, out string? before) && i > lastBox)
                        {
                            line = Math.Max(cursor, trackY.TryGetValue(before, out double previous) ? previous : positions[before].Y + (heightOf[before] / 2));
                        }
                        trackY[element] = line;
                        cursor = line + trackSpacing;
                        bottom = Math.Max(bottom, line + 4);
                        continue;
                    }
                    foreach (string key in stacks.StackOf[element])
                    {
                        positions[key] = (col, cursor);
                        bottom = Math.Max(bottom, cursor + heightOf[key]);
                        cursor += heightOf[key] + rowSpacing;
                    }
                }
                free[col] = cursor;
            }

            // A feedback cable runs back in one straight line, under everything in the columns it spans.
            foreach (Plan plan in feedbackPlans[lane])
            {
                List<int> spanned = plan.Tracks.Select(t => routing.TrackColumn[t]).ToList();
                double line = spanned.Max(c => free[c]);
                foreach (string track in plan.Tracks)
                {
                    trackY[track] = line;
                }
                foreach (int col in spanned)
                {
                    free[col] = line + trackSpacing;
                }
                bottom = Math.Max(bottom, line + 4);
            }

            double height = bottom - y + bandPadding;
            bands.Add(new DiagramBand(lanes[lane].Band, lanes[lane].Group, lanes[lane].Title, y, height));
            y += height + 8;
        }

        // Watchers, top to bottom by the height of what they watch, each centered on it unless the one above is in the way.
        if (stacks.Watchers.Count == 0)
        {
            return Snapshot(y, 0);
        }
        Placement placed = Snapshot(y, 0);
        ILookup<string, string> watchedFrom = merged.ToLookup(g => g.Key.To, g => g.Key.From);
        double Wanted(string watcher) => watchedFrom[watcher].Select(placed.Center).DefaultIfEmpty(margin).Average();
        double cursorY = margin + bandHeaderHeight;
        foreach (string watcher in stacks.Watchers.OrderBy(Wanted))
        {
            double top = Math.Max(cursorY, Wanted(watcher) - (heightOf[watcher] / 2));
            positions[watcher] = (columns.Count, top);
            cursorY = top + heightOf[watcher] + rowSpacing;
        }
        double watchersBottom = Math.Max(y - 8, cursorY - rowSpacing + bandPadding);
        return Snapshot(Math.Max(y, watchersBottom + 8), watchersBottom);
    }

    /// <param name="Levels">The height a cable runs at in each column: out port, tracks, in port.</param>
    private sealed record Ports(Dictionary<EdgeKey, double> OutY, Dictionary<EdgeKey, double> InY, Dictionary<EdgeKey, List<double>> Levels);

    /// <summary>
    /// Ports: several cables on one side of a box are spread along it, sorted by where the line goes next, so they
    /// don't cross at the box. Then the height a cable runs at in each column: out port, tracks, in port.
    /// </summary>
    private static Ports PlacePorts(List<IGrouping<EdgeKey, AnalyzedCable>> merged, Dictionary<EdgeKey, string> trunkOf, Dictionary<EdgeKey, Plan> plans, Placement placement)
    {
        double NextY(EdgeKey key) => plans.TryGetValue(key, out Plan? plan) && plan.Tracks.Count > 0 ? placement.TrackY[plan.Tracks[0]] : placement.Center(key.To);
        double PreviousY(EdgeKey key) => plans.TryGetValue(key, out Plan? plan) && plan.Tracks.Count > 0 ? placement.TrackY[plan.Tracks[^1]] : placement.Center(key.From);
        Dictionary<EdgeKey, double> outY = new Dictionary<EdgeKey, double>();
        Dictionary<EdgeKey, double> inY = new Dictionary<EdgeKey, double>();
        foreach (IGrouping<string, IGrouping<EdgeKey, AnalyzedCable>> side in merged.GroupBy(g => g.Key.From))
        {
            List<List<EdgeKey>> trunks = side.GroupBy(g => trunkOf[g.Key])
                .Select(t => t.Select(g => g.Key).ToList())
                .OrderBy(t => t.Average(NextY)).ThenBy(t => t[0].Signal)
                .ToList();
            Spread(trunks, placement.Positions[side.Key].Y, placement.HeightOf[side.Key], outY);
        }
        foreach (IGrouping<string, IGrouping<EdgeKey, AnalyzedCable>> side in merged.GroupBy(g => g.Key.To))
        {
            Spread(side.Select(g => g.Key).OrderBy(PreviousY).ThenBy(k => k.Signal).Select(k => new List<EdgeKey> { k }).ToList(), placement.Positions[side.Key].Y, placement.HeightOf[side.Key], inY);
        }
        Dictionary<EdgeKey, List<double>> levels = plans.ToDictionary(
            kv => kv.Key,
            kv => new[] { outY[kv.Key] }.Concat(kv.Value.Tracks.Select(t => placement.TrackY[t])).Append(inY[kv.Key]).ToList());
        return new Ports(outY, inY, levels);
    }

    /// <summary>Vertical runs: every change of height, and both U-turns of a feedback cable, in the gap where it happens.</summary>
    private static List<Turn> FindTurns(Dictionary<EdgeKey, Plan> plans, Dictionary<EdgeKey, List<double>> levels)
    {
        List<Turn> turns = new List<Turn>();
        foreach ((EdgeKey key, Plan plan) in plans)
        {
            List<double> ys = levels[key];
            for (int i = 0; i < plan.Gaps.Count; i++)
            {
                bool uTurn = plan.Feedback && (i == 0 || i == plan.Gaps.Count - 1);
                if (uTurn || Math.Abs(ys[i + 1] - ys[i]) >= 1)
                {
                    turns.Add(new Turn(plan.Gaps[i], key, i, ys[i], ys[i + 1], plan.Feedback));
                }
            }
        }
        return turns;
    }

    /// <summary>
    /// A host and its stacked insert share a column: the send runs down in the gap on the right of it, the return up in
    /// the gap on the left, in channels of their own like any other cable.
    /// </summary>
    private static IEnumerable<Turn> SideLoopTurns(List<IGrouping<EdgeKey, AnalyzedCable>> merged, Dictionary<EdgeKey, Plan> plans, Ports ports, Dictionary<string, (int Column, double Y)> positions)
    {
        foreach (EdgeKey key in merged.Select(g => g.Key).Where(k => !plans.ContainsKey(k)))
        {
            int column = positions[key.From].Column;
            bool send = positions[key.To].Y > positions[key.From].Y;
            yield return new Turn(send ? column : column - 1, key, 0, ports.OutY[key], ports.InY[key], false);
        }
    }

    /// <summary>
    /// Port tabs: one per cable line arriving, one per trunk leaving. A stacked insert's side loop only has them on its
    /// host (send out on the right, return in on the left). They take room in the gaps, next to the boxes.
    /// </summary>
    private sealed class Tabs
    {
        private readonly Dictionary<EdgeKey, PortTab> inTabs = new Dictionary<EdgeKey, PortTab>();
        private readonly Dictionary<EdgeKey, PortTab> outTabs = new Dictionary<EdgeKey, PortTab>();
        private readonly Dictionary<int, double> inWidths;
        private readonly Dictionary<int, double> outWidths;
        private readonly ILookup<string, PortTab> arrivingAt;
        private readonly ILookup<string, PortTab> leavingFrom;

        public Tabs(List<IGrouping<EdgeKey, AnalyzedCable>> merged, Dictionary<EdgeKey, Plan> plans, Ports ports, bool portTabs, Dictionary<string, (int Column, double Y)> positions)
        {
            foreach (IGrouping<EdgeKey, AnalyzedCable> edge in merged.Where(g => portTabs))
            {
                bool sideLoop = !plans.ContainsKey(edge.Key);
                bool send = positions[edge.Key.To].Y > positions[edge.Key.From].Y;
                if (!sideLoop || !send)
                {
                    string arriving = PortTabs.Text(edge.Select(c => c.ToPort), edge.Key.Signal);
                    inTabs[edge.Key] = new PortTab(false, ports.InY[edge.Key], PortTabs.Width(arriving), arriving, edge.Key.Signal, string.Join("\n", edge.Select(c => c.ToPort).Distinct()));
                }
                if (!sideLoop || send)
                {
                    string leaving = PortTabs.Text(edge.Select(c => c.FromPort), edge.Key.Signal);
                    outTabs[edge.Key] = new PortTab(true, ports.OutY[edge.Key], PortTabs.Width(leaving), leaving, edge.Key.Signal, string.Join("\n", edge.Select(c => c.FromPort).Distinct()));
                }
            }
            inWidths = inTabs.GroupBy(t => positions[t.Key.To].Column).ToDictionary(g => g.Key, g => g.Max(t => t.Value.Width));
            outWidths = outTabs.GroupBy(t => positions[t.Key.From].Column).ToDictionary(g => g.Key, g => g.Max(t => t.Value.Width));
            arrivingAt = inTabs.ToLookup(t => t.Key.To, t => t.Value);
            leavingFrom = outTabs.ToLookup(t => t.Key.From, t => t.Value);
        }

        /// <summary>Widest tab arriving at a box of this column.</summary>
        public double InWidth(int column) => inWidths.GetValueOrDefault(column);

        /// <summary>Widest tab leaving a box of this column.</summary>
        public double OutWidth(int column) => outWidths.GetValueOrDefault(column);

        public double InTabWidth(EdgeKey key) => inTabs.TryGetValue(key, out PortTab? tab) ? tab.Width : 0;

        public double OutTabWidth(EdgeKey key) => outTabs.TryGetValue(key, out PortTab? tab) ? tab.Width : 0;

        /// <summary>A box's tabs: one per line arriving, one per height a line leaves at.</summary>
        public List<PortTab> Of(string nodeKey) => arrivingAt[nodeKey].Concat(leavingFrom[nodeKey].DistinctBy(t => t.Y)).ToList();
    }

    /// <summary>
    /// Each gap between two columns is as wide as its vertical runs need, so busy gaps don't squeeze lines together.
    /// Gaps -1 and columns - 1, outside the first and last columns, only exist for feedback cables.
    /// The first turns of a trunk's cables, when they go the same way, share one vertical run.
    /// </summary>
    private sealed class Gaps
    {
        private readonly Dictionary<int, int> runCount;
        private readonly Tabs tabs;
        private readonly int drawnColumns;

        public Gaps(List<Turn> turns, Tabs tabs, int drawnColumns, Func<Turn, string?> trunkOf)
        {
            this.tabs = tabs;
            this.drawnColumns = drawnColumns;
            runCount = turns.GroupBy(t => t.Gap).ToDictionary(g => g.Key, g => Bundles(g, trunkOf).Count);
            ColumnX = new List<double> { margin + Width(-1) };
            for (int gap = 0; gap + 1 < drawnColumns; gap++)
            {
                ColumnX.Add(ColumnX[gap] + NodeWidth + Width(gap));
            }
            Channels = AssignChannels(turns, trunkOf, g => Left(g) + tabs.OutWidth(g), g => Right(g) - tabs.InWidth(g + 1));
        }

        /// <summary>Left edge of the boxes of each column.</summary>
        public List<double> ColumnX { get; }

        /// <summary>x of every vertical run.</summary>
        public Dictionary<(EdgeKey Key, int Part), double> Channels { get; }

        public double Left(int gap) => gap < 0 ? margin : ColumnX[gap] + NodeWidth;

        public double Right(int gap) => gap + 1 < drawnColumns ? ColumnX[gap + 1] : Left(gap) + Width(gap);

        private double Width(int gap)
        {
            int count = runCount.GetValueOrDefault(gap);
            double needed = gapPadding + (count * channelPitch) + tabs.OutWidth(gap) + tabs.InWidth(gap + 1);
            return gap < 0 || gap >= drawnColumns - 1 ? (count > 0 ? needed : 0) : Math.Max(minGap, needed);
        }
    }

    private static List<DiagramNode> MakeNodes(PatchAnalysis analysis, Folding folding, Placement placement, List<double> columnX, Tabs tabs, MonitorView monitors, bool functionNames, bool libraryLinks)
    {
        ILookup<long, AnalyzedCable> cablesTo = analysis.Cables.ToLookup(c => c.Cable.To.ModuleId);
        ILookup<long, AnalyzedCable> cablesFrom = analysis.Cables.ToLookup(c => c.Cable.From.ModuleId);
        List<DiagramNode> nodes = new List<DiagramNode>();
        foreach (PatchGroup group in folding.Groups)
        {
            if (!folding.ShowsModules(group))
            {
                string key = GroupKey(group.Key);
                (int gc, double gy) = placement.Positions[key];
                nodes.Add(new DiagramNode(key, null, group.Key, true, FoldedTitle(group, analysis), FoldedSubtitle(group, analysis),
                    group.Role, group.Band, false, group.Members.Select(id => analysis.Module(id).Title).ToList(), columnX[gc], gy, NodeWidth, placement.HeightOf[key])
                {
                    LibraryPanels = libraryLinks ? LibraryPanels(group, analysis) : Array.Empty<LibraryPanel>(),
                    UnlistedCount = libraryLinks ? group.Members.Count(id => !Core.Catalog.VcvLibrary.IsListed(analysis.Module(id).Plugin)) : 0,
                });
                continue;
            }
            foreach (long id in group.Members.Where(id => placement.Positions.ContainsKey(ModuleKey(id))))
            {
                AnalyzedModule m = analysis.Module(id);
                (int mc, double my) = placement.Positions[ModuleKey(id)];
                string origin = m.InsertOf is long host ? $"insert of {analysis.Module(host).Title}"
                    : m.Expanders.Count > 0 ? $"{m.Plugin} + {string.Join(", ", m.Expanders)}" : m.Plugin;
                // "MIDI" or "UTILITY" would say less than the module's own name: those keep it.
                bool byFunction = functionNames && m.Function is not (Core.Catalog.ModuleFunction.Midi or Core.Catalog.ModuleFunction.Audio or Core.Catalog.ModuleFunction.Utility);
                string moduleTitle = byFunction ? FunctionTitle(m) : m.Title;
                string subtitle = byFunction ? $"{m.Title} · {origin}" : $"{origin} · {Core.Catalog.ModuleFunctions.Label(m.Function)}";
                nodes.Add(new DiagramNode(ModuleKey(id), id, group.Key, false, moduleTitle, subtitle, m.Role, m.Band, m.InsertOf is not null,
                    ModuleDetails(analysis, m, cablesTo, cablesFrom), columnX[mc], my, NodeWidth, placement.HeightOf[ModuleKey(id)])
                {
                    Function = m.Function,
                    LibraryKey = libraryLinks && Core.Catalog.VcvLibrary.IsListed(m.Plugin) ? Core.Catalog.VcvLibrary.Key(m.Plugin, m.Module.Model) : null,
                });
            }
        }
        if (monitors == MonitorView.Badge)
        {
            Watched watched = new Watched(analysis, folding);
            nodes = nodes.Select(n => n with { Watchers = watched.Of(n) }).ToList();
        }
        return nodes.Select(n => n with { Tabs = tabs.Of(n.Key) }).ToList();
    }

    /// <summary>"Voice 1" from "Voice 1: LVCO ← ADDR-SEQ": the box inside already says the rest.</summary>
    private static string VoiceLaneTitle(string groupTitle) =>
        groupTitle.IndexOf(": ", StringComparison.Ordinal) is int colon and > 0 ? groupTitle[..colon] : groupTitle;

    /// <summary>"Scope 'Channel A' ← SLEW #1 'Signal'" for every monitor cable leaving a box (or a module folded in it).</summary>
    private sealed class Watched
    {
        private readonly ILookup<long, string> byModule;
        private readonly ILookup<string, string> byGroup;

        public Watched(PatchAnalysis analysis, Folding folding)
        {
            List<AnalyzedCable> cables = analysis.Cables
                .Where(c => folding.Visible.Contains(c.Cable.From.ModuleId) && !folding.Visible.Contains(c.Cable.To.ModuleId)
                    && analysis.Module(c.Cable.To.ModuleId).Band == Band.Monitor)
                .ToList();
            string Text(AnalyzedCable c) => $"{analysis.Module(c.Cable.To.ModuleId).Title} '{c.ToPort}' ← {analysis.Module(c.Cable.From.ModuleId).Title} '{c.FromPort}'";
            byModule = cables.ToLookup(c => c.Cable.From.ModuleId, Text);
            byGroup = cables.ToLookup(c => folding.GroupOf[c.Cable.From.ModuleId].Key, Text);
        }

        public IReadOnlyList<string> Of(DiagramNode node) =>
            (node.IsFolded ? byGroup[node.Group] : node.ModuleId is long id ? byModule[id] : Enumerable.Empty<string>()).ToList();
    }

    /// <summary>"FILTER #2": the function, numbered like the module when several share a name.</summary>
    private static string FunctionTitle(AnalyzedModule module)
    {
        int hash = module.Title.LastIndexOf(" #", StringComparison.Ordinal);
        return Core.Catalog.ModuleFunctions.Label(module.Function).ToUpperInvariant() + (hash > 0 ? module.Title[hash..] : "");
    }

    public static string ModuleKey(long moduleId) => "m" + moduleId.ToString(CultureInfo.InvariantCulture);

    public static string GroupKey(string group) => "g:" + group;

    private static long ModuleIdOf(string key) => long.Parse(key[1..], CultureInfo.InvariantCulture);

    /// <summary>The distinct modules of a folded group listed in the VCV Library, in group order, with how many of each.</summary>
    private static List<LibraryPanel> LibraryPanels(PatchGroup group, PatchAnalysis analysis) => group.Members
        .Select(analysis.Module)
        .Where(m => Core.Catalog.VcvLibrary.IsListed(m.Plugin))
        .GroupBy(m => Core.Catalog.VcvLibrary.Key(m.Plugin, m.Module.Model))
        .Select(g => new LibraryPanel(g.Key, g.Count()))
        .ToList();

    private static string FoldedTitle(PatchGroup group, PatchAnalysis analysis)
    {
        if (group.Band != Band.Voice)
        {
            return PatchGrouping.BoxTitle(group.Band);
        }
        // "PULSE ← Host MIDI" reads better than "Voice 2: PULSE ← Host MIDI" on a box.
        Voice first = analysis.Voices[group.Voices[0]];
        string sources = group.Count > 1 ? $"{analysis.Module(first.ModuleIds[0]).Title.Split(" #")[0]} ×{group.Count}" : first.Sources;
        return first.PitchOrigin is null ? sources : $"{sources} ← {first.PitchOrigin}";
    }

    /// <summary>What's inside a folded box, in a few words: the audio chain for a voice, the members otherwise.</summary>
    private static string FoldedSubtitle(PatchGroup group, PatchAnalysis analysis)
    {
        List<AnalyzedModule> members = group.Members.Select(analysis.Module).ToList();
        if (group.Band == Band.Voice)
        {
            Voice first = analysis.Voices[group.Voices[0]];
            List<string> processing = first.ModuleIds.Select(analysis.Module).Where(m => m.Role != Core.Catalog.Role.Source).Select(m => m.Title).ToList();
            int modulators = members.Count(m => m.Band is Band.Modulation or Band.Pitch);
            string chain = processing.Count > 0 ? string.Join(" → ", processing) : "direct out";
            return (modulators > 0 ? $"{chain} · +{modulators} mod" : chain) + LoopHint(group, analysis);
        }
        return string.Join(", ", members.Select(m => m.Title)) + LoopHint(group, analysis);
    }

    /// <summary>A loop folded away inside a box is worth a hint: there's something to unfold.</summary>
    private static string LoopHint(PatchGroup group, PatchAnalysis analysis) =>
        analysis.Loops.Any(loop => loop.All(group.Members.Contains)) ? " · ↺ feedback" : "";

    private static List<string> ModuleDetails(PatchAnalysis analysis, AnalyzedModule module, ILookup<long, AnalyzedCable> cablesTo, ILookup<long, AnalyzedCable> cablesFrom)
    {
        List<string> lines = new List<string> { $"{module.Title} ({module.Plugin} · {module.Role})" };
        if (module.Expanders.Count > 0)
        {
            lines.Add($"with expanders: {string.Join(", ", module.Expanders)}");
        }
        lines.AddRange(cablesTo[module.Module.Id].Select(c => $"in  {c.ToPort} ← {analysis.Module(c.Cable.From.ModuleId).Title}"));
        lines.AddRange(cablesFrom[module.Module.Id].Select(c => $"out {c.FromPort} → {analysis.Module(c.Cable.To.ModuleId).Title}"));
        return lines;
    }

    /// <summary>Longest-path column of each box, in the given (rack) order for stability.</summary>
    private static Dictionary<string, int> Depths(List<string> boxes, List<(string From, string To)> links)
    {
        Dictionary<string, long> index = boxes.Select((b, i) => (b, (long)i)).ToDictionary(p => p.b, p => p.Item2);
        Dictionary<long, int> depths = GraphOrder.Depths(
            boxes.Select(b => index[b]).ToList(),
            links.Select(l => (index[l.From], index[l.To])).ToList());
        return boxes.ToDictionary(b => b, b => depths[index[b]]);
    }

    /// <summary>
    /// Columns a cable runs through on tracks, and the gaps where it may turn (one between each two heights it runs at).
    /// A feedback cable's tracks go from its source's column back to its target's.
    /// </summary>
    private sealed record Plan(List<string> Tracks, List<int> Gaps, bool Feedback);

    /// <summary>A vertical run in a gap, from one height to the next; Part is its index along the cable.</summary>
    private sealed record Turn(int Gap, EdgeKey Key, int Part, double YFrom, double YTo, bool Feedback);

    /// <summary>
    /// Cables leave from the right edge and arrive on the left edge of boxes. Along their tracks they're horizontal,
    /// every change of height is a rounded bend in its own channel of a gap, and they end horizontally into the arrow,
    /// so every arrowhead has a visible line of its own.
    /// </summary>
    private static List<DiagramEdge> DrawEdges(
        PatchAnalysis analysis,
        List<IGrouping<EdgeKey, AnalyzedCable>> merged,
        Dictionary<string, DiagramNode> nodeByKey,
        Dictionary<EdgeKey, Plan> plans,
        Dictionary<EdgeKey, List<double>> levels,
        Dictionary<EdgeKey, double> outY,
        Dictionary<EdgeKey, double> inY,
        Dictionary<(EdgeKey Key, int Part), double> channels,
        IReadOnlyDictionary<long, string>? intents,
        Dictionary<string, List<LabelPlacement.Run>> runs,
        Func<EdgeKey, double> outTab,
        Func<EdgeKey, double> inTab)
    {
        // Tooltips name modules as in the rack, whatever their box shows.
        string TitleOf(DiagramNode node) => node.ModuleId is long id ? analysis.Module(id).Title : node.Title;
        List<DiagramEdge> edges = new List<DiagramEdge>();
        foreach (IGrouping<EdgeKey, AnalyzedCable> group in merged)
        {
            DiagramNode from = nodeByKey[group.Key.From];
            DiagramNode to = nodeByKey[group.Key.To];
            double x1 = from.X + from.Width + outTab(group.Key);
            double y1 = outY[group.Key];
            double x2 = to.X - inTab(group.Key) - ArrowLength;
            double y2 = inY[group.Key];

            string path;
            double labelX;
            double labelY;
            bool feedback = false;
            if (plans.TryGetValue(group.Key, out Plan? plan))
            {
                List<double> ys = levels[group.Key];
                feedback = plan.Feedback;
                path = "M " + Num(x1) + " " + Num(y1);
                labelX = (x1 + x2) / 2;
                labelY = y1;
                List<double> used = new List<double>();
                List<(double X, double Y)> corners = new List<(double, double)> { (x1, y1) };
                for (int part = 0; part < plan.Gaps.Count; part++)
                {
                    if (channels.TryGetValue((group.Key, part), out double channel))
                    {
                        // A feedback cable heads left between its two U-turns.
                        int arriving = !feedback || part == 0 ? 1 : -1;
                        int leaving = !feedback || part == plan.Gaps.Count - 1 ? 1 : -1;
                        path += Bend(channel, ys[part], ys[part + 1], arriving, leaving);
                        used.Add(channel);
                        corners.Add((channel, ys[part]));
                        corners.Add((channel, ys[part + 1]));
                        labelX = channel;
                        labelY = (ys[part] + ys[part + 1]) / 2;
                    }
                }
                path += " H " + Num(x2);
                corners.Add((x2, y2));
                if (feedback)
                {
                    labelX = (used[0] + used[^1]) / 2;
                    labelY = ys[1];
                }
                else
                {
                    runs[$"{group.Key.From}>{group.Key.To}:{group.Key.Signal}"] = corners.Zip(corners.Skip(1))
                        .Where(p => p.First != p.Second)
                        .Select(p => new LabelPlacement.Run(p.First.X, p.First.Y, p.Second.X, p.Second.Y))
                        .ToList();
                }
            }
            else
            {
                // Host and its insert, stacked in one column: a side loop, down on the right (send), up on the left (return).
                bool down = to.Y > from.Y;
                double sx = down ? from.X + from.Width + outTab(group.Key) : from.X;
                double ex = down ? to.X + to.Width + ArrowLength : to.X - inTab(group.Key) - ArrowLength;
                // Out to its channel, along it, back in: square like every other cable.
                int outward = down ? 1 : -1;
                double channel = channels[(group.Key, 0)];
                path = $"M {Num(sx)} {Num(y1)}" + Bend(channel, y1, y2, outward, -outward) + $" H {Num(ex)}";
                labelX = channel;
                labelY = (y1 + y2) / 2;
            }

            List<AnalyzedCable> groupCables = group.ToList();
            AnalyzedCable firstCable = groupCables[0];
            string intent = groupCables.Count == 1
                ? intents is not null && intents.TryGetValue(firstCable.Cable.Id, out string? custom) && custom is not null ? custom : firstCable.SuggestedIntent
                : $"{groupCables.Count} × {DiagramLayout.LayerLabel(DiagramEdgeLayer(group.Key.Signal))}";
            string fromPort = groupCables.Count == 1 ? firstCable.FromPort : string.Join(", ", groupCables.Select(c => c.FromPort).Distinct());
            string toPort = groupCables.Count == 1 ? firstCable.ToPort : string.Join(", ", groupCables.Select(c => $"{analysis.Module(c.Cable.To.ModuleId).Title} '{c.ToPort}'").Distinct());
            edges.Add(new DiagramEdge(
                $"{group.Key.From}>{group.Key.To}:{group.Key.Signal}", from.Key, to.Key, groupCables.Select(c => c.Cable.Id).ToList(),
                TitleOf(from), fromPort, TitleOf(to), toPort, group.Key.Signal, intent, path, labelX, labelY)
            {
                IsFeedback = feedback,
                IsMonitor = to.Band == Band.Monitor,
            });
        }
        return edges;
    }

    /// <summary>
    /// x of every vertical run, spread over its gap. In each gap, runs going down sit left of runs going up,
    /// ordered so they don't cross each other. A trunk's runs going the same way are one run, as long as the farthest.
    /// </summary>
    private static Dictionary<(EdgeKey Key, int Part), double> AssignChannels(List<Turn> turns, Func<Turn, string?> trunkOf, Func<int, double> gapLeft, Func<int, double> gapRight)
    {
        Dictionary<(EdgeKey Key, int Part), double> channelX = new Dictionary<(EdgeKey, int), double>();
        foreach (IGrouping<int, Turn> gap in turns.GroupBy(t => t.Gap))
        {
            static bool Down(List<Turn> bundle) => bundle[0].YTo > bundle[0].YFrom;
            List<List<Turn>> ordered = Bundles(gap, trunkOf)
                .OrderBy(b => Down(b) ? 0 : 1)
                .ThenBy(b => Down(b) ? -b.Max(t => t.YTo) : b.Min(t => t.YTo))
                .ToList();
            TurnFirstWhenAlongside(ordered);
            double left = gapLeft(gap.Key) + 14;
            double right = gapRight(gap.Key) - ArrowLength - 12;
            double step = (right - left) / ordered.Count;
            for (int i = 0; i < ordered.Count; i++)
            {
                foreach (Turn turn in ordered[i])
                {
                    channelX[(turn.Key, turn.Part)] = left + ((i + 0.5) * step);
                }
            }
        }
        return channelX;
    }

    /// <summary>
    /// A cable leaving its channel rightwards a few pixels from the height another one comes in at from the left would
    /// run alongside it between their two channels, the two reading as one: the one coming in turns first (it then
    /// crosses the other at a right angle, which reads).
    /// </summary>
    private static void TurnFirstWhenAlongside(List<List<Turn>> ordered)
    {
        static bool Alongside(List<Turn> leaving, List<Turn> entering) => leaving.Any(l => !l.Feedback
            && entering.Any(e => !e.Feedback && Math.Abs(l.YTo - e.YFrom) is > 0.5 and < minRunSpacing));
        // Bounded: two runs each alongside the other would swap forever.
        for (int pass = 0; pass < ordered.Count; pass++)
        {
            (int Leaving, int Entering)? swap = Enumerable.Range(0, ordered.Count)
                .SelectMany(i => Enumerable.Range(i + 1, ordered.Count - i - 1).Select(j => (i, j)))
                .Where(p => Alongside(ordered[p.i], ordered[p.j]))
                .Select(p => ((int, int)?)p)
                .FirstOrDefault();
            if (swap is not (int leaving, int entering))
            {
                return;
            }
            List<Turn> bundle = ordered[entering];
            ordered.RemoveAt(entering);
            ordered.Insert(leaving, bundle);
        }
    }

    /// <summary>Vertical runs drawn as one: a trunk's turns going the same way; every other turn on its own.</summary>
    private static List<List<Turn>> Bundles(IEnumerable<Turn> turns, Func<Turn, string?> trunkOf) =>
        turns.GroupBy(t => trunkOf(t) is string trunk ? $"{trunk}|{Math.Sign(t.YTo - t.YFrom)}" : $"{t.Key}|{t.Part}")
            .Select(b => b.ToList())
            .ToList();

    /// <summary>
    /// " H … Q … V … Q …": run to channel x, turn, go from fromY to toY, turn back to horizontal (rounded corners).
    /// arriving / leaving are the horizontal directions before and after (1 = rightwards, -1 = leftwards).
    /// </summary>
    private static string Bend(double x, double fromY, double toY, int arriving, int leaving)
    {
        double sign = Math.Sign(toY - fromY);
        double r = Math.Min(8, Math.Abs(toY - fromY) / 2);
        return $" H {Num(x - (arriving * r))} Q {Num(x)} {Num(fromY)} {Num(x)} {Num(fromY + (sign * r))} V {Num(toY - (sign * r))} Q {Num(x)} {Num(toY)} {Num(x + (leaving * r))} {Num(toY)}";
    }

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static string DiagramEdgeLayer(SignalType signal) => signal switch
    {
        SignalType.Audio => "audio",
        SignalType.Pitch => "pitch",
        SignalType.Gate => "gate",
        _ => "modulation",
    };

    /// <summary>One port per slot, evenly spread along a box side; the cables of a slot leave or arrive at the same point.</summary>
    private static void Spread(List<List<EdgeKey>> slots, double top, double height, Dictionary<EdgeKey, double> target)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            foreach (EdgeKey key in slots[i])
            {
                target[key] = top + (height * (i + 1) / (slots.Count + 1));
            }
        }
    }
}