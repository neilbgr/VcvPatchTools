using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Diagram.Analysis;

/// <summary>
/// Where each audio processor sits, from the audio cables alone:
///   insert / send-return: fed by a module M (through a "send" output, or a loop) and feeding back into M
///                         (through a "return" input) → belongs to M, wherever M is;
///   voice:                linked by audio cables to a source without going through a mixer, unless that mixer only
///                         gathers sources playing the same notes (the oscillators of one keyboard zone…);
///   bus:                  everything else (after a mixer).
/// </summary>
internal sealed class AudioTopology
{
    /// <summary>Insert/send-return module → its host.</summary>
    public Dictionary<long, long> InsertHost { get; } = new Dictionary<long, long>();

    /// <summary>Each voice's modules in signal order (inserts excluded: they follow their host).</summary>
    public List<List<long>> Voices { get; } = new List<List<long>>();

    /// <param name="pitchOrigin">Output port a source's notes come from (see <see cref="PatchAnalyzer"/>), null when it plays no notes.</param>
    public AudioTopology(IReadOnlyList<long> rackOrder, IReadOnlyList<AnalyzedCable> cables, IReadOnlyDictionary<long, Role> roleById, Func<long, PortRef?> pitchOrigin)
    {
        List<AnalyzedCable> audio = cables.Where(c => c.Signal == SignalType.Audio && c.Cable.From.ModuleId != c.Cable.To.ModuleId).ToList();
        FindInserts(audio, roleById);

        // Voices: union of sources/modifiers/effects over audio cables, never through a mixer or an insert loop.
        bool IsProcessor(long id) => roleById[id] is Role.Source or Role.Modifier or Role.Effect && !InsertHost.ContainsKey(id);
        Dictionary<long, long> parent = rackOrder.Where(IsProcessor).ToDictionary(id => id, id => id);
        long Find(long id) => parent[id] == id ? id : parent[id] = Find(parent[id]);
        void Union()
        {
            foreach (AnalyzedCable cable in audio)
            {
                long from = cable.Cable.From.ModuleId;
                long to = cable.Cable.To.ModuleId;
                if (parent.ContainsKey(from) && parent.ContainsKey(to) && Find(from) != Find(to))
                {
                    parent[Find(from)] = Find(to);
                }
            }
        }
        Union();

        // A mixer fed only by voices playing the same notes (two oscillators on one keyboard zone) mixes one voice:
        // the voice goes on through it, up to where different notes meet. Repeat for mixers of mixers.
        HashSet<PortRef?> OriginsOf(long root) => parent.Keys
            .Where(id => Find(id) == root && roleById[id] == Role.Source)
            .Select(pitchOrigin)
            .ToHashSet();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (long mixer in rackOrder.Where(id => roleById[id] == Role.Mixer && !InsertHost.ContainsKey(id) && !parent.ContainsKey(id)))
            {
                List<long> feeders = audio.Where(c => c.Cable.To.ModuleId == mixer).Select(c => c.Cable.From.ModuleId).Distinct().ToList();
                if (feeders.Count == 0 || !feeders.All(parent.ContainsKey))
                {
                    continue;
                }
                // At least two voices, each made of sources playing notes, all the same ones: a mixer fed by a single
                // voice is that voice going to the bus, and one fed by a plain processor mixes something else in.
                List<HashSet<PortRef?>> perVoice = feeders.Select(Find).Distinct().Select(OriginsOf).ToList();
                List<PortRef?> origins = perVoice.SelectMany(o => o).Distinct().ToList();
                if (perVoice.Count >= 2 && perVoice.All(o => o.Count > 0) && origins.Count == 1 && origins[0] is not null)
                {
                    parent[mixer] = mixer;
                    Union();
                    changed = true;
                }
            }
        }

        HashSet<long> audioTouched = audio.SelectMany(c => new[] { c.Cable.From.ModuleId, c.Cable.To.ModuleId }).ToHashSet();
        IEnumerable<List<long>> components = parent.Keys
            .Where(audioTouched.Contains)
            .GroupBy(Find)
            .Select(g => g.ToList())
            .Where(g => g.Any(id => roleById[id] == Role.Source))
            .OrderBy(g => g.Min(id => IndexOf(rackOrder, id)));
        foreach (List<long> component in components)
        {
            HashSet<long> set = component.ToHashSet();
            List<(long From, long To)> edges = audio.Where(c => set.Contains(c.Cable.From.ModuleId) && set.Contains(c.Cable.To.ModuleId))
                .Select(c => (c.Cable.From.ModuleId, c.Cable.To.ModuleId)).Distinct().ToList();
            Voices.Add(GraphOrder.Topological(component.OrderBy(id => IndexOf(rackOrder, id)).ToList(), edges));
        }
    }

    /// <summary>
    /// From every audio output of a host M that is a "send" (by name) or that starts a loop back into M,
    /// the modules that can be reached from it and that lead back into M are M's inserts.
    /// </summary>
    private void FindInserts(List<AnalyzedCable> audio, IReadOnlyDictionary<long, Role> roleById)
    {
        Dictionary<long, List<long>> next = audio.GroupBy(c => c.Cable.From.ModuleId).ToDictionary(g => g.Key, g => g.Select(c => c.Cable.To.ModuleId).Distinct().ToList());

        // Outputs named "send" first: they say who the host is, so a plain loop can't flip host and insert.
        IEnumerable<AnalyzedCable> starts = audio.OrderBy(c => c.FromPort.Contains("send", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        foreach (AnalyzedCable start in starts)
        {
            long host = start.Cable.From.ModuleId;
            long first = start.Cable.To.ModuleId;
            if (roleById[first] is not (Role.Modifier or Role.Effect) || InsertHost.ContainsKey(first) || InsertHost.ContainsKey(host)
                || InsertHost.ContainsValue(first))
            {
                continue;
            }

            HashSet<long> chain = ChainBackTo(host, first, next, roleById);
            // An insert only hears its host (or the rest of its own chain): a module also fed from elsewhere is in the main path.
            bool closed = chain.Count > 0 && audio
                .Where(c => chain.Contains(c.Cable.To.ModuleId))
                .All(c => c.Cable.From.ModuleId == host || chain.Contains(c.Cable.From.ModuleId));
            if (closed)
            {
                foreach (long member in chain)
                {
                    InsertHost.TryAdd(member, host);
                }
            }
        }
    }

    /// <summary>Modules reachable from <paramref name="first"/> (not through the host or a mixer) that can get back to the host.</summary>
    private static HashSet<long> ChainBackTo(long host, long first, Dictionary<long, List<long>> next, IReadOnlyDictionary<long, Role> roleById)
    {
        HashSet<long> reachable = new HashSet<long>();
        Stack<long> stack = new Stack<long>(new[] { first });
        while (stack.Count > 0)
        {
            long id = stack.Pop();
            if (id == host || roleById[id] is Role.Mixer or Role.Io || !reachable.Add(id))
            {
                continue;
            }
            foreach (long target in next.GetValueOrDefault(id) ?? new List<long>())
            {
                stack.Push(target);
            }
        }

        // Keep only those with a path back into the host.
        HashSet<long> backToHost = new HashSet<long>();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (long id in reachable.Where(id => !backToHost.Contains(id)))
            {
                List<long> targets = next.GetValueOrDefault(id) ?? new List<long>();
                if (targets.Contains(host) || targets.Any(backToHost.Contains))
                {
                    backToHost.Add(id);
                    changed = true;
                }
            }
        }
        return backToHost;
    }

    private static int IndexOf(IReadOnlyList<long> order, long id)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == id)
            {
                return i;
            }
        }
        return int.MaxValue;
    }
}