using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Diagram.Analysis;

/// <summary>
/// Turns a raw patch into an explained one: names ports, types cables, gives each module a role and a band,
/// groups the audio path into voices (sources and what processes them before a mixer), tells bus processing
/// (after a mixer) and inserts / send-returns apart, and lists anything suspicious.
/// Modules without any cable (blanks, spare modules) are left out; a docked expander is folded into its base
/// (see <see cref="ExpanderChains"/>): its cables attach to the base, its ports named after it.
/// </summary>
public sealed class PatchAnalyzer
{
    private readonly PortCatalog ports;
    private readonly RoleCatalog roles;
    private readonly SignalClassifier classifier;

    public PatchAnalyzer(PortCatalog ports, RoleCatalog roles, SignalClassifier? classifier = null)
    {
        this.ports = ports;
        this.roles = roles;
        this.classifier = classifier ?? new SignalClassifier();
    }

    public static PatchAnalyzer CreateDefault() => new PatchAnalyzer(PortCatalog.LoadEmbedded(), RoleCatalog.LoadEmbedded());

    public PatchAnalysis Analyze(PatchDocument patch)
    {
        List<string> diagnostics = new List<string>();
        Dictionary<long, PatchModule> byId = patch.Modules.ToDictionary(m => m.Id);
        Dictionary<long, long> baseOf = ExpanderChains.BaseOf(patch.Modules, ports);
        HashSet<long> cabled = patch.Cables.SelectMany(c => new[] { c.From.ModuleId, c.To.ModuleId }).Where(byId.ContainsKey)
            .Select(id => baseOf.GetValueOrDefault(id, id)).ToHashSet();
        List<PatchModule> shown = patch.Modules.Where(m => cabled.Contains(m.Id)).ToList();
        Dictionary<long, string> titles = Titles(shown);

        Dictionary<long, Role> roleById = new Dictionary<long, Role>();
        foreach (PatchModule module in shown)
        {
            ModuleInfo info = ports.Get(module.Plugin, module.Model);
            if (info == ModuleInfo.Empty)
            {
                diagnostics.Add($"{titles[module.Id]}: {module.CatalogKey} is not in the port catalog");
            }

            (Role role, bool guessed) = roles.Resolve(module.Plugin, module.Model, info);
            roleById[module.Id] = role;
            if (guessed)
            {
                diagnostics.Add($"{titles[module.Id]}: role guessed as {role} (add {module.CatalogKey} to catalog/roles.json)");
            }
        }

        List<AnalyzedCable> cables = TypeCables(patch, byId, baseOf, titles, roleById, diagnostics);
        ResolveContextualRoles(shown, cables, roleById);

        // Host modules that send audio into the patch act as audio sources (external audio in).
        Dictionary<long, Role> audioRole = new Dictionary<long, Role>(roleById);
        foreach (PatchModule module in shown.Where(m => roleById[m.Id] == Role.Io))
        {
            if (cables.Any(c => c.Cable.From.ModuleId == module.Id && c.Signal == SignalType.Audio))
            {
                audioRole[module.Id] = Role.Source;
            }
        }

        Dictionary<long, AnalyzedCable?> originOf = shown.Where(m => audioRole[m.Id] == Role.Source)
            .ToDictionary(m => m.Id, m => PitchOrigin(m.Id, cables, roleById));
        AudioTopology topology = new AudioTopology(shown.Select(m => m.Id).ToList(), cables, audioRole, id => originOf.GetValueOrDefault(id)?.Cable.From);

        // A module sending notes to voices from several outputs (keyboard zones…) names the output too.
        HashSet<long> splitOrigins = originOf.Values.OfType<AnalyzedCable>()
            .GroupBy(c => c.Cable.From.ModuleId)
            .Where(g => g.Select(c => c.Cable.From.PortId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();
        string OriginName(AnalyzedCable origin) => splitOrigins.Contains(origin.Cable.From.ModuleId)
            ? $"{titles[origin.Cable.From.ModuleId]} › {origin.FromPort}"
            : titles[origin.Cable.From.ModuleId];
        AnalyzedCable? OriginOfVoice(List<long> members) => members.Where(id => audioRole[id] == Role.Source)
            .Select(id => originOf.GetValueOrDefault(id)).FirstOrDefault(o => o is not null);
        // Voices played from the outputs of one module (keyboard zones A, B…) stay together, in output order.
        List<List<long>> ordered = topology.Voices
            .Select((members, position) => (Members: members, Position: position, Origin: OriginOfVoice(members)))
            .GroupBy(v => v.Origin is AnalyzedCable o && splitOrigins.Contains(o.Cable.From.ModuleId) ? o.Cable.From.ModuleId : -1 - v.Position)
            .OrderBy(g => g.Min(v => v.Position))
            .SelectMany(g => g.OrderBy(v => v.Origin?.Cable.From.PortId ?? 0).ThenBy(v => v.Position))
            .Select(v => v.Members)
            .ToList();
        List<Voice> voices = ordered.Select((members, index) =>
        {
            List<long> sources = members.Where(id => audioRole[id] == Role.Source).ToList();
            AnalyzedCable? origin = OriginOfVoice(members);
            return new Voice(index, members, string.Join(" + ", sources.Select(id => titles[id])), origin is null ? null : OriginName(origin));
        }).ToList();
        Dictionary<long, int> voiceById = voices.SelectMany(v => v.ModuleIds.Select(id => (id, v.Index))).ToDictionary(p => p.id, p => p.Index);

        List<AnalyzedModule> modules = new List<AnalyzedModule>();
        foreach (PatchModule module in shown)
        {
            long? host = topology.InsertHost.TryGetValue(module.Id, out long h) ? h : null;
            // An insert lives wherever its host lives: in the host's voice, or on the bus.
            long placedAs = host ?? module.Id;
            int? voice = voiceById.TryGetValue(placedAs, out int v) ? v : null;
            Band band = voice is not null ? Band.Voice : BandFor(placedAs, roleById[placedAs], cables);
            modules.Add(new AnalyzedModule(module, titles[module.Id], module.Plugin, roleById[module.Id], band, voice, host)
            {
                Expanders = baseOf.Where(kv => kv.Value == module.Id).Select(kv => ExpanderName(byId[kv.Key])).ToList(),
                Function = ModuleFunctions.Of(roleById[module.Id], ports.Get(module.Plugin, module.Model).Tags, module.Model, ports.Get(module.Plugin, module.Model).Name),
            });
        }

        // An insert or send/return chain returning into its host is an effect loop, not feedback.
        Dictionary<long, long> hostOf = modules.Where(m => m.InsertOf is not null).ToDictionary(m => m.Module.Id, m => m.InsertOf!.Value);
        long LoopHost(long id) => hostOf.GetValueOrDefault(id, id);
        List<List<long>> loops = GraphOrder.Loops(
            shown.Select(m => m.Id).ToList(),
            cables.Where(c => LoopHost(c.Cable.From.ModuleId) != LoopHost(c.Cable.To.ModuleId)).Select(c => (c.Cable.From.ModuleId, c.Cable.To.ModuleId)).ToList());
        return new PatchAnalysis(patch, modules, cables, voices, diagnostics) { Loops = loops };
    }

    private List<AnalyzedCable> TypeCables(PatchDocument patch, Dictionary<long, PatchModule> byId, Dictionary<long, long> baseOf, Dictionary<long, string> titles, Dictionary<long, Role> roleById, List<string> diagnostics)
    {
        List<AnalyzedCable> cables = new List<AnalyzedCable>();
        List<(PortRef Plugged, AnalyzedCable Cable)> plugged = new List<(PortRef, AnalyzedCable)>();
        foreach (PatchCable original in patch.Cables.Where(c => byId.ContainsKey(c.From.ModuleId) && byId.ContainsKey(c.To.ModuleId)))
        {
            // Ports are named on the module they're on (an expander's prefixed with its name), the cable then attaches to the base.
            PatchModule fromModule = byId[original.From.ModuleId];
            PatchModule toModule = byId[original.To.ModuleId];
            string fromPort = PortOn(fromModule, MidiLearn.Name(fromModule, original.From.PortId, ports.OutputName(fromModule.Plugin, fromModule.Model, original.From.PortId)), baseOf);
            string toPort = PortOn(toModule, MidiLearn.Name(toModule, original.To.PortId, ports.InputName(toModule.Plugin, toModule.Model, original.To.PortId)), baseOf);
            PatchCable cable = original with
            {
                From = original.From with { ModuleId = baseOf.GetValueOrDefault(original.From.ModuleId, original.From.ModuleId) },
                To = original.To with { ModuleId = baseOf.GetValueOrDefault(original.To.ModuleId, original.To.ModuleId) },
            };
            PatchModule from = byId[cable.From.ModuleId];
            PatchModule to = byId[cable.To.ModuleId];
            IReadOnlyList<ModRoute> routes = baseOf.ContainsKey(toModule.Id) ? Array.Empty<ModRoute>() : ModRoutes(to, cable.To.PortId);
            if (!baseOf.ContainsKey(toModule.Id) && ports.Get(to.Plugin, to.Model).Modulation is ModMatrix matrix && cable.To.PortId >= matrix.FirstInput && cable.To.PortId < matrix.FirstInput + matrix.InputCount)
            {
                if (routes.Count == 0)
                {
                    diagnostics.Add($"{titles[from.Id]} → {titles[to.Id]} '{toPort}': no target knob has a depth for this input, so the cable does nothing");
                }
                else
                {
                    toPort = $"Mod {cable.To.PortId - matrix.FirstInput + 1} → {string.Join(", ", routes.Select(r => r.ToString()))}";
                }
            }

            (SignalType signal, string? mismatch) = classifier.Classify(cable.Color, fromPort, toPort, roleById[from.Id], roleById[to.Id]);
            if (mismatch is not null)
            {
                diagnostics.Add($"{titles[from.Id]} '{fromPort}' → {titles[to.Id]} '{toPort}': {mismatch}");
            }

            string intent = routes.Count > 0
                ? IntentSuggester.SuggestMod(titles[from.Id], roleById[from.Id], routes)
                : IntentSuggester.Suggest(signal, titles[from.Id], fromPort.Split(" › ")[^1], titles[to.Id], toPort.Split(" › ")[^1], roleById[from.Id], roleById[to.Id]);
            AnalyzedCable analyzed = new AnalyzedCable(cable, fromPort, toPort, signal, intent) { Routes = routes };
            cables.Add(analyzed);
            plugged.Add((original.To, analyzed));
        }

        // Some modules pass on what they get, whatever their output is called: an octave shifter fed a velocity outputs a
        // velocity, a switch routing oscillators outputs audio. Only when the cable color doesn't already say so; repeat
        // for chains of them.
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int i = 0; i < cables.Count; i++)
            {
                AnalyzedCable cable = cables[i];
                long from = cable.Cable.From.ModuleId;
                if (classifier.KnowsColor(cable.Cable.Color) || PassedOn(from, cables, roleById) is not SignalType passed || passed == cable.Signal)
                {
                    continue;
                }
                cables[i] = cable with
                {
                    Signal = passed,
                    SuggestedIntent = IntentSuggester.Suggest(passed, titles[from], cable.FromPort.Split(" › ")[^1], titles[cable.Cable.To.ModuleId], cable.ToPort.Split(" › ")[^1], roleById[from], roleById[cable.Cable.To.ModuleId]),
                };
                changed = true;
            }
        }

        foreach (IGrouping<PortRef, (PortRef Plugged, AnalyzedCable Cable)> stacked in plugged.GroupBy(p => p.Plugged).Where(g => g.Count() > 1))
        {
            AnalyzedCable first = stacked.First().Cable;
            diagnostics.Add($"{titles[first.Cable.To.ModuleId]} '{first.ToPort}' has {stacked.Count()} stacked cables (summed by Rack ≥ 2.5, only the first one kept by Cardinal)");
        }
        return cables;
    }

    /// <summary>
    /// What a pass-through module sends out, from what it gets: a pitch processor fed only CV sends CV; a switch sends
    /// the one signal type of everything it routes (its clock, reset and select inputs left aside). Null otherwise.
    /// </summary>
    private static SignalType? PassedOn(long moduleId, List<AnalyzedCable> cables, Dictionary<long, Role> roleById)
    {
        List<AnalyzedCable> fed = cables.Where(c => c.Cable.To.ModuleId == moduleId).ToList();
        if (roleById[moduleId] == Role.Pitch)
        {
            return fed.Count > 0 && fed.All(c => c.Signal == SignalType.Cv) ? SignalType.Cv : null;
        }
        if (roleById[moduleId] == Role.Router)
        {
            List<SignalType> routed = fed.Where(c => !SignalClassifier.IsControlPort(c.ToPort)).Select(c => c.Signal).Distinct().ToList();
            return routed.Count == 1 ? routed[0] : null;
        }
        return null;
    }

    /// <summary>
    /// Roles that depend on the patch:
    ///  - a performance module that sends nothing out only shows what it receives (a piano keyboard lighting the notes
    ///    played): it's a display;
    ///  - an I/O or performance module with inputs and outputs (keyboard zones, MIDI pads, on-screen pads, joystick…) relays
    ///    the outside world only when everything it receives comes from the outside (Host MIDI, the musician or another
    ///    relay); otherwise another module plays it and it's a pitch/time/control utility;
    ///  - a "visual" module that also sends signals out (a piano display with outputs) is a controller, not a monitor;
    ///  - a switch takes the role of what it routes: audio makes it part of the sound path (a modifier), pitch a pitch
    ///    processor, gates a time utility, CV a controller.
    /// </summary>
    private static void ResolveContextualRoles(List<PatchModule> shown, List<AnalyzedCable> cables, Dictionary<long, Role> roleById)
    {
        bool HasOutputs(long id) => cables.Any(c => c.Cable.From.ModuleId == id);
        List<AnalyzedCable> Inputs(long id) => cables.Where(c => c.Cable.To.ModuleId == id).ToList();

        foreach (PatchModule module in shown.Where(m => roleById[m.Id] == Role.Router))
        {
            SignalType? routed = cables.Where(c => c.Cable.To.ModuleId == module.Id && !SignalClassifier.IsControlPort(c.ToPort))
                .GroupBy(c => c.Signal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (SignalType?)g.Key).FirstOrDefault();
            roleById[module.Id] = routed switch
            {
                SignalType.Audio => Role.Modifier,
                SignalType.Pitch => Role.Pitch,
                SignalType.Gate => Role.Time,
                _ => Role.Controller,
            };
        }
        foreach (PatchModule module in shown.Where(m => roleById[m.Id] == Role.Monitor && HasOutputs(m.Id)))
        {
            roleById[module.Id] = Role.Controller;
        }
        foreach (PatchModule module in shown.Where(m => roleById[m.Id] == Role.Performance && !HasOutputs(m.Id)))
        {
            roleById[module.Id] = Role.Monitor;
        }

        bool Outer(long id) => roleById[id] is Role.Io or Role.Performance && HasOutputs(id);
        List<long> relays = shown.Where(m => Outer(m.Id) && Inputs(m.Id).Count > 0).Select(m => m.Id).ToList();
        HashSet<long> outside = shown.Where(m => Outer(m.Id) && Inputs(m.Id).Count == 0).Select(m => m.Id).ToHashSet();
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (long relay in relays.Where(r => !outside.Contains(r)))
            {
                if (Inputs(relay).All(c => outside.Contains(c.Cable.From.ModuleId)))
                {
                    outside.Add(relay);
                    changed = true;
                }
            }
        }

        foreach (long relay in relays.Where(r => !outside.Contains(r)))
        {
            SignalType dominant = cables.Where(c => c.Cable.From.ModuleId == relay)
                .GroupBy(c => c.Signal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
            roleById[relay] = dominant switch
            {
                SignalType.Pitch => Role.Pitch,
                SignalType.Gate => Role.Time,
                _ => Role.Controller,
            };
        }
    }

    /// <summary>Band of a module that is not part of a voice.</summary>
    private static Band BandFor(long moduleId, Role role, List<AnalyzedCable> cables) => role switch
    {
        Role.Time => Band.Time,
        Role.Pitch => Band.Pitch,
        // A VCA or a filter that only handles CV (scaling an envelope by the velocity…) is modulation.
        Role.Effect or Role.Modifier when !cables.Any(c => c.Signal == SignalType.Audio && (c.Cable.From.ModuleId == moduleId || c.Cable.To.ModuleId == moduleId)) => Band.Modulation,
        // Audio processing outside any voice is fed by a mixer (or by nothing): it's bus processing.
        Role.Mixer or Role.Effect or Role.Modifier => Band.Bus,
        // Inputs from the outside world get their own band on top; outputs to the outside close the bus.
        Role.Io => cables.Any(c => c.Cable.From.ModuleId == moduleId) ? Band.External : Band.Bus,
        Role.Performance => Band.External,
        Role.Monitor => Band.Monitor,
        _ => Band.Modulation,
    };

    /// <summary>Knobs a programmable modulation input actually reaches, with their depth (Surge XT style matrices).</summary>
    private IReadOnlyList<ModRoute> ModRoutes(PatchModule module, int inputId)
    {
        if (ports.Get(module.Plugin, module.Model).Modulation is not ModMatrix matrix)
        {
            return Array.Empty<ModRoute>();
        }
        int modulator = inputId - matrix.FirstInput;
        if (modulator < 0 || modulator >= matrix.InputCount)
        {
            return Array.Empty<ModRoute>();
        }

        List<ModRoute> routes = new List<ModRoute>();
        for (int target = 0; target < matrix.Targets.Count; target++)
        {
            double depth = module.Params.GetValueOrDefault(matrix.FirstDepthParam + (target * matrix.InputCount) + modulator);
            if (Math.Abs(depth) >= 0.005)
            {
                routes.Add(new ModRoute(matrix.Targets[target], depth));
            }
        }
        return routes;
    }

    /// <summary>
    /// Walks back the pitch cable into a source, through pitch processors (quantizers…), to where the notes come from:
    /// the cable leaving that module, so its output port is known too.
    /// </summary>
    private static AnalyzedCable? PitchOrigin(long sourceId, List<AnalyzedCable> cables, Dictionary<long, Role> roleById)
    {
        long current = sourceId;
        HashSet<long> seen = new HashSet<long> { current };
        AnalyzedCable? origin = null;
        while (true)
        {
            AnalyzedCable? pitchIn = cables.FirstOrDefault(c => c.Cable.To.ModuleId == current && c.Signal == SignalType.Pitch);
            if (pitchIn is null || !seen.Add(pitchIn.Cable.From.ModuleId))
            {
                return origin;
            }
            current = pitchIn.Cable.From.ModuleId;
            origin = pitchIn;
            if (roleById[current] != Role.Pitch)
            {
                return origin;
            }
        }
    }

    /// <summary>Catalog name, or the model slug; duplicates get "#2", "#3"… in rack order.</summary>
    /// <summary>"Mix Pan" for Venom's "Mix Pan Expander": the name it adds to its base's ports.</summary>
    private string ExpanderName(PatchModule expander)
    {
        string name = ports.Get(expander.Plugin, expander.Model).Name ?? expander.Model;
        string shortName = name.Replace("Expander", "", StringComparison.OrdinalIgnoreCase).Trim();
        return shortName.Length > 0 ? shortName : name;
    }

    private string PortOn(PatchModule module, string port, Dictionary<long, long> baseOf) =>
        baseOf.ContainsKey(module.Id) ? $"{ExpanderName(module)} › {port}" : port;

    private Dictionary<long, string> Titles(IReadOnlyList<PatchModule> modules)
    {
        Dictionary<long, string> titles = new Dictionary<long, string>();
        foreach (IGrouping<string, PatchModule> group in modules.GroupBy(m => ports.Get(m.Plugin, m.Model).Name ?? m.Model))
        {
            List<PatchModule> ordered = group.OrderBy(m => m.Row).ThenBy(m => m.Column).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                titles[ordered[i].Id] = ordered.Count == 1 ? group.Key : $"{group.Key} #{i + 1}";
            }
        }
        return titles;
    }
}