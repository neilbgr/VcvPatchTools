using VcvPatchTools.Diagram.Analysis;

namespace VcvPatchTools.Tests;

public class GraphOrderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void TopologicalOrderMatchesTheReferenceDefinition(int seed)
    {
        (List<long> nodes, List<(long From, long To)> edges) = RandomGraph(seed, 60, 120);

        Assert.Equal(ReferenceTopological(nodes, edges), GraphOrder.Topological(nodes, edges));
    }

    [Fact]
    public void TopologicalOrderIgnoresSelfLoopsAndUnknownNodes()
    {
        List<long> order = GraphOrder.Topological(new long[] { 5, 3, 9 }, new[] { (9L, 9L), (9L, 3L), (42L, 5L), (3L, 42L) });

        Assert.Equal(new long[] { 5, 9, 3 }, order);
    }

    [Fact]
    public void LoopsAreListedInInputOrder()
    {
        List<List<long>> loops = GraphOrder.Loops(new long[] { 7, 1, 4, 2 }, new[] { (4L, 2L), (2L, 4L), (1L, 7L), (7L, 1L) });

        Assert.Equal(new[] { new long[] { 7, 1 }, new long[] { 4, 2 } }, loops.Select(l => l.ToArray()));
    }

    private static (List<long> Nodes, List<(long From, long To)> Edges) RandomGraph(int seed, int nodeCount, int edgeCount)
    {
        Random random = new Random(seed);
        List<long> nodes = Enumerable.Range(0, nodeCount).Select(i => (long)((i * 37) % 101)).ToList();
        List<(long From, long To)> edges = Enumerable.Range(0, edgeCount)
            .Select(_ => (nodes[random.Next(nodeCount)], nodes[random.Next(nodeCount)]))
            .ToList();
        return (nodes, edges);
    }

    /// <summary>The definition, written plainly (quadratic): what the fast version must keep returning.</summary>
    private static List<long> ReferenceTopological(List<long> nodes, List<(long From, long To)> edges)
    {
        Dictionary<long, int> inDegree = nodes.ToDictionary(n => n, n => edges.Count(e => e.To == n && e.From != n && nodes.Contains(e.From)));
        List<long> remaining = nodes.ToList();
        List<long> result = new List<long>();
        while (remaining.Count > 0)
        {
            int Balance(long n) => edges.Count(e => e.From == n && e.To != n && remaining.Contains(e.To)) - edges.Count(e => e.To == n && e.From != n && remaining.Contains(e.From));
            long next = remaining.Any(n => inDegree[n] == 0)
                ? remaining.First(n => inDegree[n] == 0)
                : remaining.Select((n, i) => (n, i)).OrderByDescending(p => Balance(p.n)).ThenBy(p => p.i).First().n;
            remaining.Remove(next);
            result.Add(next);
            foreach ((long from, long to) in edges.Where(e => e.From == next && e.To != next && remaining.Contains(e.To)))
            {
                inDegree[to]--;
            }
        }
        return result;
    }
}