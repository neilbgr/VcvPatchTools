using System.Text.Json;
using System.Text.RegularExpressions;

namespace VcvPatchTools.Core.Catalog;

/// <summary>What one Cardinal source tree loads: "Plugin/Model" keys and the slug fallbacks it applies to patches.</summary>
public sealed record CardinalScan(
    IReadOnlySet<string> Modules,
    IReadOnlyDictionary<string, string> ModuleFallbacks,
    IReadOnlyDictionary<string, string> PluginFallbacks,
    IReadOnlyList<string> Problems)
{
    /// <summary>Display names from the manifests ("Cardinal/HostMIDI" → "Host MIDI").</summary>
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Reads which modules a Cardinal build actually loads, the way Cardinal itself does it:
///   plugins/plugins.cpp, initStaticPlugins() → the initStatic__X() calls that aren't commented out
///   initStatic__X()                        → StaticPluginLoader spl(p, "Dir") loads plugins/Dir/plugin.json,
///                                           spl.removeModule("slug") drops a module from that manifest
///   src/override/plugin.cpp               → moduleSlugFallbacks / pluginSlugFallbacks applied when a patch loads
/// Assumes the full desktop build: removeModule calls in an #else branch (GLES, headless, static builds) are ignored.
/// </summary>
public static partial class CardinalScanner
{
    [GeneratedRegex(@"void\s+initStaticPlugins\s*\(\s*\)\s*\{")]
    private static partial Regex InitStaticPluginsRegex();

    [GeneratedRegex(@"\binitStatic__(\w+)\s*\(\s*\)\s*;")]
    private static partial Regex InitCallRegex();

    [GeneratedRegex(@"StaticPluginLoader\s+\w+\s*\(\s*\w+\s*,\s*""([^""]+)""")]
    private static partial Regex LoaderRegex();

    [GeneratedRegex(@"removeModule\s*\(\s*""([^""]+)""")]
    private static partial Regex RemoveModuleRegex();

    [GeneratedRegex(@"\{\s*\{\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\}\s*,\s*\{\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\}\s*\}")]
    private static partial Regex ModuleFallbackRegex();

    [GeneratedRegex(@"\{\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\}")]
    private static partial Regex PluginFallbackRegex();

    /// <param name="pluginsCpp">plugins/plugins.cpp.</param>
    /// <param name="overridePluginCpp">src/override/plugin.cpp (slug fallbacks), or null.</param>
    /// <param name="readManifest">Text of plugins/&lt;dir&gt;/plugin.json, or null when it doesn't exist.</param>
    public static CardinalScan Scan(string pluginsCpp, string? overridePluginCpp, Func<string, string?> readManifest)
    {
        List<string> problems = new List<string>();
        HashSet<string> modules = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.Ordinal);
        string code = SourceScanner.StripComments(pluginsCpp);

        Match init = InitStaticPluginsRegex().Match(code);
        if (!init.Success)
        {
            problems.Add("plugins.cpp: no initStaticPlugins() found");
            return new CardinalScan(modules, new Dictionary<string, string>(), new Dictionary<string, string>(), problems);
        }

        foreach (Match call in InitCallRegex().Matches(Body(code, init.Index + init.Length - 1)))
        {
            string name = call.Groups[1].Value;
            Match definition = Regex.Match(code, $@"static\s+void\s+initStatic__{Regex.Escape(name)}\s*\(\s*\)\s*\{{");
            if (!definition.Success)
            {
                problems.Add($"plugins.cpp: initStatic__{name}() is called but not defined");
                continue;
            }

            string body = ActiveCode(Body(code, definition.Index + definition.Length - 1));
            Match loader = LoaderRegex().Match(body);
            if (!loader.Success)
            {
                problems.Add($"initStatic__{name}: no StaticPluginLoader");
                continue;
            }

            string dir = loader.Groups[1].Value;
            string? manifest = readManifest(dir);
            if (manifest is null)
            {
                problems.Add($"{dir}: plugin.json not found");
                continue;
            }

            HashSet<string> removed = RemoveModuleRegex().Matches(body).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            try
            {
                using JsonDocument json = JsonDocument.Parse(manifest, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                string plugin = json.RootElement.GetProperty("slug").GetString() ?? dir;
                if (!json.RootElement.TryGetProperty("modules", out JsonElement list))
                {
                    continue;
                }
                foreach (JsonElement module in list.EnumerateArray())
                {
                    string? slug = module.TryGetProperty("slug", out JsonElement s) ? s.GetString() : null;
                    if (slug is not null && !removed.Contains(slug))
                    {
                        modules.Add($"{plugin}/{slug}");
                        if (module.TryGetProperty("name", out JsonElement n) && n.GetString() is string displayName)
                        {
                            names[$"{plugin}/{slug}"] = displayName;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                problems.Add($"{dir}: unreadable plugin.json ({ex.Message})");
            }
        }

        Dictionary<string, string> moduleFallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> pluginFallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overridePluginCpp is not null)
        {
            string overrides = SourceScanner.StripComments(overridePluginCpp);
            foreach (Match m in ModuleFallbackRegex().Matches(MapInitializer(overrides, "moduleSlugFallbacks")))
            {
                moduleFallbacks[$"{m.Groups[1].Value}/{m.Groups[2].Value}"] = $"{m.Groups[3].Value}/{m.Groups[4].Value}";
            }
            foreach (Match m in PluginFallbackRegex().Matches(MapInitializer(overrides, "pluginSlugFallbacks")))
            {
                pluginFallbacks[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        return new CardinalScan(modules, moduleFallbacks, pluginFallbacks, problems) { Names = names };
    }

    /// <summary>Text between the brace at <paramref name="openBrace"/> and its matching close.</summary>
    private static string Body(string code, int openBrace)
    {
        int depth = 0;
        for (int i = openBrace; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}' && --depth == 0)
            {
                return code[(openBrace + 1)..i];
            }
        }
        return code[(openBrace + 1)..];
    }

    private static string MapInitializer(string code, string name)
    {
        Match m = Regex.Match(code, $@"\b{name}\s*=\s*\{{");
        return m.Success ? Body(code, m.Index + m.Length - 1) : "";
    }

    /// <summary>Drops the lines a full desktop build doesn't compile: #else branches, and #ifdef EXPERIMENTAL / TEST / DEBUG blocks.</summary>
    private static string ActiveCode(string body)
    {
        // One entry per open #if: is this branch compiled?
        Stack<bool> branches = new Stack<bool>();
        List<string> kept = new List<string>();
        foreach (string line in body.Split('\n'))
        {
            string directive = line.Trim();
            if (directive.StartsWith('#'))
            {
                string word = directive.TrimStart('#').TrimStart();
                if (word.StartsWith("if", StringComparison.Ordinal))
                {
                    bool devOnly = Regex.IsMatch(word, @"^ifdef\s+(EXPERIMENTAL|TEST|DEBUG)\b");
                    branches.Push(!devOnly);
                }
                else if (word.StartsWith("else", StringComparison.Ordinal) && branches.Count > 0)
                {
                    branches.Pop();
                    branches.Push(false);
                }
                else if (word.StartsWith("endif", StringComparison.Ordinal) && branches.Count > 0)
                {
                    branches.Pop();
                }
                continue;
            }
            if (branches.All(active => active))
            {
                kept.Add(line);
            }
        }
        return string.Join('\n', kept);
    }
}