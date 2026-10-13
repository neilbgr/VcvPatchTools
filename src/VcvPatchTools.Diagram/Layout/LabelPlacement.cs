namespace VcvPatchTools.Diagram.Layout;

/// <summary>
/// Moves cable labels so they don't cover each other or a box (greedy, in drawing order):
/// each label takes the first free spot along its own cable, by preference
///  1. the middle of a vertical run (where it was drawn so far: the turn is what tells cables apart),
///  2. the middle of a horizontal run long enough to hold it,
///  3. elsewhere on a vertical run,
///  4. the start or the end of a long horizontal run,
///  5. anywhere else along the cable, in small steps.
/// Longest labels go first, while there is most room. When no spot is free, one that only covers a box,
/// else the one overlapping least.
/// Feedback labels and insert side loops keep their place.
/// </summary>
public static class LabelPlacement
{
    /// <summary>A straight piece of a cable, horizontal or vertical.</summary>
    public sealed record Run(double X1, double Y1, double X2, double Y2);

    private sealed record Box(double Left, double Top, double Right, double Bottom)
    {
        public double Overlap(Box other) =>
            Math.Max(0, Math.Min(Right, other.Right) - Math.Max(Left, other.Left)) * Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top));
    }

    // 11px labels: average glyph width, and the text sits just above the point it is placed at (see SvgRenderer.IntentY).
    private const double charWidth = 6;
    private const double clearance = 3;
    private const double runMargin = 8;
    private const double step = 8;
    private static readonly double[] offMiddle = { 0.3, 0.7, 0.15, 0.85 };

    public static List<DiagramEdge> Place(List<DiagramEdge> edges, Dictionary<string, List<Run>> runs, IReadOnlyList<DiagramNode> nodes)
    {
        // A box, and the arrowheads entering it on its left.
        List<Box> boxes = nodes.Select(n => new Box(n.X - n.InTabsWidth - LayeredLayout.ArrowLength, n.Y, n.X + n.Width + n.OutTabsWidth, n.Y + n.Height)).ToList();
        List<Box> labels = new List<Box>();
        foreach (DiagramEdge edge in edges.Where(e => !Movable(e, runs)))
        {
            if (edge.Intent.Length > 0)
            {
                labels.Add(LabelBox(edge.Label, edge.LabelX, edge.IsFeedback ? edge.LabelY + 17 : edge.LabelY));
            }
            if (edge.IsFeedback)
            {
                labels.Add(LabelBox(Render.SvgRenderer.FeedbackTag, edge.LabelX, edge.LabelY));
            }
        }

        Dictionary<string, (double X, double Y)> spots = new Dictionary<string, (double, double)>();
        foreach (DiagramEdge edge in edges.Where(e => Movable(e, runs)).OrderByDescending(e => e.Label.Length))
        {
            List<Box> candidates = Candidates(edge, runs[edge.Key]).Select(c => LabelBox(edge.Label, c.X, c.Y)).ToList();
            // Only what lies where the candidates are can overlap them (the others add nothing to the sums).
            Box area = new Box(candidates.Min(c => c.Left), candidates.Min(c => c.Top), candidates.Max(c => c.Right), candidates.Max(c => c.Bottom));
            Box spot = Best(candidates, labels.Where(l => l.Overlap(area) > 0).ToList(), boxes.Where(b => b.Overlap(area) > 0).ToList());
            labels.Add(spot);
            spots[edge.Key] = ((spot.Left + spot.Right) / 2, spot.Bottom);
        }
        return edges.Select(e => spots.TryGetValue(e.Key, out (double X, double Y) s) ? e with { LabelX = s.X, LabelY = s.Y } : e).ToList();
    }

    /// <summary>
    /// Free of everything, else at least of other labels (a label over a box still reads, two labels don't), else the
    /// least overlap, labels counting most. The first such candidate; each overlap is measured once, and only when needed.
    /// </summary>
    private static Box Best(List<Box> candidates, List<Box> labels, List<Box> boxes)
    {
        static double Covered(Box candidate, List<Box> others) => others.Sum(o => o.Overlap(candidate));
        double[] onLabels = new double[candidates.Count];
        double?[] onBoxes = new double?[candidates.Count];
        double OnBoxes(int i) => onBoxes[i] ??= Covered(candidates[i], boxes);
        for (int i = 0; i < candidates.Count; i++)
        {
            onLabels[i] = Covered(candidates[i], labels);
            if (onLabels[i] == 0 && OnBoxes(i) == 0)
            {
                return candidates[i];
            }
        }
        int freeOfLabels = Array.IndexOf(onLabels, 0d);
        return freeOfLabels >= 0 ? candidates[freeOfLabels] : candidates[Enumerable.Range(0, candidates.Count).MinBy(i => (3 * onLabels[i]) + OnBoxes(i))];
    }

    private static bool Movable(DiagramEdge edge, Dictionary<string, List<Run>> runs) => edge.Intent.Length > 0 && runs.ContainsKey(edge.Key);

    private static List<(double X, double Y)> Candidates(DiagramEdge edge, List<Run> runs)
    {
        double half = (edge.Label.Length * charWidth / 2) + clearance;
        List<Run> vertical = runs.Where(r => r.X1 == r.X2).ToList();
        List<Run> horizontal = runs.Where(r => r.Y1 == r.Y2 && Math.Abs(r.X2 - r.X1) >= (2 * half) + runMargin).ToList();
        (double X, double Y) Along(Run r, double f) => (r.X1 + ((r.X2 - r.X1) * f), r.Y1 + ((r.Y2 - r.Y1) * f));

        List<(double X, double Y)> candidates = new List<(double, double)> { (edge.LabelX, edge.LabelY) };
        candidates.AddRange(vertical.Select(r => Along(r, 0.5)));
        candidates.AddRange(horizontal.Select(r => Along(r, 0.5)));
        candidates.AddRange(vertical.Where(r => Math.Abs(r.Y2 - r.Y1) > 4 * runMargin).SelectMany(r => offMiddle.Select(f => Along(r, f))));
        foreach (Run r in horizontal)
        {
            double left = Math.Min(r.X1, r.X2);
            double right = Math.Max(r.X1, r.X2);
            candidates.Add((left + half + runMargin, r.Y1));
            candidates.Add((right - half - runMargin, r.Y1));
        }
        foreach (Run r in runs)
        {
            double length = Math.Abs(r.X2 - r.X1) + Math.Abs(r.Y2 - r.Y1);
            candidates.AddRange(Enumerable.Range(1, (int)(length / step)).Select(i => Along(r, i * step / length)));
        }
        return candidates.Distinct().ToList();
    }

    /// <summary>Where a label drawn for a cable point at (x, y) ends up: centered on x, just above y.</summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(string text, double x, double y)
    {
        Box box = LabelBox(text, x, y);
        return (box.Left, box.Top, box.Right, box.Bottom);
    }

    private static Box LabelBox(string text, double x, double y)
    {
        double half = (text.Length * charWidth / 2) + clearance;
        return new Box(x - half, y - 15, x + half, y);
    }
}