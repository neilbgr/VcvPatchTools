using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VcvPatchTools.Core.Catalog;

/// <summary>A plugin source file, already read from disk (keeps the scanner free of file I/O).</summary>
public sealed record SourceFile(string Path, string Text);

public sealed record ScanResult(IReadOnlyDictionary<string, ModuleInfo> Modules, IReadOnlyList<string> Problems);

/// <summary>
/// Best-effort extraction of port names from a Rack plugin's C++ sources:
///   createModel&lt;Module, Widget&gt;("slug")  → module struct
///   enum InputIds / OutputIds { ... }      → port indices (ENUMS(X, n) and "= expr" handled)
///   configInput/configOutput(ID, "Name")   → port names
/// Ports without a literal config call get a name humanized from their enum identifier.
/// Anything it can't resolve is reported; catalog/ports.overrides.json fills the gaps by hand.
/// </summary>
public static partial class SourceScanner
{
    private static readonly string[] inputEnumNames = { "InputIds", "InputsIds", "InputId", "Inputs" };
    private static readonly string[] paramEnumNames = { "ParamIds", "ParamsIds", "ParamId", "Params" };
    private static readonly string[] outputEnumNames = { "OutputIds", "OutputsIds", "OutputId", "Outputs" };

    // createModel<Type<Args>, …> or createModel<Widget<Args>::M, …> (Surge XT: a typedef inside a templated widget).
    [GeneratedRegex(@"createModel\s*<\s*([\w:]+)\s*(?:<([^<>]*)>)?\s*(?:::\s*(\w+)\s*)?,\s*[\w:]+\s*(?:<[^<>]*>)?\s*>\s*\(\s*""([^""]+)""")]
    private static partial Regex CreateModelRegex();

    [GeneratedRegex(@"\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{")]
    private static partial Regex EnumRegex();

    [GeneratedRegex(@"config(Input|Output)\s*\(\s*(\w+)\s*(?:\+\s*(\d+)\s*)?,\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ConfigCallRegex();

    [GeneratedRegex(@"ModulationAssistant\s*<\s*[\w:]+(?:\s*<[^<>]*>)?\s*,\s*([^,<>]+?)\s*,\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*>")]
    private static partial Regex ModulationAssistantRegex();

    [GeneratedRegex(@"modulatorIndexFor\s*\([^)]*\)\s*\{[^}]*return\s+(\w+)\s*\+\s*offset\s*\*\s*\w+\s*\+\s*modulator")]
    private static partial Regex ModulatorIndexRegex();

    [GeneratedRegex(@"static\s+constexpr\s+int\s+(\w+)\s*(?:\{\s*(\d+)\s*\}|=\s*(\d+))")]
    private static partial Regex ConstantRegex();

    [GeneratedRegex(@"configParam\w*(?:\s*<[^;]*?>)?\s*\(\s*(\w+)\s*,[^;""]*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ConfigParamRegex();

    [GeneratedRegex(@"\b(?:static\s+)?(?:const|constexpr)\s+(?:static\s+)?(?:unsigned\s+)?(?:int|size_t|uint\w*|int\w*)\s+(\w+)\s*(?:=\s*([\w:]+)\s*;|\{\s*([\w:]+)\s*\})")]
    private static partial Regex IntConstantRegex();

    [GeneratedRegex(@"^\s*#\s*define\s+(\w+)\s+(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DefineRegex();

    [GeneratedRegex(@"config(Input|Output)\s*\(\s*(\w+)\s*\+\s*(\w+)\s*,\s*((?:[^;()]|\((?:[^;()]|\([^;()]*\))*\))+)\)\s*;")]
    private static partial Regex LoopConfigRegex();

    [GeneratedRegex(@"\b(\w+)\s*\[[^\]]*\]\s*=\s*\{([^}]*)\}")]
    private static partial Regex StringArrayRegex();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"^ENUMS\s*\(\s*(\w+)\s*,\s*(.+)\)$", RegexOptions.Singleline)]
    private static partial Regex EnumsMacroRegex();

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+(\w+)\(([^)]*)\)((?:[^\n]*\\\r?\n)*[^\n]*)", RegexOptions.Multiline)]
    private static partial Regex FunctionMacroRegex();

    [GeneratedRegex(@"std::string\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)\s*\+\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringConcatRegex();

    /// <param name="includedHeaders">
    /// Headers the sources include from elsewhere in the plugin (a bundled engine: Surge XT's n_osc_params is in
    /// SurgeStorage.h): only their constants are read, the sources' own ones win.
    /// </param>
    public static ScanResult ScanPlugin(string pluginJson, IReadOnlyList<SourceFile> sources, IReadOnlyList<SourceFile>? includedHeaders = null)
    {
        List<string> problems = new List<string>();
        Dictionary<string, ModuleInfo> result = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);

        using JsonDocument manifest = JsonDocument.Parse(pluginJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string pluginSlug = manifest.RootElement.GetProperty("slug").GetString() ?? "";

        List<SourceFile> cleaned = sources.Select(s => new SourceFile(s.Path, StripComments(s.Text))).ToList();
        Dictionary<string, int> constants = CollectConstants(cleaned.Concat((includedHeaders ?? Array.Empty<SourceFile>()).Select(s => new SourceFile(s.Path, StripComments(s.Text)))).ToList());

        Dictionary<string, (string Type, string? TemplateArgs)> structBySlug = new Dictionary<string, (string Type, string? TemplateArgs)>(StringComparer.Ordinal);
        foreach (SourceFile file in cleaned)
        {
            foreach (Match match in CreateModelRegex().Matches(file.Text + ExpandModelMacros(file.Text)))
            {
                // Widget<Args>::M: the arguments are the widget's, the module is found through the alias.
                (string Type, string? TemplateArgs) registration = match.Groups[3].Success
                    ? ($"{match.Groups[1].Value}::{match.Groups[3].Value}", null)
                    : (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null);
                structBySlug.TryAdd(match.Groups[4].Value, registration);
            }
        }

        if (!manifest.RootElement.TryGetProperty("modules", out JsonElement modulesElement))
        {
            return new ScanResult(result, problems);
        }

        foreach (JsonElement moduleElement in modulesElement.EnumerateArray())
        {
            string slug = moduleElement.GetProperty("slug").GetString() ?? "";
            string? name = moduleElement.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            List<string> tags = moduleElement.TryGetProperty("tags", out JsonElement t)
                ? t.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();

            List<string> inputs = new List<string>();
            List<string> outputs = new List<string>();
            ModMatrix? modulation = null;
            if (!structBySlug.TryGetValue(slug, out (string Type, string? TemplateArgs) registration))
            {
                problems.Add($"{pluginSlug}/{slug}: no createModel<...>(\"{slug}\") registration found");
            }
            else if (!TryResolvePorts(registration.Type, cleaned, WithTemplateArgs(registration, cleaned, constants), inputs, outputs, out modulation))
            {
                problems.Add($"{pluginSlug}/{slug}: struct '{registration.Type}' has no resolvable InputIds/OutputIds enum");
            }

            result[$"{pluginSlug}/{slug}"] = new ModuleInfo(name, tags, inputs, outputs) { Modulation = modulation };
        }

        return new ScanResult(result, problems);
    }

    private static bool TryResolvePorts(string typeName, List<SourceFile> sources, Dictionary<string, int> constants, List<string> inputs, List<string> outputs, out ModMatrix? modulation)
    {
        modulation = null;
        StructSource? found = FindStruct(LastSegment(typeName), sources, depth: 0)
            ?? FindAliasedStruct(typeName, sources);
        if (found is null)
        {
            return false;
        }

        List<(string Id, int Index, string Fallback)>? inputEnum = ParseNamedEnum(found.EnumScopes, inputEnumNames, constants);
        List<(string Id, int Index, string Fallback)>? outputEnum = ParseNamedEnum(found.EnumScopes, outputEnumNames, constants);
        if (inputEnum is null && outputEnum is null)
        {
            return false;
        }

        Dictionary<string, string> inputNames = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> outputNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string scope in found.ConfigScopes)
        {
            foreach (Match match in ConfigCallRegex().Matches(scope))
            {
                Dictionary<string, string> target = match.Groups[1].Value == "Input" ? inputNames : outputNames;
                string key = match.Groups[3].Success && match.Groups[3].Value != "0" ? $"{match.Groups[2].Value}+{match.Groups[3].Value}" : match.Groups[2].Value;
                target.TryAdd(key, Regex.Unescape(match.Groups[4].Value));
            }
        }

        foreach (string scope in found.ConfigScopes)
        {
            AddLoopNames(scope, "Input", inputEnum, inputNames);
            AddLoopNames(scope, "Output", outputEnum, outputNames);
        }

        Fill(inputs, inputEnum, inputNames);
        Fill(outputs, outputEnum, outputNames);
        modulation = FindModMatrix(found, inputEnum, constants);
        if (modulation is not null)
        {
            // The enum only names the first modulation input ("X_MOD_INPUT, NUM_INPUTS = X_MOD_INPUT + n").
            for (int i = 0; i < modulation.InputCount; i++)
            {
                int index = modulation.FirstInput + i;
                while (inputs.Count <= index)
                {
                    inputs.Add("");
                }
                inputs[index] = $"Modulation Signal {i + 1}";
            }
        }
        return true;
    }

    /// <summary>
    /// createModel&lt;MixMaster&lt;8, 2&gt;, ...&gt; with "template&lt;int N_TRK, int N_GRP&gt; struct MixMaster": binds
    /// N_TRK = 8, N_GRP = 2 on top of the plugin constants, so ENUMS(X, N_TRK * 2) can be sized.
    /// </summary>
    private static Dictionary<string, int> WithTemplateArgs((string Type, string? TemplateArgs) registration, List<SourceFile> sources, Dictionary<string, int> constants)
    {
        if (registration.TemplateArgs is null)
        {
            return constants;
        }

        Regex declaration = new Regex(@"template\s*<([^<>]*)>\s*(?:struct|class)\s+" + Regex.Escape(LastSegment(registration.Type)) + @"\b");
        foreach (SourceFile file in sources)
        {
            Match match = declaration.Match(file.Text);
            if (!match.Success)
            {
                continue;
            }

            Dictionary<string, int> bound = new Dictionary<string, int>(constants, StringComparer.Ordinal);
            string[] names = SplitTopLevel(match.Groups[1].Value).Select(p => p.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "").ToArray();
            string[] values = SplitTopLevel(registration.TemplateArgs).Select(a => a.Trim()).ToArray();
            for (int i = 0; i < Math.Min(names.Length, values.Length); i++)
            {
                if (Evaluate(values[i], constants) is int value)
                {
                    bound[names[i]] = value;
                }
            }
            return bound;
        }
        return constants;
    }

    /// <summary>
    /// Surge XT registers its effects through a macro, "#define FXMODEL(type, nm) … createModel&lt;…&gt;(std::string("SurgeXTFX") + #nm)"
    /// then "FXMODEL(fxt_reverb, Reverb);": expands the invocations of such macros, with their slugs folded into one literal.
    /// </summary>
    private static string ExpandModelMacros(string text)
    {
        StringBuilder expanded = new StringBuilder();
        foreach (Match macro in FunctionMacroRegex().Matches(text))
        {
            string body = Regex.Replace(macro.Groups[3].Value, @"\\\r?\n", " ");
            if (!body.Contains("createModel", StringComparison.Ordinal))
            {
                continue;
            }
            string[] parameters = SplitTopLevel(macro.Groups[2].Value).Select(p => p.Trim()).ToArray();
            Regex invocation = new Regex(@"(?<![#\w])" + Regex.Escape(macro.Groups[1].Value) + @"\s*\(([^()]*)\)\s*;");
            foreach (Match call in invocation.Matches(text))
            {
                string[] arguments = SplitTopLevel(call.Groups[1].Value).Select(a => a.Trim()).ToArray();
                if (arguments.Length != parameters.Length)
                {
                    continue;
                }
                string result = body;
                for (int i = 0; i < parameters.Length; i++)
                {
                    string name = Regex.Escape(parameters[i]);
                    result = Regex.Replace(result, @"(?<!#)#\s*" + name + @"\b", $"\"{arguments[i]}\"");
                    result = Regex.Replace(result, @"\b" + name + @"\b", arguments[i]);
                }
                result = Regex.Replace(result, @"\s*##\s*", "");
                for (string folded = StringConcatRegex().Replace(result, "\"$1$2\""); folded != result; folded = StringConcatRegex().Replace(result, "\"$1$2\""))
                {
                    result = folded;
                }
                expanded.Append('\n').Append(result).Append(';');
            }
        }
        return expanded.ToString();
    }

    private static string LastSegment(string typeName)
    {
        int lastColons = typeName.LastIndexOf("::", StringComparison.Ordinal);
        return lastColons >= 0 ? typeName[(lastColons + 2)..] : typeName;
    }

    /// <summary>
    /// Surge XT registers createModel&lt;VCFWidget::M, ...&gt;: "M" is a typedef inside the widget struct
    /// ("typedef vcf::VCF M;"), so look the alias up in the owner's body.
    /// </summary>
    private static StructSource? FindAliasedStruct(string typeName, List<SourceFile> sources)
    {
        string[] segments = typeName.Split("::");
        if (segments.Length < 2)
        {
            return null;
        }
        string owner = segments[^2];
        string alias = segments[^1];
        Regex ownerDefinition = new Regex(@"\b(?:struct|class)\s+" + Regex.Escape(owner) + @"\b[^;{]*\{");
        Regex aliasRegex = new Regex(@"(?:typedef\s+([\w:]+)(?:\s*<[^<>]*>)?\s+" + Regex.Escape(alias) + @"\s*;|using\s+" + Regex.Escape(alias) + @"\s*=\s*([\w:]+)(?:\s*<[^<>]*>)?\s*;)");
        foreach (SourceFile file in sources)
        {
            foreach (Match match in ownerDefinition.Matches(file.Text))
            {
                Match aliased = aliasRegex.Match(BraceBody(file.Text, match.Index + match.Length - 1));
                if (aliased.Success)
                {
                    string target = aliased.Groups[1].Success ? aliased.Groups[1].Value : aliased.Groups[2].Value;
                    return FindStruct(LastSegment(target), sources, depth: 0);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Surge XT's ModulationAssistant&lt;T, COUNT, FIRST_PARAM, N_MOD_INPUTS, FIRST_MOD_INPUT&gt; plus
    /// modulatorIndexFor() "return DEPTH_0 + offset * n_mod_inputs + modulator;" describe the depth matrix.
    /// </summary>
    private static ModMatrix? FindModMatrix(StructSource found, List<(string Id, int Index, string Fallback)>? inputEnum, Dictionary<string, int> pluginConstants)
    {
        string body = found.ConfigScopes[0];
        Match assistant = ModulationAssistantRegex().Match(body);
        Match depthBase = ModulatorIndexRegex().Match(body);
        if (!assistant.Success || !depthBase.Success || inputEnum is null)
        {
            return null;
        }

        Dictionary<string, int> constants = ConstantRegex().Matches(body)
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => int.Parse(g.First().Groups[2].Success ? g.First().Groups[2].Value : g.First().Groups[3].Value));
        // The struct's own constants first, then the plugin's (and the engine headers it includes).
        foreach ((string name, int value) in pluginConstants)
        {
            constants.TryAdd(name, value);
        }

        List<(string Id, int Index, string Fallback)>? paramEnum = ParseNamedEnum(found.EnumScopes, paramEnumNames, constants);
        int? count = Evaluate(assistant.Groups[1].Value, constants);
        int? inputCount = Evaluate(assistant.Groups[3].Value, constants);
        // The C++ only needs a number: Surge XT's VCO passes PITCH_CV (input 0) for its first target, PITCH_0 (param 0).
        int? firstTarget = IndexOf(paramEnum, assistant.Groups[2].Value) ?? IndexOf(inputEnum, assistant.Groups[2].Value);
        int? firstDepth = IndexOf(paramEnum, depthBase.Groups[1].Value);
        int? firstInput = IndexOf(inputEnum, assistant.Groups[4].Value);
        if (count is null || inputCount is null || firstTarget is null || firstDepth is null || firstInput is null || paramEnum is null)
        {
            return null;
        }

        Dictionary<string, string> paramNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string scope in found.ConfigScopes)
        {
            foreach (Match match in ConfigParamRegex().Matches(scope))
            {
                paramNames.TryAdd(match.Groups[1].Value, Regex.Unescape(match.Groups[2].Value));
            }
        }

        List<string> targets = new List<string>();
        for (int i = 0; i < count; i++)
        {
            (string Id, int Index, string Fallback) entry = paramEnum.FirstOrDefault(e => e.Index == firstTarget + i);
            // "OSC_CTRL_PARAM_0, OSC_MOD_PARAM_0 = OSC_CTRL_PARAM_0 + n": a block named at run time → "Osc ctrl param 1…n".
            (string Id, int Index, string Fallback) block = paramEnum.LastOrDefault(e => e.Index <= firstTarget + i);
            string? numbered = block.Id is not null && block.Id.EndsWith("_0", StringComparison.Ordinal) && !paramNames.ContainsKey(block.Id)
                ? $"{Humanize(block.Id[..^2])} {firstTarget + i - block.Index + 1}"
                : null;
            targets.Add(numbered ?? (entry.Id is null ? $"param#{firstTarget + i}" : paramNames.GetValueOrDefault(entry.Id) ?? entry.Fallback));
        }
        return new ModMatrix(firstInput.Value, inputCount.Value, firstDepth.Value, targets);
    }

    private static int? IndexOf(List<(string Id, int Index, string Fallback)>? entries, string id)
    {
        foreach ((string entryId, int index, string _) in entries ?? new List<(string Id, int Index, string Fallback)>())
        {
            if (entryId == id)
            {
                return index;
            }
        }
        return null;
    }

    private static void Fill(List<string> ports, List<(string Id, int Index, string Fallback)>? entries, Dictionary<string, string> names)
    {
        if (entries is null || entries.Count == 0)
        {
            return;
        }

        string[] byIndex = new string[entries.Max(e => e.Index) + 1];
        foreach ((string id, int index, string fallback) in entries)
        {
            byIndex[index] = names.GetValueOrDefault(id) ?? fallback;
        }
        // Loop-named slots between two enum entries ("MOD_INPUT_0+3").
        foreach ((string id, int index, string _) in entries)
        {
            for (int k = 1; index + k < byIndex.Length && byIndex[index + k] is null && names.TryGetValue($"{id}+{k}", out string? name); k++)
            {
                byIndex[index + k] = name;
            }
        }
        ports.AddRange(byIndex.Select(s => s ?? ""));
    }

    private sealed record StructSource(List<string> EnumScopes, List<string> ConfigScopes);

    /// <summary>Finds the struct body (plus base structs and out-of-line constructors) for enum and config lookups.</summary>
    private static StructSource? FindStruct(string structName, List<SourceFile> sources, int depth)
    {
        Regex definition = new Regex(@"\b(?:struct|class)\s+" + Regex.Escape(structName) + @"\b\s*(?:final\s*)?(:[^;{]*)?\{");
        Regex constructor = new Regex(@"\b" + Regex.Escape(structName) + @"\s*::\s*" + Regex.Escape(structName) + @"\s*\([^)]*\)[^;{]*\{");

        foreach (SourceFile file in sources)
        {
            foreach (Match match in definition.Matches(file.Text))
            {
                string body = BraceBody(file.Text, match.Index + match.Length - 1);
                List<string> enumScopes = new List<string> { body };
                List<string> configScopes = new List<string> { body };

                foreach (SourceFile other in sources)
                {
                    foreach (Match ctor in constructor.Matches(other.Text))
                    {
                        configScopes.Add(BraceBody(other.Text, ctor.Index + ctor.Length - 1));
                    }
                }

                if (match.Groups[1].Success && depth < 3)
                {
                    foreach (string baseName in BaseNames(match.Groups[1].Value))
                    {
                        StructSource? baseSource = FindStruct(baseName, sources, depth + 1);
                        if (baseSource is not null)
                        {
                            enumScopes.AddRange(baseSource.EnumScopes);
                            configScopes.AddRange(baseSource.ConfigScopes);
                        }
                    }
                }

                // Old-style plugins sometimes declare the enums at file scope next to the struct.
                enumScopes.Add(file.Text);
                return new StructSource(enumScopes, configScopes);
            }
        }
        return null;
    }

    private static IEnumerable<string> BaseNames(string baseClause)
    {
        foreach (string part in SplitTopLevel(baseClause.TrimStart(':')))
        {
            string name = Regex.Replace(part, @"\b(public|private|protected|virtual)\b", "").Trim();
            int templateStart = name.IndexOf('<');
            if (templateStart >= 0)
            {
                name = name[..templateStart];
            }
            int lastColons = name.LastIndexOf("::", StringComparison.Ordinal);
            name = (lastColons >= 0 ? name[(lastColons + 2)..] : name).Trim();
            if (name.Length > 0 && name != "Module" && name != "TerminalModule")
            {
                yield return name;
            }
        }
    }

    /// <summary>Looks for the first enum with one of the given names, scanning scopes in priority order.</summary>
    private static List<(string Id, int Index, string Fallback)>? ParseNamedEnum(List<string> scopes, string[] enumNames, Dictionary<string, int> constants)
    {
        foreach (string scope in scopes)
        {
            foreach (Match match in EnumRegex().Matches(scope))
            {
                if (enumNames.Contains(match.Groups[1].Value))
                {
                    return ParseEnumBody(BraceBody(scope, match.Index + match.Length - 1), constants);
                }
            }
        }
        return null;
    }

    /// <summary>Parses "A, B = 5, ENUMS(C, 4), NUM_X" into (identifier, index, humanized fallback).</summary>
    public static List<(string Id, int Index, string Fallback)> ParseEnumBody(string body) => ParseEnumBody(body, new Dictionary<string, int>());

    public static List<(string Id, int Index, string Fallback)> ParseEnumBody(string body, Dictionary<string, int> constants)
    {
        List<(string Id, int Index, string Fallback)> entries = new List<(string Id, int Index, string Fallback)>();
        Dictionary<string, int> known = new Dictionary<string, int>(StringComparer.Ordinal);
        int next = 0;

        foreach (string rawEntry in SplitTopLevel(body))
        {
            string entry = rawEntry.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            Match enums = EnumsMacroRegex().Match(entry);
            if (enums.Success)
            {
                string id = enums.Groups[1].Value;
                int? resolvedCount = Evaluate(enums.Groups[2].Value, constants);
                if (resolvedCount is null)
                {
                    // Unknown size: name the first port of the range, then stop (overrides fill the rest).
                    entries.Add((id, next, Humanize(id)));
                    break;
                }
                int count = resolvedCount.Value;
                known[id] = next;
                for (int i = 0; i < count; i++)
                {
                    entries.Add((i == 0 ? id : $"{id}+{i}", next + i, $"{Humanize(id)} {i + 1}"));
                }
                next += count;
                continue;
            }

            string name = entry;
            int equals = entry.IndexOf('=');
            if (equals >= 0)
            {
                name = entry[..equals].Trim();
                int? value = Evaluate(entry[(equals + 1)..], known.Concat(constants.Where(c => !known.ContainsKey(c.Key))).ToDictionary(c => c.Key, c => c.Value));
                if (value is null)
                {
                    // Can't follow the numbering past an unknown expression: stop here, overrides fill the rest.
                    break;
                }
                next = value.Value;
            }

            if (name.StartsWith("NUM_", StringComparison.Ordinal))
            {
                break;
            }

            known[name] = next;
            entries.Add((name, next, Humanize(name)));
            next++;
        }

        return entries;
    }

    /// <summary>Integer expression with + - * / on literals and known names ("N_TRK * 2", "VCF_MOD_PARAM_0 + 20"); null if anything is unknown.</summary>
    private static int? Evaluate(string expression, Dictionary<string, int> known)
    {
        MatchCollection tokens = Regex.Matches(expression, @"[\w:]+|[-+*/()]|\S");
        int position = 0;

        int? Factor()
        {
            if (position >= tokens.Count)
            {
                return null;
            }
            string token = tokens[position++].Value;
            if (token == "(")
            {
                int? inner = Sum();
                return position < tokens.Count && tokens[position++].Value == ")" ? inner : null;
            }
            if (token == "-")
            {
                return -Factor();
            }
            int scope = token.LastIndexOf("::", StringComparison.Ordinal);
            string name = scope >= 0 ? token[(scope + 2)..] : token;
            return int.TryParse(name, out int literal) ? literal : known.TryGetValue(name, out int value) ? value : null;
        }

        int? Product()
        {
            int? value = Factor();
            while (value is not null && position < tokens.Count && tokens[position].Value is "*" or "/")
            {
                string op = tokens[position++].Value;
                int? right = Factor();
                value = right is null || (op == "/" && right == 0) ? null : op == "*" ? value * right : value / right;
            }
            return value;
        }

        int? Sum()
        {
            int? value = Product();
            while (value is not null && position < tokens.Count && tokens[position].Value is "+" or "-")
            {
                string op = tokens[position++].Value;
                int? right = Product();
                value = right is null ? null : op == "+" ? value + right : value - right;
            }
            return value;
        }

        int? result = Sum();
        return position == tokens.Count ? result : null;
    }

    /// <summary>
    /// Integer constants usable as ENUMS(X, N) sizes: "static const int N = 5;", "constexpr int N{4};", "#define N 8",
    /// including aliases ("NUM_OSC = DroneVoice::NUM_OSC"), resolved by name across the whole plugin.
    /// </summary>
    private static Dictionary<string, int> CollectConstants(List<SourceFile> sources)
    {
        // Same name may be declared several times ("NUM_OSC = 5" in one struct, "NUM_OSC = DroneVoice::NUM_OSC" in another):
        // keep every candidate, the first one that resolves wins.
        List<(string Name, string Value)> raw = new List<(string Name, string Value)>();
        foreach (SourceFile file in sources)
        {
            foreach (Match match in IntConstantRegex().Matches(file.Text))
            {
                raw.Add((match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value));
            }
            foreach (Match match in DefineRegex().Matches(file.Text))
            {
                raw.Add((match.Groups[1].Value, match.Groups[2].Value));
            }
        }

        Dictionary<string, int> constants = new Dictionary<string, int>(StringComparer.Ordinal);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach ((string name, string value) in raw)
            {
                if (!constants.ContainsKey(name) && Evaluate(value, constants) is int resolved)
                {
                    constants[name] = resolved;
                    changed = true;
                }
            }
        }
        return constants;
    }

    /// <summary>
    /// Names given in a loop over an ENUMS range:
    ///   configInput(X + i, string::f("Osc %d trigger", i + 1))
    ///   configInput(X + i, labels[i])                       with   labels[] = { "a", "b" }
    ///   configOutput(X + i, string::f("%s gate", labels[i]))
    /// </summary>
    private static void AddLoopNames(string scope, string kind, List<(string Id, int Index, string Fallback)>? entries, Dictionary<string, string> names)
    {
        if (entries is null)
        {
            return;
        }
        Dictionary<string, List<string>> arrays = StringArrayRegex().Matches(scope)
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => StringLiteralRegex().Matches(g.First().Groups[2].Value).Select(m => Regex.Unescape(m.Groups[1].Value)).ToList());

        foreach (Match call in LoopConfigRegex().Matches(scope))
        {
            if (call.Groups[1].Value != kind)
            {
                continue;
            }
            string baseId = call.Groups[2].Value;
            string loopVar = call.Groups[3].Value;
            string argument = call.Groups[4].Value.Trim();
            // "auto s = std::string("Modulation Signal ") + std::to_string(m + 1); configInput(MOD_INPUT_0 + m, s);"
            Match local = Regex.Match(scope[..call.Index], @"\b(?:auto|std::string|string)\s+" + Regex.Escape(argument) + @"\s*=\s*([^;]+);", RegexOptions.RightToLeft);
            if (Regex.IsMatch(argument, @"^\w+$") && local.Success)
            {
                argument = local.Groups[1].Value.Trim();
            }

            // "MOD_INPUT_0, NEXT = MOD_INPUT_0 + n": the slots up to the next entry are the loop's too.
            List<string> ids = entries.Where(e => e.Id == baseId || e.Id.StartsWith(baseId + "+", StringComparison.Ordinal)).Select(e => e.Id).ToList();
            if (ids.Count == 1 && entries.FindIndex(e => e.Id == baseId) is int at and >= 0 && at + 1 < entries.Count)
            {
                ids.AddRange(Enumerable.Range(1, entries[at + 1].Index - entries[at].Index - 1).Select(k => $"{baseId}+{k}"));
            }

            foreach (string id in ids)
            {
                int i = id == baseId ? 0 : int.Parse(id[(baseId.Length + 1)..]);
                string? name = LoopName(argument, loopVar, i, arrays);
                if (name is not null)
                {
                    names.TryAdd(id, name);
                }
            }
        }
    }

    private static string? LoopName(string argument, string loopVar, int i, Dictionary<string, List<string>> arrays)
    {
        string? Label(string expression)
        {
            Match indexed = Regex.Match(expression.Trim(), @"^(\w+)\s*\[\s*" + Regex.Escape(loopVar) + @"\s*\]$");
            return indexed.Success && arrays.TryGetValue(indexed.Groups[1].Value, out List<string>? values) && i < values.Count ? values[i] : null;
        }

        Match concatenated = Regex.Match(argument, @"^(?:std::)?string\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)\s*\+\s*(?:std::)?to_string\s*\(\s*" + Regex.Escape(loopVar) + @"\s*(\+\s*1\s*)?\)$");
        if (concatenated.Success)
        {
            return Regex.Unescape(concatenated.Groups[1].Value) + (concatenated.Groups[2].Success ? i + 1 : i).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Match format = Regex.Match(argument, @"^(?:rack::)?string::f\(\s*""((?:[^""\\]|\\.)*)""\s*,\s*(.+)\)$");
        if (format.Success)
        {
            string pattern = Regex.Unescape(format.Groups[1].Value);
            string formatArg = format.Groups[2].Value.Trim();
            if (Regex.IsMatch(formatArg, @"^" + Regex.Escape(loopVar) + @"\s*\+\s*1$"))
            {
                return Regex.Replace(pattern, "%d|%i", (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (formatArg == loopVar)
            {
                return Regex.Replace(pattern, "%d|%i", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            string? label = Label(formatArg);
            return label is null ? null : pattern.Replace("%s", label);
        }
        return Label(argument);
    }

    /// <summary>"PITCH_INPUT" → "Pitch", "CLK_OUTPUTS" → "Clk".</summary>
    public static string Humanize(string identifier)
    {
        string trimmed = Regex.Replace(identifier, @"_(INPUTS?|OUTPUTS?)$", "");
        string spaced = trimmed.Replace('_', ' ').ToLowerInvariant().Trim();
        return spaced.Length == 0 ? identifier : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    /// <summary>Returns the text between the brace at openIndex and its matching closing brace.</summary>
    private static string BraceBody(string text, int openIndex)
    {
        int level = 0;
        for (int i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                level++;
            }
            else if (text[i] == '}')
            {
                level--;
                if (level == 0)
                {
                    return text.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }
        return text[(openIndex + 1)..];
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '<' or '{')
            {
                depth++;
            }
            else if (c is ')' or '>' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    /// <summary>Removes // and /* */ comments while leaving string and char literals untouched.</summary>
    public static string StripComments(string text)
    {
        StringBuilder output = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int end = i + 1;
                while (end < text.Length && text[end] != c && text[end] != '\n')
                {
                    end += text[end] == '\\' ? 2 : 1;
                }
                end = Math.Min(end + 1, text.Length);
                output.Append(text, i, end - i);
                i = end;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                output.Append(' ');
            }
            else
            {
                output.Append(c);
                i++;
            }
        }
        return output.ToString();
    }
}