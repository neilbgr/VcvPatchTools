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

    // createModel<Type<Args>, …> or createModel<Widget<Args>::M, …> (Surge XT: a typedef inside a templated widget);
    // plugin helpers named alike too (Sapphire's createSapphireModel<Module, Widget>("slug", role)).
    [GeneratedRegex(@"create\w*Model\s*<\s*([\w:]+)\s*(?:<([^<>]*)>)?\s*(?:::\s*(\w+)\s*)?,\s*[\w:]+\s*(?:<[^<>]*>)?\s*>\s*\(\s*(?:""([^""]+)""|(\w+)\s*[),])")]
    private static partial Regex CreateModelRegex();

    // "#define SLUG "atoms"", "constexpr auto SlugFolding {"ACFolding"};", "static const char* NAME = "x";"
    [GeneratedRegex(@"(?:^[ \t]*#[ \t]*define[ \t]+(\w+)[ \t]+""([^""]*)""|\b(?:constexpr|const)\b[^;{=""]*?\b(\w+)\s*(?:\[\s*\])?\s*(?:=\s*|\{\s*)""([^""]*)""\s*\}?\s*;)", RegexOptions.Multiline)]
    private static partial Regex StringConstantRegex();

    [GeneratedRegex(@"\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{")]
    private static partial Regex EnumRegex();

    // configInput(X_INPUT, "Name"), adjacent literals included ("\xe2\x88\x9a" "2 V"); the literal is the whole argument.
    [GeneratedRegex(@"config(Input|Output)\s*\(\s*(?:\w+::)*(\w+)\s*(?:\+\s*(\d+)\s*)?,\s*((?:""(?:[^""\\]|\\.)*""\s*)+)(?=[,)])")]
    private static partial Regex ConfigCallRegex();

    [GeneratedRegex(@"ModulationAssistant\s*<\s*[\w:]+(?:\s*<[^<>]*>)?\s*,\s*([^,<>]+?)\s*,\s*(\w+)\s*,\s*(\w+)\s*,\s*(\w+)\s*>")]
    private static partial Regex ModulationAssistantRegex();

    [GeneratedRegex(@"modulatorIndexFor\s*\([^)]*\)\s*\{[^}]*return\s+(\w+)\s*\+\s*offset\s*\*\s*\w+\s*\+\s*modulator")]
    private static partial Regex ModulatorIndexRegex();

    [GeneratedRegex(@"static\s+constexpr\s+int\s+(\w+)\s*(?:\{\s*(\d+)\s*\}|=\s*(\d+))")]
    private static partial Regex ConstantRegex();

    [GeneratedRegex(@"configParam\w*(?:\s*<[^;]*?>)?\s*\(\s*(\w+)\s*,[^;""]*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex ConfigParamRegex();

    // "static const int N = 3;", "constexpr int N{3};", "static auto constexpr input_count = 16;"
    [GeneratedRegex(@"\b(?:static\s+)?(?:(?:const|constexpr)\s+(?:static\s+)?(?:unsigned\s+)?(?:int|size_t|uint\w*|int\w*|auto)|(?:int|size_t|uint\w*|int\w*|auto)\s+(?:const|constexpr))\s+(\w+)\s*(?:=\s*([\w:]+)\s*;|\{\s*([\w:]+)\s*\})")]
    private static partial Regex IntConstantRegex();

    [GeneratedRegex(@"^\s*#\s*define\s+(\w+)\s+(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex DefineRegex();

    // configInput(X_INPUT + i, …), configInput(ANG_INPUT + i + 1, …), configInput(…)->description = …
    [GeneratedRegex(@"config(Input|Output)\s*\(\s*(?:\w+::)*(\w+)\s*\+\s*(\w+)\s*(?:\+\s*(\d+)\s*)?,\s*((?:[^;()]|\((?:[^;()]|\([^;()]*\))*\))+)\)\s*(?:;|->)")]
    private static partial Regex LoopConfigRegex();

    [GeneratedRegex(@"\b(\w+)\s*\[[^\]]*\]\s*=\s*\{([^}]*)\}")]
    private static partial Regex StringArrayRegex();

    [GeneratedRegex(@"""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"^ENUMS\s*\(\s*(\w+)\s*,\s*(.+)\)$", RegexOptions.Singleline)]
    private static partial Regex EnumsMacroRegex();

    // config(NUM_PARAMS, NUM_INPUTS, NUM_OUTPUTS, NUM_LIGHTS), venomConfig(…), config(0, 8, 1, 0)
    [GeneratedRegex(@"\b(?:config|\w+Config)\s*\(\s*([^,();""]+?)\s*,\s*([^,();""]+?)\s*,\s*([^,();""]+?)\s*,\s*([^,();""]+?)\s*\)")]
    private static partial Regex ConfigCountsRegex();

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+(\w+)\(([^)]*)\)((?:[^\n]*\\\r?\n)*[^\n]*)", RegexOptions.Multiline)]
    private static partial Regex FunctionMacroRegex();

    [GeneratedRegex(@"std::string\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)\s*\+\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringConcatRegex();

    /// <param name="includedHeaders">
    /// Headers the sources include from elsewhere in the plugin (a bundled engine: Surge XT's n_osc_params is in
    /// SurgeStorage.h): only their constants are read, the sources' own ones win.
    /// </param>
    /// <param name="engineSources">
    /// A bundled engine's sources, for names the plugin takes from it at run time (Surge XT's oscillator parameters).
    /// </param>
    public static ScanResult ScanPlugin(string pluginJson, IReadOnlyList<SourceFile> sources, IReadOnlyList<SourceFile>? includedHeaders = null, IReadOnlyList<SourceFile>? engineSources = null)
    {
        List<string> problems = new List<string>();
        Dictionary<string, ModuleInfo> result = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);

        using JsonDocument manifest = JsonDocument.Parse(pluginJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string pluginSlug = manifest.RootElement.GetProperty("slug").GetString() ?? "";

        List<SourceFile> cleaned = sources.Select(s => new SourceFile(s.Path, StripComments(s.Text))).ToList();
        cleaned.AddRange(MacroInstantiations(cleaned));
        cleaned = cleaned.Select(s => s.Text.Contains("#define", StringComparison.Ordinal) ? s with { Text = ExpandExpressionMacros(s.Text) } : s).ToList();
        List<SourceFile> headers = (includedHeaders ?? Array.Empty<SourceFile>()).Select(s => new SourceFile(s.Path, StripComments(s.Text))).ToList();
        Dictionary<string, int> constants = CollectConstants(cleaned.Concat(headers).ToList());
        foreach ((string name, int value) in FileScopeEnumValues(cleaned.Concat(headers)))
        {
            constants.TryAdd(name, value);
        }

        Dictionary<string, (string Type, string? TemplateArgs)> structBySlug = new Dictionary<string, (string Type, string? TemplateArgs)>(StringComparer.Ordinal);
        Dictionary<string, SourceFile> registeredIn = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        // VCOWidget<ot_classic>::M: the widget's argument, Surge XT's oscillator type.
        Dictionary<string, string> widgetArgsBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyList<string>> oscillatorNames = engineSources is null
            ? new Dictionary<string, IReadOnlyList<string>>()
            : SurgeOscillators.ParameterNames(engineSources);
        Dictionary<string, string> pluginStrings = StringConstants(cleaned);
        foreach (SourceFile file in cleaned)
        {
            Dictionary<string, string> fileStrings = StringConstants(new List<SourceFile> { file });
            foreach (Match match in CreateModelRegex().Matches(file.Text + ExpandModelMacros(file.Text)))
            {
                // The slug can be a string constant: createModel<Atoms, AtomsWidget>(SLUG).
                string? slug = match.Groups[4].Success ? match.Groups[4].Value
                    : fileStrings.GetValueOrDefault(match.Groups[5].Value) ?? pluginStrings.GetValueOrDefault(match.Groups[5].Value);
                if (slug is null)
                {
                    continue;
                }
                // "#define X4 XX<4>" then createModel<X4, X4Widget>: the template the macro names.
                Match typeMacro = Regex.Match(file.Text, @"^[ \t]*#[ \t]*define[ \t]+" + Regex.Escape(match.Groups[1].Value) + @"[ \t]+([\w:]+)\s*<([^<>\r\n]*)>[ \t]*\r?$", RegexOptions.Multiline);
                if (!match.Groups[2].Success && !match.Groups[3].Success && typeMacro.Success)
                {
                    if (structBySlug.TryAdd(slug, (typeMacro.Groups[1].Value, typeMacro.Groups[2].Value)))
                    {
                        registeredIn[slug] = file;
                    }
                    continue;
                }
                // Widget<Args>::M: the arguments are the widget's, the module is found through the alias.
                (string Type, string? TemplateArgs) registration = match.Groups[3].Success
                    ? ($"{match.Groups[1].Value}::{match.Groups[3].Value}", null)
                    : (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value : null);
                if (structBySlug.TryAdd(slug, registration))
                {
                    registeredIn[slug] = file;
                    if (match.Groups[3].Success && match.Groups[2].Success)
                    {
                        widgetArgsBySlug[slug] = match.Groups[2].Value.Trim();
                    }
                }
            }
        }

        Dictionary<string, (List<string> Inputs, List<string> Outputs)> metaModules = MetaModuleInfos(cleaned, headers);

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
            if (metaModules.TryGetValue(slug, out (List<string> Inputs, List<string> Outputs) described))
            {
                inputs.AddRange(described.Inputs);
                outputs.AddRange(described.Outputs);
            }
            else if (!structBySlug.TryGetValue(slug, out (string Type, string? TemplateArgs) registration))
            {
                problems.Add($"{pluginSlug}/{slug}: no createModel<...>(\"{slug}\") registration found");
            }
            else if (LocalFirst(cleaned, registeredIn[slug]) is List<SourceFile> ordered
                && !TryResolvePorts(registration.Type, ordered, headers, constants, TemplateArgs(registration, ordered, constants), inputs, outputs, out modulation,
                    widgetArgsBySlug.TryGetValue(slug, out string? widgetArgs) ? oscillatorNames.GetValueOrDefault(widgetArgs) : null))
            {
                problems.Add($"{pluginSlug}/{slug}: struct '{registration.Type}' has no resolvable InputIds/OutputIds enum");
            }

            result[$"{pluginSlug}/{slug}"] = new ModuleInfo(name, tags, inputs, outputs) { Modulation = modulation };
        }

        return new ScanResult(result, problems);
    }

    private static bool TryResolvePorts(string typeName, List<SourceFile> sources, List<SourceFile> headers, Dictionary<string, int> constants, Dictionary<string, int> templateArgs, List<string> inputs, List<string> outputs, out ModMatrix? modulation, IReadOnlyList<string>? engineParamNames = null)
    {
        modulation = null;
        // Blank panels register Rack's own Module: no ports (unless the plugin has a "Module" of its own, as DHE does).
        if (typeName is "Module" or "rack::Module" or "rack::engine::Module" or "engine::Module" && FindStruct("Module", sources, depth: 0) is null)
        {
            return true;
        }
        StructSource? found = FindStruct(LastSegment(typeName), sources, depth: 0)
            ?? FindAliasedStruct(typeName, sources)
            ?? (AliasTarget(LastSegment(typeName), sources) is string aliasedType ? FindStruct(aliasedType, sources, depth: 0) : null)
            // Declared in a header outside src/ (not-things' include/modules/solim.hpp), constructed in src/.
            ?? FindStruct(LastSegment(typeName), sources.Concat(headers).ToList(), depth: 0);
        if (found is null)
        {
            return false;
        }
        found = WithCompositeEnums(found, sources, headers);
        // The file's own constants win over the plugin's (mscHack's "#define nCHANNELS 8" in ASAF8.cpp, another value
        // elsewhere), the struct's over its file's, the template arguments over all (MixMaster<8, 2>).
        constants = new Dictionary<string, int>(constants, StringComparer.Ordinal);
        if (found.FileText.Length > 0)
        {
            foreach ((string name, int value) in CollectConstants(new List<SourceFile> { new SourceFile("", found.FileText) }))
            {
                constants[name] = value;
            }
        }
        // The struct's own constants over its file's (SubmarineFree's SS_112 and SS_208 each have a "deviceCount").
        foreach ((string name, int value) in CollectConstants(new List<SourceFile> { new SourceFile("", string.Join("\n", found.ConfigScopes.Take(1))) }))
        {
            constants[name] = value;
        }
        foreach ((string name, int value) in templateArgs)
        {
            constants[name] = value;
        }

        // The MetaModule hub and its expanders: "config(… ElementCount::count<Info>() …)", Info an alias of an info struct.
        Match counted = Regex.Match(string.Join("\n", found.ConfigScopes), @"\bElementCount\s*::\s*count\s*<\s*(\w+)\s*>");
        if (counted.Success)
        {
            string info = counted.Groups[1].Value;
            string scope = string.Join("\n", found.ConfigScopes.Append(found.FileText));
            Match alias = Regex.Match(scope, @"\busing\s+" + Regex.Escape(info) + @"\s*=\s*(?:\w+::)*(\w+)\s*;|#\s*define\s+" + Regex.Escape(info) + @"\s+(?:\w+::)*(\w+)");
            if (alias.Success)
            {
                info = alias.Groups[1].Success ? alias.Groups[1].Value : alias.Groups[2].Value;
            }
            if (MetaModuleInfo(info, sources.Concat(headers)) is (string _, List<string> jacksIn, List<string> jacksOut))
            {
                inputs.AddRange(jacksIn);
                outputs.AddRange(jacksOut);
                return true;
            }
        }

        List<(string Id, int Index, string Fallback)>? inputEnum = ParseNamedEnum(found.EnumScopes, inputEnumNames, constants);
        List<(string Id, int Index, string Fallback)>? outputEnum = ParseNamedEnum(found.EnumScopes, outputEnumNames, constants);
        // Otherwise, the enums config(PARAMS, INPUTS, OUTPUTS, LIGHTS) closes, whatever their names (Venom's ExpInputId,
        // LFoldInputIds…), or plain counts (ExpertSleepers' "config(0, 8, 1, 0)": unnamed ports, "#1"… as in Rack).
        if (inputEnum is null && outputEnum is null)
        {
            Match config = found.ConfigScopes.Select(scope => ConfigCountsRegex().Match(scope)).FirstOrDefault(m => m.Success) ?? Match.Empty;
            List<string> scopes = found.EnumScopes.Concat(found.HeaderScopes).ToList();
            List<(string Id, int Index, string Fallback)>? closedInputs = config.Success ? EnumClosedBy(LastSegment(config.Groups[2].Value), scopes, constants) : null;
            List<(string Id, int Index, string Fallback)>? closedOutputs = config.Success ? EnumClosedBy(LastSegment(config.Groups[3].Value), scopes, constants) : null;
            if (closedInputs is not null || closedOutputs is not null)
            {
                (inputEnum, outputEnum) = (closedInputs, closedOutputs);
            }
            else
            {
                // DHE: "struct InputId { enum { … } }" in control-ids.h, next to the module.
                inputEnum = ParseNamedEnum(found.HeaderScopes, inputEnumNames, constants);
                outputEnum = ParseNamedEnum(found.HeaderScopes, outputEnumNames, constants);
                if (inputEnum is null && outputEnum is null)
                {
                    // A module that never calls config() has no ports (Notes, a wire manager…).
                    return !found.ConfigScopes.Any(scope => Regex.IsMatch(scope, @"\b(?:config|\w+Config)\s*\(|\bconfig(?:Input|Output)\s*\("));
                }
            }
        }
        // One side named by an enum, the other by a count only: "config(NUM_PARAMS, NUM_INPUTS, 8, NUM_LIGHTS)".
        if (inputEnum is null || outputEnum is null)
        {
            Match counts = found.ConfigScopes.Select(scope => ConfigCountsRegex().Match(scope)).FirstOrDefault(m => m.Success) ?? Match.Empty;
            if (counts.Success)
            {
                List<string> scopes = found.EnumScopes.Concat(found.HeaderScopes).ToList();
                inputEnum ??= EnumClosedBy(LastSegment(counts.Groups[2].Value), scopes, constants);
                outputEnum ??= EnumClosedBy(LastSegment(counts.Groups[3].Value), scopes, constants);
            }
        }

        Dictionary<string, string> inputNames = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> outputNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string scope in found.ConfigScopes)
        {
            foreach (Match match in ConfigCallRegex().Matches(scope))
            {
                Dictionary<string, string> target = match.Groups[1].Value == "Input" ? inputNames : outputNames;
                string key = match.Groups[3].Success && match.Groups[3].Value != "0" ? $"{match.Groups[2].Value}+{match.Groups[3].Value}" : match.Groups[2].Value;
                target.TryAdd(key, CLiterals(match.Groups[4].Value));
            }
        }

        foreach (string scope in found.ConfigScopes)
        {
            AddLoopNames(scope, "Input", inputEnum, inputNames, constants);
            AddLoopNames(scope, "Output", outputEnum, outputNames, constants);
        }

        Fill(inputs, inputEnum, inputNames);
        Fill(outputs, outputEnum, outputNames);
        modulation = FindModMatrix(found, inputEnum, constants, engineParamNames);
        if (modulation is not null)
        {
            // Surge XT's usual name for its modulation inputs, unless the source names them ("Mod 0" on QuadAD).
            HashSet<int> named = inputEnum!.Where(e => inputNames.ContainsKey(e.Id)).Select(e => e.Index).ToHashSet();
            for (int i = 0; i < modulation.InputCount; i++)
            {
                int index = modulation.FirstInput + i;
                while (inputs.Count <= index)
                {
                    inputs.Add("");
                }
                if (!named.Contains(index))
                {
                    inputs[index] = $"Modulation Signal {i + 1}";
                }
            }
        }
        return true;
    }

    /// <summary>
    /// createModel&lt;MixMaster&lt;8, 2&gt;, ...&gt; with "template&lt;int N_TRK, int N_GRP&gt; struct MixMaster": binds
    /// N_TRK = 8, N_GRP = 2, so ENUMS(X, N_TRK * 2) can be sized.
    /// </summary>
    private static Dictionary<string, int> TemplateArgs((string Type, string? TemplateArgs) registration, List<SourceFile> sources, Dictionary<string, int> constants)
    {
        Dictionary<string, int> bound = new Dictionary<string, int>(StringComparer.Ordinal);
        if (registration.TemplateArgs is null)
        {
            return bound;
        }

        Regex declaration = new Regex(@"template\s*<([^<>]*)>\s*(?:struct|class)\s+" + Regex.Escape(LastSegment(registration.Type)) + @"\b");
        foreach (SourceFile file in sources)
        {
            Match match = declaration.Match(file.Text);
            if (!match.Success)
            {
                continue;
            }

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
        return bound;
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
                string result = Substitute(body, parameters, arguments);
                for (string folded = StringConcatRegex().Replace(result, "\"$1$2\""); folded != result; folded = StringConcatRegex().Replace(result, "\"$1$2\""))
                {
                    result = folded;
                }
                expanded.Append('\n').Append(result).Append(';');
            }
        }
        return expanded.ToString();
    }

    /// <summary>
    /// mscHack's mixers share one header written against macros: Mixer_4_0_4.cpp has "#define nCHANNELS 8",
    /// "#define Mixer_ Mixer_4_0_4", then #include "Mixer_x_x_x.hpp" which declares "struct Mixer_". For every source that
    /// defines macros a header of the plugin it includes uses, gives that header as the source sees it: macros replaced,
    /// #if / #ifdef / #else / #endif resolved.
    /// </summary>
    private static List<SourceFile> MacroInstantiations(List<SourceFile> sources)
    {
        List<SourceFile> instances = new List<SourceFile>();
        foreach (SourceFile file in sources)
        {
            Dictionary<string, string> defines = Regex.Matches(file.Text, @"^[ \t]*#[ \t]*define[ \t]+(\w+)[ \t]+([^\r\n]+?)[ \t]*\r?$", RegexOptions.Multiline)
                .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value, StringComparer.Ordinal);
            if (defines.Count == 0)
            {
                continue;
            }
            foreach (Match include in Regex.Matches(file.Text, @"^[ \t]*#[ \t]*include[ \t]*""([^""]+)""", RegexOptions.Multiline))
            {
                string name = Path.GetFileName(include.Groups[1].Value);
                SourceFile? header = sources.FirstOrDefault(s => s != file && Path.GetFileName(s.Path) == name);
                if (header is null || !defines.Keys.Any(d => Regex.IsMatch(header.Text, @"\b(?:struct|class)\s+" + Regex.Escape(d) + @"\b")))
                {
                    continue;
                }
                string text = ResolveConditionals(header.Text, defines);
                text = Regex.Replace(text, @"\b\w+\b", m => defines.TryGetValue(m.Value, out string? value) && Regex.IsMatch(value, @"^[\w.]+$") ? value : m.Value);
                instances.Add(new SourceFile($"{file.Path} > {name}", text));
            }
        }
        return instances;
    }

    /// <summary>Keeps the branches of #if / #ifdef / #ifndef / #else / #endif that these defines select.</summary>
    private static string ResolveConditionals(string text, Dictionary<string, string> defines)
    {
        Dictionary<string, int> numbers = defines.Where(d => int.TryParse(d.Value, out _)).ToDictionary(d => d.Key, d => int.Parse(d.Value), StringComparer.Ordinal);
        bool? Condition(string directive, string expression) => directive switch
        {
            "ifdef" => defines.ContainsKey(expression.Trim()),
            "ifndef" => !defines.ContainsKey(expression.Trim()),
            _ => Regex.Match(expression, @"^\s*(.+?)\s*(==|!=|>=|<=|>|<)\s*(.+?)\s*$") is { Success: true } comparison
                && Evaluate(comparison.Groups[1].Value, numbers) is int left && Evaluate(comparison.Groups[3].Value, numbers) is int right
                ? comparison.Groups[2].Value switch { "==" => left == right, "!=" => left != right, ">=" => left >= right, "<=" => left <= right, ">" => left > right, _ => left < right }
                : Evaluate(expression, numbers) is int value ? value != 0 : null,
        };

        StringBuilder kept = new StringBuilder();
        // Per nesting level: is this branch kept, has a branch of this #if been taken already.
        Stack<(bool Keep, bool Taken)> levels = new Stack<(bool Keep, bool Taken)>();
        bool Keeping() => levels.All(l => l.Keep);
        foreach (string line in text.Split('\n'))
        {
            Match directive = Regex.Match(line, @"^\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b(.*)$");
            if (!directive.Success)
            {
                if (Keeping())
                {
                    kept.Append(line).Append('\n');
                }
                continue;
            }
            string expression = directive.Groups[2].Value.Trim();
            switch (directive.Groups[1].Value)
            {
                case "if" or "ifdef" or "ifndef":
                    // An unknown condition keeps its first branch.
                    bool keep = Condition(directive.Groups[1].Value, expression) ?? true;
                    levels.Push((keep, keep));
                    break;
                case "elif" when levels.Count > 0:
                    (bool _, bool taken) = levels.Pop();
                    bool elif = !taken && (Condition("if", expression) ?? false);
                    levels.Push((elif, taken || elif));
                    break;
                case "else" when levels.Count > 0:
                    (bool _, bool done) = levels.Pop();
                    levels.Push((!done, true));
                    break;
                case "endif" when levels.Count > 0:
                    levels.Pop();
                    break;
            }
        }
        return kept.ToString();
    }

    /// <summary>
    /// The files a source includes from its own folder (transitively) first, then the others: DHE names every module
    /// "Module", each in its folder (modules/blossom/init.cpp registers the one of modules/blossom/module.h).
    /// </summary>
    private static List<SourceFile> LocalFirst(List<SourceFile> sources, SourceFile from)
    {
        // The registering file itself first: a header next to it may hold an unrelated struct of the same name
        // (AmalgamatedHarmonics' Chord module and its chord type).
        List<SourceFile> local = LocalIncludes(sources, from).Prepend(from).Distinct().ToList();
        return local.Count == 1 ? sources : local.Concat(sources.Where(s => !local.Contains(s))).ToList();
    }

    /// <summary>The sources a file includes by a relative path, transitively.</summary>
    private static List<SourceFile> LocalIncludes(List<SourceFile> sources, SourceFile from)
    {
        static string? Full(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }
        Dictionary<string, SourceFile> byPath = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        foreach (SourceFile source in sources)
        {
            if (Full(source.Path) is string full)
            {
                byPath.TryAdd(full, source);
            }
        }

        List<SourceFile> local = new List<SourceFile>();
        Queue<SourceFile> pending = new Queue<SourceFile>(new[] { from });
        while (pending.Count > 0)
        {
            SourceFile file = pending.Dequeue();
            string directory = Path.GetDirectoryName(file.Path) ?? "";
            foreach (Match include in Regex.Matches(file.Text, @"^[ \t]*#[ \t]*include[ \t]*""([^""]+)""", RegexOptions.Multiline))
            {
                if (Full(Path.Combine(directory, include.Groups[1].Value)) is string full && byPath.TryGetValue(full, out SourceFile? header) && !local.Contains(header))
                {
                    local.Add(header);
                    pending.Enqueue(header);
                }
            }
        }
        return local;
    }

    /// <summary>
    /// 4ms MetaModule modules ("rack::Model *modelOctave = GenericModule&lt;OctaveInfo&gt;::create();"): OctaveInfo, in the
    /// core-modules library, has the slug and an Elements array in panel order (OctaveKnob{{…, "Octave", ""}},
    /// AnalogJackInput4ms{{…, "CV", ""}}…); the generic wrapper numbers inputs and outputs in that order.
    /// </summary>
    private static Dictionary<string, (List<string> Inputs, List<string> Outputs)> MetaModuleInfos(List<SourceFile> sources, List<SourceFile> headers)
    {
        Dictionary<string, (List<string> Inputs, List<string> Outputs)> modules = new Dictionary<string, (List<string>, List<string>)>(StringComparer.Ordinal);
        HashSet<string> infos = sources.SelectMany(s => Regex.Matches(s.Text, @"\bGenericModule\s*<\s*(?:\w+::)*(\w+)\s*>\s*::\s*create\s*\(")).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        foreach (string info in infos)
        {
            if (MetaModuleInfo(info, sources.Concat(headers)) is (string slug, List<string> inputs, List<string> outputs))
            {
                modules.TryAdd(slug, (inputs, outputs));
            }
        }
        return modules;
    }

    /// <summary>A MetaModule info struct's slug and jacks, in panel order.</summary>
    private static (string Slug, List<string> Inputs, List<string> Outputs)? MetaModuleInfo(string info, IEnumerable<SourceFile> files)
    {
        foreach (SourceFile file in files)
        {
            Match definition = Regex.Match(file.Text, @"\bstruct\s+" + Regex.Escape(info) + @"\b[^;{]*\{");
            if (!definition.Success)
            {
                continue;
            }
            string body = BraceBody(file.Text, definition.Index + definition.Length - 1);
            Match slug = Regex.Match(body, @"\bslug\s*(?:\{\s*|=\s*)""([^""]+)""");
            Match elements = Regex.Match(body, @"\bElements\s*(?:=\s*)?\{\s*\{");
            if (!slug.Success || !elements.Success)
            {
                return null;
            }
            List<string> inputs = new List<string>();
            List<string> outputs = new List<string>();
            string array = BraceBody(body, elements.Index + elements.Length - 1);
            foreach (Match element in Regex.Matches(array, @"\b(\w+)\s*\{\s*\{[^""{}]*""((?:[^""\\]|\\.)*)"""))
            {
                string type = element.Groups[1].Value;
                if (Regex.IsMatch(type, @"Jack\w*In(put)?|In(put)?\w*Jack"))
                {
                    inputs.Add(CString(element.Groups[2].Value));
                }
                else if (Regex.IsMatch(type, @"Jack\w*Out(put)?|Out(put)?\w*Jack"))
                {
                    outputs.Add(CString(element.Groups[2].Value));
                }
            }
            return (slug.Groups[1].Value, inputs, outputs);
        }
        return null;
    }

    /// <summary>String constants by name, the first definition winning.</summary>
    private static Dictionary<string, string> StringConstants(List<SourceFile> sources)
    {
        Dictionary<string, string> strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in sources.SelectMany(s => StringConstantRegex().Matches(s.Text)))
        {
            strings.TryAdd(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value, match.Groups[1].Success ? match.Groups[2].Value : match.Groups[4].Value);
        }
        return strings;
    }

    /// <summary>A file without the bodies of its structs and classes: its file-scope declarations only.</summary>
    private static string WithoutStructBodies(string text)
    {
        StringBuilder kept = new StringBuilder();
        int position = 0;
        foreach (Match definition in Regex.Matches(text, @"\b(?:struct|class)\s+\w+\b[^;{()]*\{"))
        {
            if (definition.Index < position)
            {
                continue;
            }
            int open = definition.Index + definition.Length - 1;
            kept.Append(text, position, open + 1 - position);
            position = open + 1 + BraceBody(text, open).Length;
        }
        return kept.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>One or more adjacent C string literals, quotes included: "\xe2\x88\x9a" "2 V" → "√2 V".</summary>
    private static string CLiterals(string literals) =>
        string.Concat(StringLiteralRegex().Matches(literals).Select(m => CString(m.Groups[1].Value)));

    /// <summary>A C string literal's content: escapes are bytes ("\xcf\x80" is π in UTF-8), the rest UTF-8 text.</summary>
    public static string CString(string content)
    {
        List<byte> bytes = new List<byte>();
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c != '\\' || i + 1 >= content.Length)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                continue;
            }
            char e = content[++i];
            switch (e)
            {
                case 'x':
                    int hexEnd = i + 1;
                    while (hexEnd < content.Length && hexEnd < i + 3 && Uri.IsHexDigit(content[hexEnd]))
                    {
                        hexEnd++;
                    }
                    if (hexEnd > i + 1)
                    {
                        bytes.Add(Convert.ToByte(content[(i + 1)..hexEnd], 16));
                        i = hexEnd - 1;
                    }
                    break;
                case >= '0' and <= '7':
                    int octalEnd = i;
                    while (octalEnd < content.Length && octalEnd < i + 3 && content[octalEnd] is >= '0' and <= '7')
                    {
                        octalEnd++;
                    }
                    bytes.Add((byte)Convert.ToInt32(content[i..octalEnd], 8));
                    i = octalEnd - 1;
                    break;
                case 'u' when i + 4 < content.Length:
                    bytes.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(Convert.ToInt32(content[(i + 1)..(i + 5)], 16))));
                    i += 4;
                    break;
                default:
                    bytes.AddRange(Encoding.UTF8.GetBytes(e switch { 'n' => "\n", 't' => "\t", 'r' => "\r", _ => e.ToString() }));
                    break;
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Splits on a character outside parentheses, brackets and string literals.</summary>
    private static List<string> SplitTopLevelOn(string text, char separator)
    {
        List<string> parts = new List<string>();
        int depth = 0;
        bool quoted = false;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                continue;
            }
            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                default:
                    if (c == separator && depth == 0)
                    {
                        parts.Add(text[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }
        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>A function-like macro's body with its parameters replaced (#x stringized, a##b pasted).</summary>
    private static string Substitute(string body, string[] parameters, string[] arguments)
    {
        string result = body;
        for (int i = 0; i < parameters.Length; i++)
        {
            string name = Regex.Escape(parameters[i]);
            result = Regex.Replace(result, @"(?<!#)#\s*" + name + @"\b", $"\"{arguments[i]}\"");
            result = Regex.Replace(result, @"\b" + name + @"\b", arguments[i]);
        }
        return Regex.Replace(result, @"\s*##\s*", "");
    }

    /// <summary>
    /// Expands a file's expression macros (no braces, no semicolon), so enums written with them can be read: DHE's
    /// "#define PER_STEP(name, n) name, name##_LAST = (name) + (n)-1" then "enum { PER_STEP(StepCurvature, N), … }".
    /// </summary>
    private static string ExpandExpressionMacros(string text)
    {
        List<(string Name, string[] Parameters, string Body)> macros = FunctionMacroRegex().Matches(text)
            .Select(m => (m.Groups[1].Value, SplitTopLevel(m.Groups[2].Value).Select(p => p.Trim()).ToArray(), Regex.Replace(m.Groups[3].Value, @"\\\r?\n", " ").Trim()))
            .Where(m => m.Item3.Length > 0 && m.Item3.IndexOfAny(new[] { '{', '}', ';' }) < 0)
            .ToList();
        if (macros.Count == 0)
        {
            return text;
        }
        // Leave the #define lines alone, expand everywhere else; a few passes for macros using macros.
        string withoutDefinitions = FunctionMacroRegex().Replace(text, m => macros.Any(d => d.Name == m.Groups[1].Value) ? "" : m.Value);
        for (int pass = 0; pass < 4; pass++)
        {
            string before = withoutDefinitions;
            foreach ((string name, string[] parameters, string body) in macros)
            {
                withoutDefinitions = Regex.Replace(withoutDefinitions, @"(?<![#\w])" + Regex.Escape(name) + @"\s*\(((?:[^()]|\([^()]*\))*)\)", call =>
                {
                    string[] arguments = SplitTopLevel(call.Groups[1].Value).Select(a => a.Trim()).ToArray();
                    return arguments.Length == parameters.Length ? Substitute(body, parameters, arguments) : call.Value;
                });
            }
            if (withoutDefinitions == before)
            {
                break;
            }
        }
        return withoutDefinitions;
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
    private static ModMatrix? FindModMatrix(StructSource found, List<(string Id, int Index, string Fallback)>? inputEnum, Dictionary<string, int> pluginConstants, IReadOnlyList<string>? engineParamNames = null)
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
                paramNames.TryAdd(match.Groups[1].Value, CString(match.Groups[2].Value));
            }
        }

        // Surge XT's VCO: "configParam<…>(OSC_CTRL_PARAM_0 + i, 0, 1, oscstorage->p[i].get_value_f01())": that block's
        // knobs are the engine's oscillator parameters, named by the oscillator type.
        Match engineBlock = Regex.Match(string.Join("\n", found.ConfigScopes), @"configParam\w*(?:\s*<[^;]*?>)?\s*\(\s*(\w+)\s*\+\s*(\w+)\s*,[^;]*\boscstorage\s*->\s*p\s*\[\s*\2\s*\]");
        string? EngineName(string? id)
        {
            if (engineParamNames is null || id is null || !engineBlock.Success)
            {
                return null;
            }
            string block = engineBlock.Groups[1].Value;
            int offset = id == block ? 0 : id.StartsWith(block + "+", StringComparison.Ordinal) ? int.Parse(id[(block.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture) : -1;
            return offset >= 0 && offset < engineParamNames.Count && engineParamNames[offset].Length > 0 ? engineParamNames[offset] : null;
        }

        List<string> targets = new List<string>();
        for (int i = 0; i < count; i++)
        {
            (string Id, int Index, string Fallback) entry = paramEnum.FirstOrDefault(e => e.Index == firstTarget + i);
            targets.Add(EngineName(entry.Id) ?? (entry.Id is null ? $"param#{firstTarget + i}" : paramNames.GetValueOrDefault(entry.Id) ?? entry.Fallback));
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
            // An empty configured name shows as "#n" in Rack.
            byIndex[index] = names.GetValueOrDefault(id) is string name ? (name.Length == 0 ? $"#{index + 1}" : name) : fallback;
        }
        ports.AddRange(byIndex.Select(s => s ?? ""));
    }

    /// <param name="FileText">The file the struct is defined in (for its file-scope aliases).</param>
    /// <summary>
    /// "using FrolicModuleBase = Sapphire::Chaos::ChaosModule&lt;Rucklidge&gt;;" (a base class) or
    /// "using SlothTorporModule = SlothModule&lt;…&gt;;" (a registered type): the struct the alias names.
    /// </summary>
    private static string? AliasTarget(string alias, List<SourceFile> sources)
    {
        Regex aliasRegex = new Regex(@"(?:\busing\s+" + Regex.Escape(alias) + @"\s*=\s*(?:typename\s+)?([\w:]+)\s*(?:<[^;]*>)?\s*;|\btypedef\s+([\w:]+)\s*(?:<[^;]*>)?\s+" + Regex.Escape(alias) + @"\s*;)");
        foreach (SourceFile file in sources)
        {
            Match match = aliasRegex.Match(file.Text);
            if (match.Success)
            {
                string target = LastSegment(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
                return target == alias ? null : target;
            }
        }
        return null;
    }

    private sealed record StructSource(List<string> EnumScopes, List<string> ConfigScopes, string FileText = "")
    {
        /// <summary>Other files defining its constructor out of line (SquinkyLabs: class in a .h, constructor in the .cpp).</summary>
        public List<string> ConstructorFiles { get; init; } = new List<string>();

        /// <summary>Headers the struct's file includes from its folder: enums looked for there last.</summary>
        public List<string> HeaderScopes { get; init; } = new List<string>();
    }

    /// <summary>
    /// SquinkyLabs' "composite" architecture (also StudioSixPlusOne): "using Comp = MixM&lt;WidgetComposite&gt;;" next to
    /// the module, which uses Comp::NUM_INPUTS and configInput(Comp::AUDIO_INPUT, …); the enums are in the composite
    /// class, often in a header outside src/.
    /// </summary>
    private static StructSource WithCompositeEnums(StructSource found, List<SourceFile> sources, List<SourceFile> headers)
    {
        // The struct and its constructors (often defined out of line).
        string body = string.Join("\n", found.ConfigScopes);
        string files = string.Join("\n", found.ConstructorFiles.Prepend(found.FileText));
        IEnumerable<(string Alias, string Target)> aliases = Regex.Matches(files, @"\busing\s+(\w+)\s*=\s*(?:typename\s+)?([\w:]+)\s*(?:<[^;]*>)?\s*;")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .Concat(Regex.Matches(files, @"\btypedef\s+([\w:]+)\s*(?:<[^;]*>)?\s+(\w+)\s*;").Select(m => (m.Groups[2].Value, m.Groups[1].Value)));
        foreach ((string alias, string target) in aliases)
        {
            // "config(Comp::NUM_PARAMS, Comp::NUM_INPUTS, …)", or through a member: "Comp vco; … config(vco.NUM_PARAMS, vco.NUM_INPUTS, …)".
            Match member = Regex.Match(body, @"\b" + Regex.Escape(alias) + @"\s+(\w+)\s*;");
            if (!Regex.IsMatch(body, @"\b" + Regex.Escape(alias) + @"::NUM_(INPUTS|OUTPUTS)\b")
                && !(member.Success && Regex.IsMatch(body, @"\b" + member.Groups[1].Value + @"\.NUM_(INPUTS|OUTPUTS)\b")))
            {
                continue;
            }
            // The header the module includes first: StudioSixPlusOne's EmptyComposite.h also declares a "MaccomoComp".
            HashSet<string> included = Regex.Matches(files, @"#\s*include\s*[<""]([^>""]+)[>""]").Select(m => Path.GetFileName(m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
            List<SourceFile> candidates = sources.Concat(headers).OrderBy(f => included.Contains(Path.GetFileName(f.Path)) ? 0 : 1).ToList();
            StructSource? composite = FindStruct(LastSegment(target), candidates, depth: 0);
            if (composite is not null)
            {
                return found with
                {
                    EnumScopes = composite.EnumScopes.Concat(found.EnumScopes).ToList(),
                    ConfigScopes = found.ConfigScopes.Concat(composite.ConfigScopes).ToList(),
                };
            }
        }
        return found;
    }

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
                List<string> constructorFiles = new List<string>();

                foreach (SourceFile other in sources)
                {
                    foreach (Match ctor in constructor.Matches(other.Text))
                    {
                        configScopes.Add(BraceBody(other.Text, ctor.Index + ctor.Length - 1));
                        if (other != file && !constructorFiles.Contains(other.Text))
                        {
                            constructorFiles.Add(other.Text);
                        }
                    }
                }

                if (match.Groups[1].Success && depth < 3)
                {
                    foreach (string baseName in BaseNames(match.Groups[1].Value))
                    {
                        StructSource? baseSource = FindStruct(baseName, sources, depth + 1)
                            ?? (AliasTarget(baseName, sources) is string aliased ? FindStruct(aliased, sources, depth + 1) : null);
                        if (baseSource is not null)
                        {
                            enumScopes.AddRange(baseSource.EnumScopes);
                            configScopes.AddRange(baseSource.ConfigScopes);
                        }
                    }
                }

                // Old-style plugins sometimes declare the enums at file scope next to the struct, or in a header next to
                // it (DHE's control-ids.h).
                enumScopes.Add(WithoutStructBodies(file.Text));
                return new StructSource(enumScopes, configScopes, file.Text) { ConstructorFiles = constructorFiles, HeaderScopes = LocalIncludes(sources, file).Select(header => header.Text).ToList() };
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

    /// <summary>
    /// The ports counted by one argument of config(…): "0" or "8" (unnamed ports), or the end marker of an enum of any
    /// name. Null when it can't be told.
    /// </summary>
    private static List<(string Id, int Index, string Fallback)>? EnumClosedBy(string count, List<string> scopes, Dictionary<string, int> constants)
    {
        // A number, a constant or an expression of them ("config(0, deviceCount * deviceSetCount, 0, 0)"): ports known by
        // index only.
        if (Evaluate(count, constants) is int ports && !Regex.IsMatch(count, @"^(NUM_\w+|\w+_(LEN|COUNT|END)|Count)$"))
        {
            return Enumerable.Range(0, ports).Select(i => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, $"#{i + 1}")).ToList();
        }
        Regex closing = new Regex(@"(^|,)\s*" + Regex.Escape(count) + @"\s*(=|,|$)");
        foreach (string scope in scopes)
        {
            foreach (Match match in Regex.Matches(scope, @"\benum\s+(?:class\s+)?(?:\w+\s*)?(?::\s*\w+\s*)?\{"))
            {
                string body = BraceBody(scope, match.Index + match.Length - 1);
                if (closing.IsMatch(body))
                {
                    return ParseEnumBody(body, constants);
                }
            }
        }
        return null;
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
            // DHE: "struct InputId { enum { SpinSpeedCv, …, Count }; };"
            foreach (Match match in Regex.Matches(scope, @"\b(?:struct|class)\s+(\w+)\s*\{\s*enum\s*(?::\s*\w+\s*)?\{"))
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
        bool ended = false;

        foreach (string rawEntry in SplitTopLevel(body))
        {
            string entry = rawEntry.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            Match enums = EnumsMacroRegex().Match(entry);
            if (enums.Success && ended)
            {
                break;
            }
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
                    entries.Add((i == 0 ? id : $"{id}+{i}", next + i, RangeName(id, i)));
                }
                next += count;
                continue;
            }

            string name = entry;
            int equals = entry.IndexOf('=');
            if (ended && !IsEnumEnd(equals >= 0 ? entry[..equals].Trim() : entry))
            {
                break;
            }
            if (equals >= 0)
            {
                name = entry[..equals].Trim();
                int? value = Evaluate(entry[(equals + 1)..], known.Concat(constants.Where(c => !known.ContainsKey(c.Key))).ToDictionary(c => c.Key, c => c.Value));
                if (value is null)
                {
                    // Can't follow the numbering past an unknown expression: stop here, overrides fill the rest.
                    break;
                }
                // "OSC_INPUT, CV_INPUT = OSC_INPUT + 4": the slots skipped belong to the entry before (a range).
                int gap = value.Value - next;
                // "enum OutputId { POLY_OUTPUT = 8, OUTPUTS_LEN };": the first 8 ports have no name in the enum.
                if (gap > 0 && gap <= 256 && entries.Count == 0)
                {
                    entries.AddRange(Enumerable.Range(0, value.Value).Select(i => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, $"#{i + 1}")));
                }
                if (gap > 0 && gap <= 256 && entries.Count > 0 && entries[^1].Index == next - 1)
                {
                    string rangeId = entries[^1].Id.Split('+')[0];
                    int start = entries.First(e => e.Id == rangeId).Index;
                    if (entries[^1].Id == rangeId)
                    {
                        entries[^1] = (rangeId, start, RangeName(rangeId, 0));
                    }
                    for (int index = next; index < value.Value; index++)
                    {
                        entries.Add(($"{rangeId}+{index - start}", index, RangeName(rangeId, index - start)));
                    }
                }
                next = value.Value;
            }

            // "OUTPUTS_END = OUTPUTS_START + N - 1, NUM_OUTPUTS = N": the last of the end markers sizes the range.
            if (IsEnumEnd(name))
            {
                // "enum OutputId { OUTPUTS_LEN = MSIZE };": a count and no names, ports known by index.
                if (entries.Count == 0 && !ended && next is > 0 and <= 256)
                {
                    entries.AddRange(Enumerable.Range(0, next).Select(i => (i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, $"#{i + 1}")));
                }
                ended = true;
                continue;
            }
            // "StepCurvature, StepCurvature_LAST = StepCurvature + N - 1": the range's last port.
            if (name.EndsWith("_LAST", StringComparison.Ordinal) && entries.Count > 0 && entries[^1].Id.Split('+')[0] == name[..^5]
                && entries[^1].Index == next)
            {
                // A range of one: X_LAST is X.
                next++;
                continue;
            }
            if (name.EndsWith("_LAST", StringComparison.Ordinal) && entries.Count > 0 && entries[^1].Index == next - 1
                && entries[^1].Id.Split('+')[0] == name[..^5])
            {
                string rangeId = name[..^5];
                int start = entries.First(e => e.Id == rangeId).Index;
                if (entries[^1].Id == rangeId)
                {
                    entries[^1] = (rangeId, start, RangeName(rangeId, 0));
                }
                known[name] = next;
                entries.Add(($"{rangeId}+{next - start}", next, RangeName(rangeId, next - start)));
                next++;
                continue;
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
    /// <summary>
    /// Values of the enums declared outside any struct, when their name is unique in the plugin: MADZINE's
    /// "enum { STAGE_TRIG_OUTPUT, STAGE_ENV_OUTPUT, STAGE_NUM_OUTPUTS };" that its modules' enums are written with.
    /// </summary>
    private static Dictionary<string, int> FileScopeEnumValues(IEnumerable<SourceFile> sources)
    {
        List<(string Name, int Value)> values = new List<(string Name, int Value)>();
        foreach (SourceFile file in sources)
        {
            string fileScope = WithoutStructBodies(file.Text);
            foreach (Match match in Regex.Matches(fileScope, @"\benum\s+(?:class\s+)?\w*\s*(?::\s*\w+\s*)?\{"))
            {
                values.AddRange(ParseEnumBody(BraceBody(fileScope, match.Index + match.Length - 1)).Where(e => !e.Id.Contains('+') && !char.IsDigit(e.Id[0])).Select(e => (e.Id, e.Index)));
            }
        }
        return values.GroupBy(v => v.Name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
    }

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
    private static void AddLoopNames(string scope, string kind, List<(string Id, int Index, string Fallback)>? entries, Dictionary<string, string> names, Dictionary<string, int>? constants = null)
    {
        if (entries is null)
        {
            return;
        }
        AddLoopNamesCore(scope, kind, entries, names);
        AddNestedLoopNames(scope, kind, entries, names, constants ?? new Dictionary<string, int>());
    }

    /// <summary>
    /// Any index expression over the enclosing loops, by port position: "MOD_INPUT + k * MSIZE + j" in two loops
    /// (dbRackModules X4: "Mod %d %d"), "Comp::EQ0_INPUT + i" over separate entries EQ0_INPUT, EQ1_INPUT… (SquinkyLabs LFN).
    /// Names already found win.
    /// </summary>
    private static void AddNestedLoopNames(string scope, string kind, List<(string Id, int Index, string Fallback)> entries, Dictionary<string, string> names, Dictionary<string, int> constants)
    {
        Dictionary<string, List<string>> arrays = StringArrayRegex().Matches(scope)
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => StringLiteralRegex().Matches(g.First().Groups[2].Value).Select(m => CString(m.Groups[1].Value)).ToList());
        Dictionary<int, string> idAt = entries.GroupBy(e => e.Index).ToDictionary(g => g.Key, g => g.First().Id);
        foreach (Match call in Regex.Matches(scope, @"config(Input|Output)\s*\(\s*(?:\w+::)*(\w+)\s*\+\s*([^,;]+?)\s*,\s*((?:[^;()]|\((?:[^;()]|\([^;()]*\))*\))+)\)\s*(?:;|->)"))
        {
            if (call.Groups[1].Value != kind || entries.FindIndex(e => e.Id == call.Groups[2].Value) is not (int at and >= 0))
            {
                continue;
            }
            string before = scope[..call.Index];
            // The loops the index uses, innermost last: "for (int k = 0; k < MSIZE; k++)".
            List<(string Var, int First, int Last, int Step)> loops = new List<(string, int, int, int)>();
            foreach (string variable in Regex.Matches(call.Groups[3].Value, @"\b[A-Za-z_]\w*\b").Select(m => m.Value).Distinct())
            {
                Match header = Regex.Match(before, @"\bfor\s*\(\s*(?:\w+\s+)*" + Regex.Escape(variable) + @"\s*=\s*([^;]+);\s*" + Regex.Escape(variable) + @"\s*(<=?)\s*([^;]+);\s*(?:" + Regex.Escape(variable) + @"\s*\+\+|\+\+\s*" + Regex.Escape(variable) + @"|" + Regex.Escape(variable) + @"\s*\+=\s*(\d+))\s*\)", RegexOptions.RightToLeft);
                if (!header.Success)
                {
                    if (constants.ContainsKey(variable))
                    {
                        continue;
                    }
                    loops.Clear();
                    break;
                }
                if (Evaluate(header.Groups[1].Value, constants) is not int first || Evaluate(header.Groups[3].Value, constants) is not int bound)
                {
                    loops.Clear();
                    break;
                }
                loops.Add((variable, first, header.Groups[2].Value == "<=" ? bound : bound - 1, header.Groups[4].Success ? int.Parse(header.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 1));
            }
            if (loops.Count == 0 || loops.Aggregate(1L, (n, l) => n * Math.Max(0, (l.Last - l.First) / l.Step + 1)) > 4096)
            {
                continue;
            }

            string argument = call.Groups[4].Value.Trim();
            void Visit(int depth, Dictionary<string, int> vars)
            {
                if (depth == loops.Count)
                {
                    Dictionary<string, int> known = new Dictionary<string, int>(constants, StringComparer.Ordinal);
                    foreach ((string name, int value) in vars)
                    {
                        known[name] = value;
                    }
                    if (Evaluate(call.Groups[3].Value, known) is int offset && idAt.TryGetValue(entries[at].Index + offset, out string? id)
                        && LoopName(argument, new Dictionary<string, int>(vars, StringComparer.Ordinal), arrays, name => LocalString(before, name)) is string portName)
                    {
                        names.TryAdd(id, portName);
                    }
                    return;
                }
                (string variable, int first, int last, int step) = loops[depth];
                for (int value = first; value <= last; value += step)
                {
                    vars[variable] = value;
                    Visit(depth + 1, vars);
                }
            }
            Visit(0, new Dictionary<string, int>(StringComparer.Ordinal));
        }
    }

    private static void AddLoopNamesCore(string scope, string kind, List<(string Id, int Index, string Fallback)> entries, Dictionary<string, string> names)
    {
        Dictionary<string, List<string>> arrays = StringArrayRegex().Matches(scope)
            .GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => StringLiteralRegex().Matches(g.First().Groups[2].Value).Select(m => CString(m.Groups[1].Value)).ToList());

        // Ports known by index only, named in a loop: "for (unsigned int i = 0; i < deviceCount; i++) configInput(i, "Signal Sink");"
        foreach (Match call in Regex.Matches(scope, @"config(Input|Output)\s*\(\s*(\w+)\s*,\s*((?:[^;()]|\((?:[^;()]|\([^;()]*\))*\))+)\)\s*(?:;|->)"))
        {
            string loopVar = call.Groups[2].Value;
            string before = scope[..call.Index];
            if (call.Groups[1].Value != kind || !Regex.IsMatch(before, @"\bfor\s*\([^;]*\b" + Regex.Escape(loopVar) + @"\s*=") || int.TryParse(loopVar, out _))
            {
                continue;
            }
            foreach ((string id, int index, string _) in entries.Where(e => int.TryParse(e.Id, out _)))
            {
                if (LoopName(call.Groups[3].Value.Trim(), loopVar, index, arrays, name => LocalString(before, name)) is string name)
                {
                    names.TryAdd(id, name);
                }
            }
        }

        foreach (Match call in LoopConfigRegex().Matches(scope))
        {
            if (call.Groups[1].Value != kind)
            {
                continue;
            }
            string baseId = call.Groups[2].Value;
            string loopVar = call.Groups[3].Value;
            int shift = call.Groups[4].Success ? int.Parse(call.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            // "for (int i = 0; i < N * 2; i += 2) { configOutput(X + i, …left); configOutput(X + i + 1, …right); }"
            Match header = Regex.Match(scope[..call.Index], @"\bfor\s*\([^;]*?\b" + Regex.Escape(loopVar) + @"\s*=\s*(\d+)\s*;[^;]*;\s*(?:" + Regex.Escape(loopVar) + @"\s*\+=\s*(\d+)|\+\+\s*" + Regex.Escape(loopVar) + @"|" + Regex.Escape(loopVar) + @"\s*\+\+)\s*\)", RegexOptions.RightToLeft);
            int first = header.Success ? int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            int step = header.Success && header.Groups[2].Success ? int.Parse(header.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            string argument = call.Groups[5].Value.Trim();
            // "auto s = std::string("Modulation Signal ") + std::to_string(m + 1); configInput(MOD_INPUT_0 + m, s);"
            string before = scope[..call.Index];
            // Only a real loop variable: "configOutput(CLK_OUTPUTS + 0, …)" isn't a loop.
            if (!Regex.IsMatch(before, @"\bfor\s*\([^;]*\b" + Regex.Escape(loopVar) + @"\s*=") || char.IsDigit(loopVar[0]))
            {
                continue;
            }
            if (Regex.IsMatch(argument, @"^\w+$") && LocalString(before, argument) is string value)
            {
                argument = value;
            }

            List<string> ids = entries.Where(e => e.Id == baseId || e.Id.StartsWith(baseId + "+", StringComparison.Ordinal)).Select(e => e.Id).ToList();

            foreach (string id in ids)
            {
                int i = (id == baseId ? 0 : int.Parse(id[(baseId.Length + 1)..])) - shift;
                string? name = i < first || (i - first) % step != 0 ? null : LoopName(argument, loopVar, i, arrays, name => LocalString(before, name));
                if (name is not null)
                {
                    names.TryAdd(id, name);
                }
            }
        }
    }

    /// <summary>The expression a local string variable is initialized with ("auto const step_name = "Step " + …;").</summary>
    private static string? LocalString(string before, string name)
    {
        Match local = Regex.Match(before, @"\b(?:auto|std::string|string)(?:\s+const)?\s+" + Regex.Escape(name) + @"\s*=\s*([^;]+);", RegexOptions.RightToLeft);
        return local.Success ? local.Groups[1].Value.Trim() : null;
    }

    private static string? LoopName(string argument, string loopVar, int i, Dictionary<string, List<string>> arrays, Func<string, string?>? locals = null) =>
        LoopName(argument, new Dictionary<string, int>(StringComparer.Ordinal) { [loopVar] = i }, arrays, locals);

    /// <summary>A port name built from loop variables (one or nested loops: "Mod %d %d" with k + 1, j + 1).</summary>
    private static string? LoopName(string argument, Dictionary<string, int> vars, Dictionary<string, List<string>> arrays, Func<string, string?>? locals = null)
    {
        string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // "std::string label = "Bit " + std::to_string(k + 1); configOutput(k, label);"
        if (Regex.IsMatch(argument, @"^\w+$") && !vars.ContainsKey(argument) && locals?.Invoke(argument) is string local && !local.Contains(argument, StringComparison.Ordinal))
        {
            return LoopName(local, vars, arrays);
        }
        string? Label(string expression)
        {
            Match indexed = Regex.Match(expression.Trim(), @"^(\w+)\s*\[(.+)\]$");
            return indexed.Success && arrays.TryGetValue(indexed.Groups[1].Value, out List<string>? values)
                && Evaluate(indexed.Groups[2].Value, vars) is int index && index >= 0 && index < values.Count ? values[index] : null;
        }

        // "Gate #" + std::to_string(i + 1), "OR " + std::to_string((15 - i) + 1), "Reset " + std::string(colors[i])
        string? Term(string term)
        {
            term = term.Trim();
            Match literal = Regex.Match(term, @"^""((?:[^""\\]|\\.)*)""$");
            if (literal.Success)
            {
                return CString(literal.Groups[1].Value);
            }
            Match number = Regex.Match(term, @"^(?:std::)?to_string\s*\((.*)\)$");
            if (number.Success)
            {
                return Evaluate(number.Groups[1].Value, vars) is int value ? Number(value) : null;
            }
            Match wrapped = Regex.Match(term, @"^(?:std::)?string\s*\((.*)\)$");
            if (wrapped.Success)
            {
                return Concatenation(wrapped.Groups[1].Value);
            }
            // A local string variable: "step_name + "curvature CV"".
            return Regex.IsMatch(term, @"^\w+$") && locals?.Invoke(term) is string local && !local.Contains(term, StringComparison.Ordinal)
                ? Concatenation(local)
                : Label(term);
        }
        string? Concatenation(string expression)
        {
            List<string> terms = SplitTopLevelOn(expression, '+');
            if (terms.Count == 1)
            {
                return Term(terms[0]);
            }
            List<string?> parts = terms.Select(Term).ToList();
            return parts.Contains(null) ? null : string.Concat(parts);
        }
        if (Regex.IsMatch(argument, @"^(?:""(?:[^""\\]|\\.)*""\s*)+$"))
        {
            return CLiterals(argument);
        }
        if (argument.Contains('+') && Concatenation(argument) is string joined)
        {
            return joined;
        }

        // string::f("Mod %d %d", k + 1, j + 1), string::f("%s gate", labels[i])
        Match format = Regex.Match(argument, @"^(?:rack::)?string::f\(\s*""((?:[^""\\]|\\.)*)""\s*,\s*(.+)\)$");
        if (format.Success)
        {
            string pattern = CString(format.Groups[1].Value);
            List<string> formatArgs = SplitTopLevelOn(format.Groups[2].Value, ',');
            int next = 0;
            bool failed = false;
            // %d / %i / %u numbers, %s strings, %c a letter ("Track %c clock", i + 'A'); anything else (%f…) is only known
            // at run time.
            string result = Regex.Replace(pattern, @"%%|%[-+ 0#]*\d*(?:\.\d+)?([a-zA-Z])", placeholder =>
            {
                if (placeholder.Value == "%%")
                {
                    return "%";
                }
                if (next >= formatArgs.Count)
                {
                    failed = true;
                    return placeholder.Value;
                }
                string formatArg = Regex.Replace(formatArgs[next++].Trim(), @"'(.)'", c => Number(c.Groups[1].Value[0]));
                string? text = placeholder.Groups[1].Value switch
                {
                    "d" or "i" or "u" => Evaluate(formatArg, vars) is int value ? Number(value) : null,
                    "c" => Evaluate(formatArg, vars) is int code and > 31 and < 127 ? ((char)code).ToString() : null,
                    "s" => Term(formatArg),
                    _ => null,
                };
                failed |= text is null;
                return text ?? placeholder.Value;
            });
            return failed ? null : result;
        }
        return Label(argument);
    }

    /// <summary>"CV_INPUTS", 2 → "Cv 3"; "OSC_CTRL_PARAM_0", 6 → "Osc ctrl param 7".</summary>
    private static string RangeName(string rangeId, int offset) =>
        $"{Humanize(Regex.Replace(rangeId, "_0$", ""))} {offset + 1}";

    /// <summary>The count closing an enum, not a port: NUM_INPUTS, INPUTS_LEN, OUTPUT_COUNT, OUTPUTS_END, nINPUTS…</summary>
    private static bool IsEnumEnd(string name) =>
        Regex.IsMatch(name, @"^(NUM_\w+|Count|\w*[a-z0-9]Count|\w*?(INPUTS?|OUTPUTS?|PARAMS?|LIGHTS?)_(LEN|COUNT|END|NUM)|n(INPUTS|OUTPUTS|PARAMS|LIGHTS))$");

    /// <summary>"PITCH_INPUT" → "Pitch", "CLK_OUTPUTS" → "Clk".</summary>
    public static string Humanize(string identifier)
    {
        string trimmed = Regex.Replace(identifier, @"_(INPUTS?|OUTPUTS?)$", "");
        // Cardinal's "kHostTimeRolling": the k of constants isn't a word.
        trimmed = Regex.Replace(trimmed, @"^k(?=[A-Z])", "");
        // DHE's PascalCase ids: "StepCurvatureCv" → "Step curvature cv".
        trimmed = Regex.Replace(trimmed, @"(?<=[a-z])(?=[A-Z])", "_");
        string spaced = trimmed.Replace('_', ' ').ToLowerInvariant().Trim();
        return spaced.Length == 0 ? identifier : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    /// <summary>Returns the text between the brace at openIndex and its matching closing brace.</summary>
    /// <summary>The text between the brace at <paramref name="openIndex"/> and its match.</summary>
    public static string BraceBodyAt(string text, int openIndex) => BraceBody(text, openIndex);

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