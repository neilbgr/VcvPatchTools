using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Diagram.Analysis;

/// <summary>
/// The MIDI gate and CC modules (Rack's MIDI to Gate / MIDI CC to CV / Gate to MIDI / CV to MIDI CC, Cardinal's Host MIDI
/// Gate / Host MIDI CC) name their ports "Gate n" / "Cell n": the note or controller each one answers to is only in
/// the patch (learned by the user, saved as data.notes / data.ccs). That number is what tells the cables apart.
/// </summary>
public static class MidiLearn
{
    private static readonly string[] noteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    // General MIDI meanings of the controllers most often used.
    private static readonly Dictionary<int, string> ccNames = new Dictionary<int, string>
    {
        [1] = "mod wheel",
        [2] = "breath",
        [4] = "foot",
        [7] = "volume",
        [10] = "pan",
        [11] = "expression",
        [64] = "sustain pedal",
        [71] = "resonance",
        [74] = "cutoff",
    };

    /// <summary>
    /// "Gate 3" → "Gate 3 · D#2", "Cell 1" → "Cell 1 · CC 74 (cutoff)"; other ports and unlearned cells are unchanged.
    /// Without <paramref name="meaning"/>, no General MIDI meaning: it's what the controller usually does, not what the
    /// patch makes of it (a CC 11 "expression" can clock a sequencer).
    /// </summary>
    public static string Name(PatchModule module, int index, string port, bool meaning = true)
    {
        if (module.Plugin is not ("Core" or "Cardinal"))
        {
            return port;
        }
        if (port.StartsWith("Gate ", StringComparison.Ordinal) && Learned(module.LearnedNotes, index) is int note)
        {
            return $"{port} · {NoteName(note)}";
        }
        if (port.StartsWith("Cell ", StringComparison.Ordinal) && Learned(module.LearnedCcs, index) is int cc)
        {
            return $"{port} · CC {cc}{(meaning && ccNames.TryGetValue(cc, out string? usual) ? $" ({usual})" : "")}";
        }
        // Rack's Gate to MIDI also calls its gate inputs "Cell n".
        return port.StartsWith("Cell ", StringComparison.Ordinal) && Learned(module.LearnedNotes, index) is int gateNote
            ? $"{port} · {NoteName(gateNote)}"
            : port;
    }

    /// <summary>MIDI note number → name, middle C (60) being C4 as in Rack.</summary>
    public static string NoteName(int note) => $"{noteNames[note % 12]}{(note / 12) - 1}";

    private static int? Learned(IReadOnlyList<int> values, int index) => index < values.Count && values[index] is >= 0 and < 128 ? values[index] : null;
}