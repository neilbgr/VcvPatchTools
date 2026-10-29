using System.Text.RegularExpressions;
using VcvPatchTools.Diagram.Analysis;

namespace VcvPatchTools.Diagram.Layout;

/// <summary>
/// Port names drawn as small colored tabs on the edge of a box, where the cable plugs in (after MonoTrail Tech Talk's
/// diagrams): "V/oct", "Gate", "Cutoff"… They tell what a cable acts on without any text along the cable.
/// Names are shortened to fit; the tab's tooltip keeps them whole.
/// </summary>
public static partial class PortTabs
{
    public const double Height = 12;
    private const int maxChars = 10;
    private const int maxJoinedChars = 12;
    private const double charWidth = 5.4;
    private const double padding = 8;

    private static readonly (string Word, string Short)[] abbreviations =
    {
        ("Frequency", "Freq"), ("Resonance", "Res"), ("Modulation", "Mod"), ("Envelope", "Env"), ("Retrigger", "Retrig"),
        ("Channel", "Ch"), ("Input", "In"), ("Output", "Out"), ("Left", "L"), ("Right", "R"), ("Position", "Pos"),
        ("Amount", "Amt"), ("Attack", "Att"), ("Release", "Rel"), ("Sustain", "Sus"), ("Trigger", "Trig"),
    };

    /// <summary>
    /// What a tab reads for the cables of one line: their port, the ports if short enough ("L send" and "R send" read
    /// "L/R send"), else how many.
    /// </summary>
    public static string Text(IEnumerable<string> ports, SignalType signal)
    {
        List<string> shorts = ports.Select(p => Short(p, signal)).Distinct().ToList();
        List<string> paired = StereoPairs(shorts);
        string joined = string.Join(", ", paired);
        return paired.Count == 1 || joined.Length <= maxJoinedChars ? joined : $"{shorts.Count} × {SignalShort(signal)}";
    }

    /// <summary>Names that differ only by their L / R word become one "L/R" name, where the first of them was.</summary>
    private static List<string> StereoPairs(List<string> shorts)
    {
        static string Both(string name) => StereoSide().Replace(name, "L/R", 1);
        HashSet<string> pairs = shorts.Where(n => StereoSide().IsMatch(n))
            .GroupBy(Both)
            .Where(g => g.Select(n => StereoSide().Match(n).Value).Distinct().Count() == 2)
            .Select(g => g.Key)
            .ToHashSet();
        return shorts.Select(n => pairs.Contains(Both(n)) ? Both(n) : n).Distinct().ToList();
    }

    public static double Width(string text) => (text.Length * charWidth) + padding;

    /// <summary>"Filter R cutoff CV (sums onto…)" → "Filter R…", "1V/octave pitch" → "V/oct", "Gate 13 · A2" → "A2".</summary>
    public static string Short(string port, SignalType signal)
    {
        string name = port.Split(" › ")[^1];
        // MIDI learn: the note or the controller tells cells apart. Surge modulation: the input, not what it moves.
        name = name.Split(" · ")[^1].Split(" → ")[0];
        name = Parenthesis().Replace(name, "").Trim();
        if (signal == SignalType.Pitch && Pitch().IsMatch(name))
        {
            return "V/oct";
        }
        foreach ((string word, string shortWord) in abbreviations)
        {
            name = Regex.Replace(name, $@"\b{word}\b", shortWord, RegexOptions.IgnoreCase);
        }
        return name.Length <= maxChars ? name : name[..(maxChars - 1)].TrimEnd() + "…";
    }

    private static string SignalShort(SignalType signal) => signal switch
    {
        SignalType.Audio => "audio",
        SignalType.Pitch => "pitch",
        SignalType.Gate => "gate",
        _ => "CV",
    };

    [GeneratedRegex(@"\s*\([^)]*\)?")]
    private static partial Regex Parenthesis();

    [GeneratedRegex(@"v\s*/\s*oct|pitch", RegexOptions.IgnoreCase)]
    private static partial Regex Pitch();

    [GeneratedRegex(@"\b[LR]\b")]
    private static partial Regex StereoSide();
}