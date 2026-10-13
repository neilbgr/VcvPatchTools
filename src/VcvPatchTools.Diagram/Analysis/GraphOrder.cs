namespace VcvPatchTools.Diagram.Analysis;

public static class GraphOrder
{
    /// <summary>
    /// Kahn topological sort, stable on the input order. Feedback loops (frequent in patches) are broken with the
    /// greedy rule of Eades, Lin &amp; Smyth (1993), the usual first step of a Sugiyama layout: when no node is free,
    /// release the one with the most outgoing minus incoming edges among the remaining ones (earliest on a tie),
    /// which keeps the cables that end up going backwards few. Every node is returned exactly once.
    /// </summary>
    public static List<long> Topological(IReadOnlyList<long> nodes, IReadOnlyList<(long From, long To)> edges)
    {
        // Nodes by index in the input order; the degrees count the edges from and to the nodes not placed yet.
        Dictionary<long, int> indexOf = Index(nodes);
        int count = nodes.Count;
        List<int>[] outgoing = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        List<int>[] incoming = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        int[] inDegree = new int[count];
        int[] outDegree = new int[count];
        foreach ((long from, long to) in edges)
        {
            if (from != to && indexOf.TryGetValue(from, out int f) && indexOf.TryGetValue(to, out int t))
            {
                outgoing[f].Add(t);
                incoming[t].Add(f);
                inDegree[t]++;
                outDegree[f]++;
            }
        }

        bool[] placed = new bool[count];
        SortedSet<int> free = new SortedSet<int>(Enumerable.Range(0, count).Where(i => inDegree[i] == 0));
        List<long> result = new List<long>(count);
        while (result.Count < count)
        {
            // The greedy rule only when no node is free.
            int next = free.Count > 0 ? free.Min : Greediest(placed, inDegree, outDegree);
            free.Remove(next);
            placed[next] = true;
            result.Add(nodes[next]);
            foreach (int to in outgoing[next].Where(to => !placed[to]))
            {
                if (--inDegree[to] == 0)
                {
                    free.Add(to);
                }
            }
            foreach (int from in incoming[next].Where(from => !placed[from]))
            {
                outDegree[from]--;
            }
        }
        return result;
    }

    /// <summary>
    /// Feedback loops: groups of nodes that can all reach each other (strongly connected components of two or more
    /// nodes, Tarjan 1972), in the input order.
    /// </summary>
    public static List<List<long>> Loops(IReadOnlyList<long> nodes, IReadOnlyList<(long From, long To)> edges)
    {
        Dictionary<long, int> inputIndex = Index(nodes);
        Dictionary<long, List<long>> next = nodes.ToDictionary(n => n, n => new List<long>());
        foreach ((long from, long to) in edges)
        {
            if (from != to && next.TryGetValue(from, out List<long>? targets) && !targets.Contains(to))
            {
                targets.Add(to);
            }
        }
        Dictionary<long, int> index = new Dictionary<long, int>();
        Dictionary<long, int> low = new Dictionary<long, int>();
        Stack<long> stack = new Stack<long>();
        HashSet<long> onStack = new HashSet<long>();
        List<List<long>> loops = new List<List<long>>();

        void Visit(long node)
        {
            index[node] = low[node] = index.Count;
            stack.Push(node);
            onStack.Add(node);
            foreach (long to in next[node].Where(next.ContainsKey))
            {
                if (!index.ContainsKey(to))
                {
                    Visit(to);
                    low[node] = Math.Min(low[node], low[to]);
                }
                else if (onStack.Contains(to))
                {
                    low[node] = Math.Min(low[node], index[to]);
                }
            }
            if (low[node] != index[node])
            {
                return;
            }
            List<long> component = new List<long>();
            long member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (member != node);
            if (component.Count > 1)
            {
                loops.Add(component.OrderBy(m => inputIndex[m]).ToList());
            }
        }

        foreach (long node in nodes.Where(n => !index.ContainsKey(n)))
        {
            Visit(node);
        }
        return loops.OrderBy(l => inputIndex[l[0]]).ToList();
    }

    /// <summary>The node not placed yet with the most outgoing minus incoming edges, the earliest on a tie.</summary>
    private static int Greediest(bool[] placed, int[] inDegree, int[] outDegree)
    {
        int best = -1;
        for (int i = 0; i < placed.Length; i++)
        {
            if (!placed[i] && (best < 0 || outDegree[i] - inDegree[i] > outDegree[best] - inDegree[best]))
            {
                best = i;
            }
        }
        return best;
    }

    private static Dictionary<long, int> Index(IReadOnlyList<long> nodes)
    {
        Dictionary<long, int> index = new Dictionary<long, int>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            index.Add(nodes[i], i);
        }
        return index;
    }

    /// <summary>Longest-path depth of each node (sources at 0), loops broken the same way as <see cref="Topological"/>.</summary>
    public static Dictionary<long, int> Depths(IReadOnlyList<long> nodes, IReadOnlyList<(long From, long To)> edges)
    {
        List<long> order = Topological(nodes, edges);
        Dictionary<long, int> position = order.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        Dictionary<long, int> depth = nodes.ToDictionary(n => n, n => 0);
        ILookup<long, long> targets = edges.ToLookup(e => e.From, e => e.To);
        foreach (long node in order)
        {
            foreach (long to in targets[node])
            {
                // Only forward edges in the chosen order count, so loops can't grow depths forever.
                if (position.TryGetValue(to, out int toPos) && toPos > position[node])
                {
                    depth[to] = Math.Max(depth[to], depth[node] + 1);
                }
            }
        }
        return depth;
    }
}