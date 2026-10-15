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
    [GeneratedRegex(@"create\w*Model\s*<\s*([\w:]+)\s*(?:<([^<>]*)>)?\s*(?:::\s*(\w+)\s*)?,\s*[\w:]+\s*(?:<[^<>]*>)?\s*>\s*\(\s*""([^""]+)""")]
    private static partial Regex CreateModelRegex();

    [GeneratedRegex(@"\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{")]
    private static partial Regex EnumRegex();

    [GeneratedRegex(@"config(Input|Output)\s*\(\s*(?:\w+::)*(\w+)\s*(?:\+\s*(\d+)\s*)?,\s*""((?:[^""\\]|\\.)*)""")]
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

    [GeneratedRegex(@"config(Input|Output)\s*\(\s*(?:\w+::)*(\w+)\s*\+\s*(\w+)\s*,\s*((?:[^;()]|\((?:[^;()]|\([^;()]*\))*\))+)\)\s*;")]
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
        cleaned.AddRange(MacroInstantiations(cleaned));
        List<SourceFile> headers = (includedHeaders ?? Array.Empty<SourceFile>()).Select(s => new SourceFile(s.Path, StripComments(s.Text))).ToList();
        Dictionary<string, int> constants = CollectConstants(cleaned.Concat(headers).ToList());

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
            else if (!TryResolvePorts(registration.Type, cleaned, headers, constants, TemplateArgs(registration, cleaned, constants), inputs, outputs, out modulation))
            {
                problems.Add($"{pluginSlug}/{slug}: struct '{registration.Type}' has no resolvable InputIds/OutputIds enum");
            }

            result[$"{pluginSlug}/{slug}"] = new ModuleInfo(name, tags, inputs, outputs) { Modulation = modulation };
        }

        return new ScanResult(result, problems);
    }

    private static bool TryResolvePorts(string typeName, List<SourceFile> sources, List<SourceFile> headers, Dictionary<string, int> constants, Dictionary<string, int> templateArgs, List<string> inputs, List<string> outputs, out ModMatrix? modulation)
    {
        modulation = null;
        StructSource? found = FindStruct(LastSegment(typeName), sources, depth: 0)
            ?? FindAliasedStruct(typeName, sources)
            ?? (AliasTarget(LastSegment(typeName), sources) is string aliasedType ? FindStruct(aliasedType, sources, depth: 0) : null);
        if (found is null)
        {
            return false;
        }
        found = WithCompositeEnums(found, sources, headers);
        // The file's own constants win over the plugin's (mscHack's "#define nCHANNELS 8" in ASAF8.cpp, another value
        // elsewhere), the template arguments over both (MixMaster<8, 2>).
        constants = new Dictionary<string, int>(constants, StringComparer.Ordinal);
        if (found.FileText.Length > 0)
        {
            foreach ((string name, int value) in CollectConstants(new List<SourceFile> { new SourceFile("", found.FileText) }))
            {
                constants[name] = value;
            }
        }
        foreach ((string name, int value) in templateArgs)
        {
            constants[name] = value;
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
            targets.Add(entry.Id is null ? $"param#{firstTarget + i}" : paramNames.GetValueOrDefault(entry.Id) ?? entry.Fallback);
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

    private sealed record StructSource(List<string> EnumScopes, List<string> ConfigScopes, string FileText = "");

    /// <summary>
    /// SquinkyLabs' "composite" architecture (also StudioSixPlusOne): "using Comp = MixM&lt;WidgetComposite&gt;;" next to
    /// the module, which uses Comp::NUM_INPUTS and configInput(Comp::AUDIO_INPUT, …); the enums are in the composite
    /// class, often in a header outside src/.
    /// </summary>
    private static StructSource WithCompositeEnums(StructSource found, List<SourceFile> sources, List<SourceFile> headers)
    {
        // The struct and its constructors (often defined out of line).
        string body = string.Join("\n", found.ConfigScopes);
        IEnumerable<(string Alias, string Target)> aliases = Regex.Matches(found.FileText, @"\busing\s+(\w+)\s*=\s*(?:typename\s+)?([\w:]+)\s*(?:<[^;]*>)?\s*;")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .Concat(Regex.Matches(found.FileText, @"\btypedef\s+([\w:]+)\s*(?:<[^;]*>)?\s+(\w+)\s*;").Select(m => (m.Groups[2].Value, m.Groups[1].Value)));
        foreach ((string alias, string target) in aliases)
        {
            if (!Regex.IsMatch(body, @"\b" + Regex.Escape(alias) + @"::NUM_(INPUTS|OUTPUTS)\b"))
            {
                continue;
            }
            StructSource? composite = FindStruct(LastSegment(target), sources, depth: 0) ?? FindStruct(LastSegment(target), headers, depth: 0);
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
                        StructSource? baseSource = FindStruct(baseName, sources, depth + 1)
                            ?? (AliasTarget(baseName, sources) is string aliased ? FindStruct(aliased, sources, depth + 1) : null);
                        if (baseSource is not null)
                        {
                            enumScopes.AddRange(baseSource.EnumScopes);
                            configScopes.AddRange(baseSource.ConfigScopes);
                        }
                    }
                }

                // Old-style plugins sometimes declare the enums at file scope next to the struct.
                enumScopes.Add(file.Text);
                return new StructSource(enumScopes, configScopes, file.Text);
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
                ended = true;
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

            List<string> ids = entries.Where(e => e.Id == baseId || e.Id.StartsWith(baseId + "+", StringComparison.Ordinal)).Select(e => e.Id).ToList();

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

        Match concatenated = Regex.Match(argument, @"^(?:(?:std::)?string\s*\(\s*)?""((?:[^""\\]|\\.)*)""\s*\)?\s*\+\s*(?:std::)?to_string\s*\(\s*" + Regex.Escape(loopVar) + @"\s*(\+\s*1\s*)?\)$");
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
    /// <summary>"CV_INPUTS", 2 → "Cv 3"; "OSC_CTRL_PARAM_0", 6 → "Osc ctrl param 7".</summary>
    private static string RangeName(string rangeId, int offset) =>
        $"{Humanize(Regex.Replace(rangeId, "_0$", ""))} {offset + 1}";

    /// <summary>The count closing an enum, not a port: NUM_INPUTS, INPUTS_LEN, OUTPUT_COUNT, OUTPUTS_END, nINPUTS…</summary>
    private static bool IsEnumEnd(string name) =>
        Regex.IsMatch(name, @"^(NUM_\w+|\w*?(INPUTS?|OUTPUTS?|PARAMS?|LIGHTS?)_(LEN|COUNT|END|NUM)|n(INPUTS|OUTPUTS|PARAMS|LIGHTS))$");

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