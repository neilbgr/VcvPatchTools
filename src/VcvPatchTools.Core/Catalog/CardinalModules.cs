using System.Text.Json;

namespace VcvPatchTools.Core.Catalog;

public enum CardinalAvailability
{
    /// <summary>Official Cardinal (DISTRHO) loads it, possibly as a stand-in (see <see cref="CardinalSupport.LoadsAs"/>).</summary>
    Official,

    /// <summary>Only the neilbgr edition of Cardinal bundles it.</summary>
    NeilbgrEdition,

    /// <summary>No Cardinal build bundles it: Cardinal drops it when loading the patch.</summary>
    Missing,
}

/// <param name="LoadsAs">"Plugin/Model" Cardinal substitutes when loading (Core/AudioInterface → Cardinal/HostAudio8), else null.</param>
/// <param name="LoadsAsName">That module's display name ("Host Audio 8"), when known.</param>
public sealed record CardinalSupport(CardinalAvailability Availability, string? LoadsAs = null, string? LoadsAsName = null);

/// <summary>
/// Which modules Cardinal can load: the official build (DISTRHO/Cardinal upstream) and the neilbgr edition, which bundles more plugins.
/// Generated into catalog/cardinal.json by "vcvpatch catalog cardinal" (see <see cref="CardinalScanner"/>).
/// </summary>
public sealed class CardinalModules
{
    public const string NeilbgrEditionUrl = "https://github.com/neilbgr/Cardinal";

    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly HashSet<string> official;
    private readonly HashSet<string> neilbgr;
    private readonly Dictionary<string, string> moduleFallbacks;
    private readonly Dictionary<string, string> pluginFallbacks;
    private readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);

    public CardinalModules(IEnumerable<string> official, IEnumerable<string> neilbgr, IEnumerable<KeyValuePair<string, string>> moduleFallbacks, IEnumerable<KeyValuePair<string, string>> pluginFallbacks)
    {
        this.official = new HashSet<string>(official, StringComparer.Ordinal);
        this.neilbgr = new HashSet<string>(neilbgr, StringComparer.Ordinal);
        this.moduleFallbacks = new Dictionary<string, string>(moduleFallbacks, StringComparer.Ordinal);
        this.pluginFallbacks = new Dictionary<string, string>(pluginFallbacks, StringComparer.Ordinal);
    }

    /// <summary>True when the embedded list was generated: without it, every module would look missing.</summary>
    public bool IsKnown => official.Count > 0;

    public static CardinalModules Empty { get; } = new CardinalModules(Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>());

    private static readonly Lazy<CardinalModules> embedded = new Lazy<CardinalModules>(() =>
    {
        string? json = PortCatalog.ReadResource("catalog.cardinal.json");
        return json is null ? Empty : FromJson(json);
    });

    public static CardinalModules Embedded => embedded.Value;

    /// <summary>Official modules are the upstream scan; the neilbgr edition lists only what upstream lacks.</summary>
    public static CardinalModules FromScans(CardinalScan upstream, CardinalScan fork)
    {
        CardinalModules modules = new CardinalModules(upstream.Modules, fork.Modules.Where(m => !upstream.Modules.Contains(m)), upstream.ModuleFallbacks, upstream.PluginFallbacks);
        // Only the stand-ins need a name: they are the ones the Info tab spells out.
        foreach (string target in upstream.ModuleFallbacks.Values.Where(upstream.Names.ContainsKey))
        {
            modules.names[target] = upstream.Names[target];
        }
        return modules;
    }

    public static CardinalModules FromJson(string json)
    {
        Stored stored = JsonSerializer.Deserialize<Stored>(json, jsonOptions) ?? new Stored();
        CardinalModules modules = new CardinalModules(stored.Official ?? new List<string>(), stored.Neilbgr ?? new List<string>(),
            stored.ModuleFallbacks ?? new Dictionary<string, string>(), stored.PluginFallbacks ?? new Dictionary<string, string>());
        foreach ((string key, string name) in stored.Names ?? new Dictionary<string, string>())
        {
            modules.names[key] = name;
        }
        return modules;
    }

    public string ToJson() => JsonSerializer.Serialize(new Stored
    {
        Official = official.Order(StringComparer.Ordinal).ToList(),
        Neilbgr = neilbgr.Order(StringComparer.Ordinal).ToList(),
        ModuleFallbacks = new SortedDictionary<string, string>(moduleFallbacks, StringComparer.Ordinal),
        PluginFallbacks = new SortedDictionary<string, string>(pluginFallbacks, StringComparer.Ordinal),
        Names = new SortedDictionary<string, string>(names, StringComparer.Ordinal),
    }, jsonOptions);

    public int OfficialCount => official.Count;

    public int NeilbgrCount => neilbgr.Count;

    /// <summary>What Cardinal does with this module when it loads a patch, following its own slug fallbacks.</summary>
    public CardinalSupport Check(string plugin, string model)
    {
        string key = $"{plugin}/{model}";
        if (official.Contains(key))
        {
            return new CardinalSupport(CardinalAvailability.Official);
        }
        if (neilbgr.Contains(key))
        {
            return new CardinalSupport(CardinalAvailability.NeilbgrEdition);
        }

        string? stand = moduleFallbacks.GetValueOrDefault(key)
            ?? (pluginFallbacks.TryGetValue(plugin, out string? otherPlugin) ? $"{otherPlugin}/{model}" : null);
        if (stand is not null && official.Contains(stand))
        {
            return new CardinalSupport(CardinalAvailability.Official, stand, names.GetValueOrDefault(stand));
        }
        if (stand is not null && neilbgr.Contains(stand))
        {
            return new CardinalSupport(CardinalAvailability.NeilbgrEdition, stand, names.GetValueOrDefault(stand));
        }
        return new CardinalSupport(CardinalAvailability.Missing);
    }

    /// <summary>Short label for tables: "Yes", "Yes, as Cardinal Audio 8", "neilbgr edition", "No".</summary>
    public static string Label(CardinalSupport support) => support.Availability switch
    {
        CardinalAvailability.Official => support.LoadsAs is null ? "Yes" : $"Yes, as {StandIn(support)}",
        CardinalAvailability.NeilbgrEdition => "neilbgr edition",
        _ => "No",
    };

    /// <summary>"Cardinal Audio 8": the plugin too, as the stand-in's own name can be the replaced module's ("Audio 8").</summary>
    private static string StandIn(CardinalSupport support) =>
        support.LoadsAsName is string name ? $"{support.LoadsAs![..support.LoadsAs!.IndexOf('/')]} {name}" : support.LoadsAs!;

    private sealed class Stored
    {
        public List<string>? Official { get; set; }
        public List<string>? Neilbgr { get; set; }
        public IDictionary<string, string>? ModuleFallbacks { get; set; }
        public IDictionary<string, string>? PluginFallbacks { get; set; }
        public IDictionary<string, string>? Names { get; set; }
    }
}