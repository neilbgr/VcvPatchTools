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
        IReadOnlyList<PatchGroup> groups = PatchGrouping.Build(analysis).Where(g => monitors == MonitorView.Modules || g.Band != Band.Monitor).ToList();
        HashSet<long> visible = groups.SelectMany(g => g.Members).ToHashSet();
        List<AnalyzedCable> cables = analysis.Cables.Where(c => visible.Contains(c.Cable.From.ModuleId) && visible.Contains(c.Cable.To.ModuleId)).ToList();
        bool IsUnfolded(PatchGroup g) => unfolded is not null && unfolded.Contains(g.Key);
        // A group of one module is never folded: a folded box promises more than it holds. It stays in its band's lane.
        bool ShowsModules(PatchGroup g) => IsUnfolded(g) || g.Members.Count == 1;
        bool HasOwnLane(PatchGroup g) => IsUnfolded(g) && g.Members.Count > 1;
        Dictionary<long, PatchGroup> groupOf = groups.SelectMany(g => g.Members.Select(id => (id, g))).ToDictionary(p => p.id, p => p.g);

        // Visible box of each module: itself if its group is unfolded, the group's box otherwise.
        string BoxOf(long moduleId) => ShowsModules(groupOf[moduleId]) ? ModuleKey(moduleId) : GroupKey(groupOf[moduleId].Key);

        // Boxes in the flow, and the scopes and displays (watchers), out of it.
        List<string> boxes = new List<string>();
        List<string> watchers = new List<string>();
        foreach (PatchGroup group in groups)
        {
            if (group.Band == Band.Monitor)
            {
                watchers.AddRange(ShowsModules(group) ? group.Members.Select(ModuleKey) : new[] { GroupKey(group.Key) });
            }
            else if (ShowsModules(group))
            {
                boxes.AddRange(group.Members.Where(id => analysis.Module(id).InsertOf is null || !group.Members.Contains(analysis.Module(id).InsertOf!.Value)).Select(ModuleKey));
            }
            else
            {
                boxes.Add(GroupKey(group.Key));
            }
        }

        // A placed box and the inserts stacked right under it move as one block.
        Dictionary<string, List<string>> stackOf = new Dictionary<string, List<string>>();
        Dictionary<string, string> anchorOf = new Dictionary<string, string>();
        foreach (string box in boxes)
        {
            List<string> stack = new List<string> { box };
            if (box.StartsWith('m'))
            {
                stack.AddRange(InsertsOf(analysis, ModuleIdOf(box)).Select(ModuleKey).Where(k => !anchorOf.ContainsKey(k) && !boxes.Contains(k)));
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
        HashSet<string> watching = watchers.ToHashSet();

        List<IGrouping<EdgeKey, AnalyzedCable>> merged = cables
            .Where(c => BoxOf(c.Cable.From.ModuleId) != BoxOf(c.Cable.To.ModuleId) && !watching.Contains(BoxOf(c.Cable.From.ModuleId))
                && anchorOf.ContainsKey(BoxOf(c.Cable.From.ModuleId)) && anchorOf.ContainsKey(BoxOf(c.Cable.To.ModuleId)))
            .GroupBy(c => (BoxOf(c.Cable.From.ModuleId), BoxOf(c.Cable.To.ModuleId), c.Signal))
            .ToList();

        List<(string From, string To)> links = merged
            .Select(g => (anchorOf[g.Key.From], anchorOf[g.Key.To]))
            .Where(l => l.Item1 != l.Item2 && !watching.Contains(l.Item2))
            .Distinct().ToList();
        Dictionary<string, int> column = Depths(boxes, links);
        int columns = column.Values.DefaultIfEmpty(0).Max() + 1;
        // The watchers' column comes after the last one.
        int drawnColumns = columns + (watchers.Count > 0 ? 1 : 0);

        // Lanes: per band, in the groups' order, an unfolded group gets its own lane where it stands, and so does every
        // voice even folded; the other folded groups and single modules around them share a lane.
        List<(Band Band, string? Group, string Title, List<string> Boxes)> lanes = new List<(Band, string?, string, List<string>)>();
        foreach (Band band in Enum.GetValues<Band>().Where(b => b != Band.Monitor))
        {
            List<string> folded = new List<string>();
            void CloseFolded()
            {
                if (folded.Count > 0)
                {
                    lanes.Add((band, null, PatchGrouping.BandTitle(band, shared: false), folded));
                    folded = new List<string>();
                }
            }
            foreach (PatchGroup group in groups.Where(g => g.Band == band))
            {
                if (HasOwnLane(group))
                {
                    CloseFolded();
                    lanes.Add((band, group.Key, group.Title, boxes.Where(b => b.StartsWith('m') && group.Members.Contains(ModuleIdOf(b))).ToList()));
                }
                else if (band == Band.Voice)
                {
                    // Every voice has its own lane from the start, folded or not: unfolding one doesn't move the others.
                    CloseFolded();
                    lanes.Add((band, null, VoiceLaneTitle(group.Title), new List<string> { ShowsModules(group) ? ModuleKey(group.Members[0]) : GroupKey(group.Key) }));
                }
                else
                {
                    folded.Add(ShowsModules(group) ? ModuleKey(group.Members[0]) : GroupKey(group.Key));
                }
            }
            CloseFolded();
        }
        Dictionary<string, int> laneOf = new Dictionary<string, int>();
        for (int lane = 0; lane < lanes.Count; lane++)
        {
            foreach (string box in lanes[lane].Boxes)
            {
                laneOf[box] = lane;
            }
        }

        // Virtual nodes: a cable gets one track per column it passes, in its source's lane. A feedback cable (going back
        // to an earlier or the same column) leaves its source on the right, turns back along tracks in every column from
        // its source's down to its target's, and turns again to enter its target from the left.
        Dictionary<EdgeKey, Plan> plans = new Dictionary<EdgeKey, Plan>();
        Dictionary<string, int> trackColumn = new Dictionary<string, int>();
        List<(string U, string V)> segments = new List<(string U, string V)>();
        foreach (IGrouping<EdgeKey, AnalyzedCable> edge in merged)
        {
            string source = anchorOf[edge.Key.From];
            string target = anchorOf[edge.Key.To];
            if (source == target)
            {
                continue;
            }
            int first = column[source];
            int last = watching.Contains(target) ? columns : column[target];
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
            if (!watching.Contains(target))
            {
                chain.Add(target);
            }
            for (int i = 0; i + 1 < chain.Count; i++)
            {
                segments.Add((chain[i], chain[i + 1]));
            }
        }

        HashSet<string> feedbackTracks = plans.Values.Where(p => p.Feedback).SelectMany(p => p.Tracks).ToHashSet();
        List<List<string>> initial = Enumerable.Range(0, columns)
            .Select(col => boxes.Where(b => column[b] == col).Concat(trackColumn.Where(t => t.Value == col).Select(t => t.Key)).ToList())
            .ToList();
        List<List<string>> order = Barycenter.Order(initial, segments, e => laneOf[e]);

        // Cables leaving from the same output port(s) form a trunk: one exit on the box, one first turn, then they branch.
        Dictionary<EdgeKey, string> trunkOf = merged.ToDictionary(
            g => g.Key,
            g => $"{g.Key.From}|{g.Key.Signal}|" + string.Join(",", g.Select(c => $"{c.Cable.From.ModuleId}:{c.Cable.From.PortId}").Distinct().Order(StringComparer.Ordinal)));

        // Box height: enough room for one arrow every portSpacing on its busiest side.
        Dictionary<string, double> heightOf = anchorOf.Keys.ToDictionary(k => k, k =>
            Math.Max(NodeHeight, (Math.Max(merged.Where(g => g.Key.From == k).Select(g => trunkOf[g.Key]).Distinct().Count(), merged.Count(g => g.Key.To == k)) + 1) * portSpacing));
        Dictionary<string, string> predecessorOf = segments.Where(s => trackColumn.ContainsKey(s.V)).ToDictionary(s => s.V, s => s.U);

        List<DiagramBand> bands = new List<DiagramBand>();
        Dictionary<string, (int Column, double Y)> positions = new Dictionary<string, (int Column, double Y)>();
        Dictionary<string, double> trackY = new Dictionary<string, double>();
        double y = margin;
        for (int lane = 0; lane < lanes.Count; lane++)
        {
            double top = y + bandHeaderHeight;
            double bottom = top + NodeHeight;
            double[] free = new double[columns];
            for (int col = 0; col < columns; col++)
            {
                List<string> here = order[col].Where(e => laneOf[e] == lane && !feedbackTracks.Contains(e)).ToList();
                double cursor = top;
                for (int i = 0; i < here.Count; i++)
                {
                    string element = here[i];
                    if (trackColumn.ContainsKey(element))
                    {
                        // Trailing tracks keep the height they come in at, so a line runs straight when it can.
                        double line = cursor;
                        if (predecessorOf.TryGetValue(element, out string? before) && here.Skip(i + 1).All(trackColumn.ContainsKey))
                        {
                            line = Math.Max(cursor, trackY.TryGetValue(before, out double previous) ? previous : positions[before].Y + (heightOf[before] / 2));
                        }
                        trackY[element] = line;
                        cursor = line + trackSpacing;
                        bottom = Math.Max(bottom, line + 4);
                        continue;
                    }
                    foreach (string key in stackOf[element])
                    {
                        positions[key] = (col, cursor);
                        bottom = Math.Max(bottom, cursor + heightOf[key]);
                        cursor += heightOf[key] + rowSpacing;
                    }
                }
                free[col] = cursor;
            }

            // A feedback cable runs back in one straight line, under everything in the columns it spans.
            foreach (Plan plan in plans.Values.Where(p => p.Feedback && p.Tracks.Count > 0 && laneOf[p.Tracks[0]] == lane))
            {
                List<int> spanned = plan.Tracks.Select(t => trackColumn[t]).ToList();
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
        double watchersBottom = 0;
        if (watchers.Count > 0)
        {
            double Wanted(string watcher) => merged.Where(g => g.Key.To == watcher).Select(g => Center(g.Key.From)).DefaultIfEmpty(margin).Average();
            double cursor = margin + bandHeaderHeight;
            foreach (string watcher in watchers.OrderBy(Wanted))
            {
                double top = Math.Max(cursor, Wanted(watcher) - (heightOf[watcher] / 2));
                positions[watcher] = (columns, top);
                cursor = top + heightOf[watcher] + rowSpacing;
            }
            watchersBottom = Math.Max(y - 8, cursor - rowSpacing + bandPadding);
            y = Math.Max(y, watchersBottom + 8);
        }

        // Ports: several cables on one side of a box are spread along it, sorted by where the line goes next, so they
        // don't cross at the box. Then the height a cable runs at in each column: out port, tracks, in port.
        double Center(string key) => positions[key].Y + (heightOf[key] / 2);
        double NextY(EdgeKey key) => plans.TryGetValue(key, out Plan? plan) && plan.Tracks.Count > 0 ? trackY[plan.Tracks[0]] : Center(key.To);
        double PreviousY(EdgeKey key) => plans.TryGetValue(key, out Plan? plan) && plan.Tracks.Count > 0 ? trackY[plan.Tracks[^1]] : Center(key.From);
        Dictionary<EdgeKey, double> outY = new Dictionary<EdgeKey, double>();
        Dictionary<EdgeKey, double> inY = new Dictionary<EdgeKey, double>();
        foreach (IGrouping<string, IGrouping<EdgeKey, AnalyzedCable>> side in merged.GroupBy(g => g.Key.From))
        {
            List<List<EdgeKey>> trunks = side.GroupBy(g => trunkOf[g.Key])
                .Select(t => t.Select(g => g.Key).ToList())
                .OrderBy(t => t.Average(NextY)).ThenBy(t => t[0].Signal)
                .ToList();
            Spread(trunks, positions[side.Key].Y, heightOf[side.Key], outY);
        }
        foreach (IGrouping<string, IGrouping<EdgeKey, AnalyzedCable>> side in merged.GroupBy(g => g.Key.To))
        {
            Spread(side.Select(g => g.Key).OrderBy(PreviousY).ThenBy(k => k.Signal).Select(k => new List<EdgeKey> { k }).ToList(), positions[side.Key].Y, heightOf[side.Key], inY);
        }
        Dictionary<EdgeKey, List<double>> levels = plans.ToDictionary(
            kv => kv.Key,
            kv => new[] { outY[kv.Key] }.Concat(kv.Value.Tracks.Select(t => trackY[t])).Append(inY[kv.Key]).ToList());

        // Vertical runs: every change of height, and both U-turns of a feedback cable, in the gap where it happens.
        List<Turn> turns = new List<Turn>();
        foreach ((EdgeKey key, Plan plan) in plans)
        {
            List<double> ys = levels[key];
            for (int i = 0; i < plan.Gaps.Count; i++)
            {
                bool uTurn = plan.Feedback && (i == 0 || i == plan.Gaps.Count - 1);
                if (uTurn || Math.Abs(ys[i + 1] - ys[i]) >= 1)
                {
                    turns.Add(new Turn(plan.Gaps[i], key, i, ys[i], ys[i + 1]));
                }
            }
        }

        // Each gap between two columns is as wide as its vertical runs need, so busy gaps don't squeeze lines together.
        // Gaps -1 and columns - 1, outside the first and last columns, only exist for feedback cables.
        // Port tabs: one per cable line arriving, one per trunk leaving (cables plugged into a stacked insert's side loop
        // have none). They take room in the gaps, next to the boxes.
        Dictionary<EdgeKey, PortTab> inTabs = new Dictionary<EdgeKey, PortTab>();
        Dictionary<EdgeKey, PortTab> outTabs = new Dictionary<EdgeKey, PortTab>();
        foreach (IGrouping<EdgeKey, AnalyzedCable> edge in merged.Where(g => portTabs && plans.ContainsKey(g.Key)))
        {
            string arriving = PortTabs.Text(edge.Select(c => c.ToPort), edge.Key.Signal);
            string leaving = PortTabs.Text(edge.Select(c => c.FromPort), edge.Key.Signal);
            inTabs[edge.Key] = new PortTab(false, inY[edge.Key], PortTabs.Width(arriving), arriving, edge.Key.Signal, string.Join("\n", edge.Select(c => c.ToPort).Distinct()));
            outTabs[edge.Key] = new PortTab(true, outY[edge.Key], PortTabs.Width(leaving), leaving, edge.Key.Signal, string.Join("\n", edge.Select(c => c.FromPort).Distinct()));
        }
        double MaxTab(Dictionary<EdgeKey, PortTab> tabs, Func<EdgeKey, string> box, int col) =>
            tabs.Where(t => positions[box(t.Key)].Column == col).Select(t => t.Value.Width).DefaultIfEmpty(0).Max();
        double OutTabs(int col) => MaxTab(outTabs, k => k.From, col);
        double InTabs(int col) => MaxTab(inTabs, k => k.To, col);

        // The first turns of a trunk's cables, when they go the same way, share one vertical run.
        string? TrunkOf(Turn turn) => turn.Part == 0 && !plans[turn.Key].Feedback ? trunkOf[turn.Key] : null;
        double GapWidth(int gap)
        {
            int count = Bundles(turns.Where(t => t.Gap == gap), TrunkOf).Count;
            double needed = gapPadding + (count * channelPitch) + OutTabs(gap) + InTabs(gap + 1);
            return gap < 0 || gap >= drawnColumns - 1 ? (count > 0 ? needed : 0) : Math.Max(minGap, needed);
        }
        List<double> columnX = new List<double> { margin + GapWidth(-1) };
        for (int gap = 0; gap + 1 < drawnColumns; gap++)
        {
            columnX.Add(columnX[gap] + NodeWidth + GapWidth(gap));
        }
        double GapLeft(int gap) => gap < 0 ? margin : columnX[gap] + NodeWidth;
        double GapRight(int gap) => gap + 1 < drawnColumns ? columnX[gap + 1] : GapLeft(gap) + GapWidth(gap);
        Dictionary<(EdgeKey Key, int Part), double> channels = AssignChannels(turns, TrunkOf, g => GapLeft(g) + OutTabs(g), g => GapRight(g) - InTabs(g + 1));

        // The watchers' column, clear of their port tabs; the horizontal bands stop short of it.
        if (watchers.Count > 0)
        {
            PatchGroup watchersGroup = groups.First(g => g.Band == Band.Monitor);
            double left = columnX[columns] - InTabs(columns) - 12;
            bands.Add(new DiagramBand(Band.Monitor, HasOwnLane(watchersGroup) ? watchersGroup.Key : null, PatchGrouping.BoxTitle(Band.Monitor), margin, watchersBottom - margin)
            {
                X = left,
                Width = columnX[columns] + NodeWidth + 16 - left,
            });
        }

        List<DiagramNode> nodes = new List<DiagramNode>();
        foreach (PatchGroup group in groups)
        {
            if (!ShowsModules(group))
            {
                string key = GroupKey(group.Key);
                (int gc, double gy) = positions[key];
                nodes.Add(new DiagramNode(key, null, group.Key, true, FoldedTitle(group, analysis), FoldedSubtitle(group, analysis),
                    group.Role, group.Band, false, group.Members.Select(id => analysis.Module(id).Title).ToList(), columnX[gc], gy, NodeWidth, heightOf[key])
                {
                    LibraryPanels = libraryLinks ? LibraryPanels(group, analysis) : Array.Empty<LibraryPanel>(),
                    UnlistedCount = libraryLinks ? group.Members.Count(id => !Core.Catalog.VcvLibrary.IsListed(analysis.Module(id).Plugin)) : 0,
                });
                continue;
            }
            foreach (long id in group.Members.Where(id => positions.ContainsKey(ModuleKey(id))))
            {
                AnalyzedModule m = analysis.Module(id);
                (int mc, double my) = positions[ModuleKey(id)];
                string origin = m.InsertOf is long host ? $"insert of {analysis.Module(host).Title}"
                    : m.Expanders.Count > 0 ? $"{m.Plugin} + {string.Join(", ", m.Expanders)}" : m.Plugin;
                // "MIDI" or "UTILITY" would say less than the module's own name: those keep it.
                bool byFunction = functionNames && m.Function is not (Core.Catalog.ModuleFunction.Midi or Core.Catalog.ModuleFunction.Audio or Core.Catalog.ModuleFunction.Utility);
                string moduleTitle = byFunction ? FunctionTitle(m) : m.Title;
                string subtitle = byFunction ? $"{m.Title} · {origin}" : $"{origin} · {Core.Catalog.ModuleFunctions.Label(m.Function)}";
                nodes.Add(new DiagramNode(ModuleKey(id), id, group.Key, false, moduleTitle, subtitle, m.Role, m.Band, m.InsertOf is not null,
                    ModuleDetails(analysis, m), columnX[mc], my, NodeWidth, heightOf[ModuleKey(id)])
                {
                    Function = m.Function,
                    LibraryKey = libraryLinks && Core.Catalog.VcvLibrary.IsListed(m.Plugin) ? Core.Catalog.VcvLibrary.Key(m.Plugin, m.Module.Model) : null,
                });
            }
        }
        if (monitors == MonitorView.Badge)
        {
            nodes = nodes.Select(n => n with { Watchers = WatchersOf(analysis, n, visible, groupOf) }).ToList();
        }
        nodes = nodes.Select(n => n with
        {
            Tabs = inTabs.Where(t => t.Key.To == n.Key).Select(t => t.Value)
                .Concat(outTabs.Where(t => t.Key.From == n.Key).Select(t => t.Value).DistinctBy(t => t.Y))
                .ToList(),
        }).ToList();
        Dictionary<string, DiagramNode> nodeByKey = nodes.ToDictionary(n => n.Key);

        Dictionary<string, List<LabelPlacement.Run>> runs = new Dictionary<string, List<LabelPlacement.Run>>();
        double TabWidth(Dictionary<EdgeKey, PortTab> tabs, EdgeKey key) => tabs.TryGetValue(key, out PortTab? tab) ? tab.Width : 0;
        List<DiagramEdge> edges = DrawEdges(analysis, merged, nodeByKey, plans, levels, outY, inY, channels, intents, runs, k => TabWidth(outTabs, k), k => TabWidth(inTabs, k));
        edges = LabelPlacement.Place(edges, runs, nodes);
        double width = GapRight(drawnColumns - 1) + margin;
        return new DiagramLayout(title, width, y - 8 + margin, bands, nodes, edges, groups, analysis.Diagnostics);
    }

    /// <summary>"Voice 1" from "Voice 1: LVCO ← ADDR-SEQ": the box inside already says the rest.</summary>
    private static string VoiceLaneTitle(string groupTitle) =>
        groupTitle.IndexOf(": ", StringComparison.Ordinal) is int colon and > 0 ? groupTitle[..colon] : groupTitle;

    /// <summary>"Scope 'Channel A' ← SLEW #1 'Signal'" for every monitor cable leaving this box (or a module folded in it).</summary>
    private static IReadOnlyList<string> WatchersOf(PatchAnalysis analysis, DiagramNode node, HashSet<long> visible, Dictionary<long, PatchGroup> groupOf) =>
        analysis.Cables
            .Where(c => visible.Contains(c.Cable.From.ModuleId) && !visible.Contains(c.Cable.To.ModuleId)
                && analysis.Module(c.Cable.To.ModuleId).Band == Band.Monitor
                && (node.ModuleId == c.Cable.From.ModuleId || (node.IsFolded && groupOf[c.Cable.From.ModuleId].Key == node.Group)))
            .Select(c => $"{analysis.Module(c.Cable.To.ModuleId).Title} '{c.ToPort}' ← {analysis.Module(c.Cable.From.ModuleId).Title} '{c.FromPort}'")
            .ToList();

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

    private static List<string> ModuleDetails(PatchAnalysis analysis, AnalyzedModule module)
    {
        List<string> lines = new List<string> { $"{module.Title} ({module.Plugin} · {module.Role})" };
        if (module.Expanders.Count > 0)
        {
            lines.Add($"with expanders: {string.Join(", ", module.Expanders)}");
        }
        lines.AddRange(analysis.Cables.Where(c => c.Cable.To.ModuleId == module.Module.Id).Select(c => $"in  {c.ToPort} ← {analysis.Module(c.Cable.From.ModuleId).Title}"));
        lines.AddRange(analysis.Cables.Where(c => c.Cable.From.ModuleId == module.Module.Id).Select(c => $"out {c.FromPort} → {analysis.Module(c.Cable.To.ModuleId).Title}"));
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

    /// <summary>Inserts of a module, in rack order.</summary>
    private static IEnumerable<long> InsertsOf(PatchAnalysis analysis, long hostId) =>
        analysis.Modules.Where(m => m.InsertOf == hostId).OrderBy(m => m.Module.Row).ThenBy(m => m.Module.Column).Select(m => m.Module.Id);

    /// <summary>
    /// Columns a cable runs through on tracks, and the gaps where it may turn (one between each two heights it runs at).
    /// A feedback cable's tracks go from its source's column back to its target's.
    /// </summary>
    private sealed record Plan(List<string> Tracks, List<int> Gaps, bool Feedback);

    /// <summary>A vertical run in a gap, from one height to the next; Part is its index along the cable.</summary>
    private sealed record Turn(int Gap, EdgeKey Key, int Part, double YFrom, double YTo);

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
                double sx = down ? from.X + from.Width : from.X;
                double ex = down ? to.X + to.Width + ArrowLength : to.X - ArrowLength;
                double side = down ? 36 : -36;
                path = string.Create(CultureInfo.InvariantCulture,
                    $"M {sx:0.#} {y1:0.#} C {sx + side:0.#} {y1:0.#}, {ex + side:0.#} {y2:0.#}, {ex:0.#} {y2:0.#}");
                labelX = sx + (side * 0.75);
                labelY = (y1 + y2) / 2;
            }

            List<AnalyzedCable> groupCables = group.ToList();
            AnalyzedCable firstCable = groupCables[0];
            string intent = groupCables.Count == 1
                ? intents is not null && intents.TryGetValue(firstCable.Cable.Id, out string? custom) ? custom : firstCable.SuggestedIntent
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