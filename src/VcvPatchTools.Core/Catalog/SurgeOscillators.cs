using System.Text.RegularExpressions;

namespace VcvPatchTools.Core.Catalog;

/// <summary>
/// Surge XT's VCO modules take their knob names from the Surge engine: Oscillator.cpp's spawn_osc() makes a
/// ClassicOscillator for ot_classic, whose init_ctrltypes() names its parameters ("oscdata->p[co_shape].set_name("Shape")"),
/// co_shape being an entry of the enum in ClassicOscillator.h.
/// </summary>
public static partial class SurgeOscillators
{
    [GeneratedRegex(@"\bcase\s+(ot_\w+)\s*:|\bnew\s*(?:\([^()]*\)\s*)?(\w+Oscillator)\s*\(")]
    private static partial Regex SpawnRegex();

    [GeneratedRegex(@"\bp\s*\[\s*(\w+)\s*\]\s*\.\s*set_name\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)")]
    private static partial Regex SetNameRegex();

    /// <summary>
    /// Parameter names by oscillator type ("ot_classic" → Shape, Width 1…), in parameter order. When a parameter is named
    /// in both branches of a condition (FM3's "M1 Frequency" / "M1 Ratio"), the last name, the default branch, wins.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParameterNames(IReadOnlyList<SourceFile> engine)
    {
        List<SourceFile> cleaned = engine.Select(s => new SourceFile(s.Path, SourceScanner.StripComments(s.Text))).ToList();
        Dictionary<string, IReadOnlyList<string>> names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        SourceFile? spawner = cleaned.FirstOrDefault(s => s.Text.Contains("spawn_osc", StringComparison.Ordinal));
        if (spawner is null)
        {
            return names;
        }

        // "case ot_window: { if (…) return new (onto) SineOscillator(…); return new (onto) WindowOscillator(…); }": the
        // last class made before the next case is the type's.
        Dictionary<string, string> classByType = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string> pending = new List<string>();
        bool made = false;
        foreach (Match match in SpawnRegex().Matches(spawner.Text))
        {
            if (match.Groups[1].Success)
            {
                if (made)
                {
                    pending.Clear();
                    made = false;
                }
                pending.Add(match.Groups[1].Value);
                continue;
            }
            foreach (string type in pending)
            {
                classByType[type] = match.Groups[2].Value;
            }
            made = pending.Count > 0;
        }

        foreach ((string type, string oscillator) in classByType)
        {
            Match init = cleaned.Select(s => Regex.Match(s.Text, @"\b" + Regex.Escape(oscillator) + @"\s*::\s*init_ctrltypes\s*\([^)]*\)\s*\{"))
                .FirstOrDefault(m => m.Success) ?? Match.Empty;
            if (!init.Success)
            {
                continue;
            }
            string body = cleaned.Select(s => s.Text).First(t => t.Contains(init.Value, StringComparison.Ordinal));
            body = SourceScanner.BraceBodyAt(body, body.IndexOf(init.Value, StringComparison.Ordinal) + init.Length - 1);

            Dictionary<string, string> byId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match setName in SetNameRegex().Matches(body))
            {
                byId[setName.Groups[1].Value] = SourceScanner.CString(setName.Groups[2].Value);
            }
            if (byId.Count == 0)
            {
                continue;
            }
            // ModernOscillator's third knob is named by the mode the patch picks ("--DYNAMIC-NAME--" + a dynamicName
            // function reading mo_multitype_names[]): its possible names, "Triangle / Square / Sine".
            foreach (Match dynamic in Regex.Matches(body, @"\bp\s*\[\s*(\w+)\s*\]\s*\.\s*dynamicName\s*=\s*&\s*(\w+)\s*;"))
            {
                string? options = DynamicNameOptions(cleaned, dynamic.Groups[2].Value);
                if (options is not null)
                {
                    byId[dynamic.Groups[1].Value] = options;
                }
            }

            // The ids are an enum of the oscillator's header: "enum co_params { co_shape = 0, co_width1, … };".
            string firstId = byId.Keys.First();
            List<(string Id, int Index, string Fallback)>? ids = cleaned
                .SelectMany(s => Regex.Matches(s.Text, @"\benum\s+(?:class\s+)?\w*\s*\{").Select(m => SourceScanner.BraceBodyAt(s.Text, m.Index + m.Length - 1)))
                .Where(enumBody => Regex.IsMatch(enumBody, @"(^|,)\s*" + Regex.Escape(firstId) + @"\s*(=|,|$)"))
                .Select(enumBody => SourceScanner.ParseEnumBody(enumBody))
                .FirstOrDefault();
            if (ids is null)
            {
                continue;
            }
            string[] ordered = new string[ids.Max(e => e.Index) + 1];
            foreach ((string id, int index, string _) in ids)
            {
                ordered[index] = byId.GetValueOrDefault(id) ?? "";
            }
            names[type] = ordered;
        }
        return names;
    }

    /// <summary>"struct ModernTriName : ParameterDynamicNameFunction { … mo_multitype_names[mt] … } dpwTriName;": the names it picks from.</summary>
    private static string? DynamicNameOptions(List<SourceFile> engine, string instance)
    {
        foreach (SourceFile file in engine)
        {
            Match function = Regex.Match(file.Text, @"\bstruct\s+\w+\s*:\s*(?:public\s+)?ParameterDynamicNameFunction\s*\{");
            for (; function.Success; function = function.NextMatch())
            {
                int open = function.Index + function.Length - 1;
                string body = SourceScanner.BraceBodyAt(file.Text, open);
                if (!Regex.IsMatch(file.Text[(open + body.Length + 2)..], @"^\s*" + Regex.Escape(instance) + @"\s*;"))
                {
                    continue;
                }
                foreach (Match array in Regex.Matches(body, @"\b(\w+)\s*\["))
                {
                    Match literal = engine.Select(s => Regex.Match(s.Text, @"\b" + Regex.Escape(array.Groups[1].Value) + @"\s*(?:\[[^\]]*\]\s*)+=\s*\{([^}]*)\}")).FirstOrDefault(m => m.Success) ?? Match.Empty;
                    List<string> options = literal.Success
                        ? Regex.Matches(literal.Groups[1].Value, @"""((?:[^""\\]|\\.)*)""").Select(m => SourceScanner.CString(m.Groups[1].Value)).ToList()
                        : new List<string>();
                    if (options.Count > 1)
                    {
                        return string.Join(" / ", options);
                    }
                }
            }
        }
        return null;
    }
}