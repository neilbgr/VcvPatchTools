using VcvPatchTools.Core.Catalog;

namespace VcvPatchTools.Cli;

/// <summary>
/// "catalog build": scans every plugin under a Cardinal "plugins" folder and writes catalog/ports.json.
/// Rack's own Core plugin (MIDI, audio interfaces) lives next to it, in src/Rack: its manifest is Core.json.
/// </summary>
internal static class CatalogCommand
{
    public static int Build(string pluginsDir, string outputPath, string? reportPath)
    {
        if (!Directory.Exists(pluginsDir))
        {
            Console.Error.WriteLine($"Plugins folder not found: {pluginsDir}");
            return 1;
        }

        Dictionary<string, ModuleInfo> all = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
        List<string> problems = new List<string>();

        string rackDir = Path.Combine(pluginsDir, "..", "src", "Rack");
        IEnumerable<(string Manifest, string Sources)> plugins = Directory.GetDirectories(pluginsDir)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => (Path.Combine(d, "plugin.json"), Path.Combine(d, "src")))
            .Prepend((Path.Combine(rackDir, "Core.json"), Path.Combine(rackDir, "src", "core")));
        foreach ((string manifestPath, string sourceDir) in plugins)
        {
            string pluginDir = Path.GetDirectoryName(manifestPath)!;
            if (!File.Exists(manifestPath) || !Directory.Exists(sourceDir))
            {
                continue;
            }

            List<SourceFile> sources = Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".cpp", StringComparison.Ordinal) || p.EndsWith(".hpp", StringComparison.Ordinal) || p.EndsWith(".h", StringComparison.Ordinal))
                .Select(p => new SourceFile(p, File.ReadAllText(p)))
                .ToList();

            try
            {
                ScanResult scan = SourceScanner.ScanPlugin(File.ReadAllText(manifestPath), sources, IncludedHeaders(pluginDir, sourceDir, sources));
                foreach ((string key, ModuleInfo info) in scan.Modules)
                {
                    all[key] = info;
                }
                problems.AddRange(scan.Problems);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException)
            {
                problems.Add($"{Path.GetFileName(pluginDir)}: unreadable plugin.json ({ex.Message})");
            }
        }

        // A plugin no longer in the source tree (removed from the Cardinal fork…) keeps its last scanned ports: patches
        // made in Rack still use it.
        if (File.Exists(outputPath))
        {
            HashSet<string> scanned = all.Keys.Select(PluginOf).ToHashSet(StringComparer.Ordinal);
            List<IGrouping<string, KeyValuePair<string, ModuleInfo>>> kept = PortCatalog.FromJson(File.ReadAllText(outputPath)).Modules
                .Where(kv => !scanned.Contains(PluginOf(kv.Key))).GroupBy(kv => PluginOf(kv.Key)).ToList();
            foreach (IGrouping<string, KeyValuePair<string, ModuleInfo>> plugin in kept)
            {
                foreach ((string key, ModuleInfo info) in plugin)
                {
                    all[key] = info;
                }
                problems.Add($"{plugin.Key}: no sources found, {plugin.Count()} modules kept from the previous catalog");
            }
        }

        File.WriteAllText(outputPath, new PortCatalog(all).ToJson());
        int resolved = all.Values.Count(m => m.Inputs.Count > 0 || m.Outputs.Count > 0);
        Console.WriteLine($"{all.Count} modules, {resolved} with ports resolved, {problems.Count} problems → {outputPath}");

        if (reportPath is not null)
        {
            File.WriteAllLines(reportPath, problems);
            Console.WriteLine($"Problems report → {reportPath}");
        }
        return 0;
    }

    /// <summary>Headers outside the source folder (a bundled engine, a library) that the sources include by name.</summary>
    private static List<SourceFile> IncludedHeaders(string pluginDir, string sourceDir, List<SourceFile> sources)
    {
        HashSet<string> included = sources
            .SelectMany(s => System.Text.RegularExpressions.Regex.Matches(s.Text, @"#\s*include\s*[<""]([^>""]+)[>""]"))
            .Select(m => Path.GetFileName(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        string ownSources = Path.GetFullPath(sourceDir) + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(pluginDir, "*.h*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".h", StringComparison.Ordinal) || p.EndsWith(".hpp", StringComparison.Ordinal))
                && !Path.GetFullPath(p).StartsWith(ownSources, StringComparison.Ordinal) && included.Contains(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new SourceFile(p, File.ReadAllText(p)))
            .ToList();
    }

    private static string PluginOf(string key) => key[..key.IndexOf('/')];

    /// <summary>
    /// "catalog cardinal": which modules official Cardinal and the neilbgr edition load, from two Cardinal source trees
    /// (plugins/plugins.cpp, plugins/*/plugin.json, src/override/plugin.cpp). Upstream is exported by scripts/export-cardinal-upstream.sh.
    /// </summary>
    public static int Cardinal(string upstreamRoot, string forkRoot, string outputPath)
    {
        CardinalScan? upstream = ScanCardinal(upstreamRoot);
        CardinalScan? fork = ScanCardinal(forkRoot);
        if (upstream is null || fork is null)
        {
            return 1;
        }

        CardinalModules modules = CardinalModules.FromScans(upstream, fork);
        File.WriteAllText(outputPath, modules.ToJson());
        Console.WriteLine($"{modules.OfficialCount} official modules, {modules.NeilbgrCount} more in the neilbgr edition, {upstream.ModuleFallbacks.Count + upstream.PluginFallbacks.Count} fallbacks → {outputPath}");
        foreach (string problem in upstream.Problems.Select(p => $"upstream: {p}").Concat(fork.Problems.Select(p => $"fork: {p}")))
        {
            Console.Error.WriteLine(problem);
        }
        return 0;
    }

    private static CardinalScan? ScanCardinal(string root)
    {
        string pluginsCpp = Path.Combine(root, "plugins", "plugins.cpp");
        if (!File.Exists(pluginsCpp))
        {
            Console.Error.WriteLine($"Not a Cardinal source tree (no plugins/plugins.cpp): {root}");
            return null;
        }
        string overridePath = Path.Combine(root, "src", "override", "plugin.cpp");
        return CardinalScanner.Scan(
            File.ReadAllText(pluginsCpp),
            File.Exists(overridePath) ? File.ReadAllText(overridePath) : null,
            dir =>
            {
                string manifest = Path.Combine(root, "plugins", dir, "plugin.json");
                return File.Exists(manifest) ? File.ReadAllText(manifest) : null;
            });
    }
}