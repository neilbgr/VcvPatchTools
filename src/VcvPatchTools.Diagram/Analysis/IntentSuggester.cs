using VcvPatchTools.Core.Catalog;

namespace VcvPatchTools.Diagram.Analysis;

/// <summary>A first draft of "why this cable exists", in plain words, for the author to rewrite.</summary>
public static class IntentSuggester
{
    /// <summary>A label the author typed (<paramref name="labeledPort"/>) is written as typed, other port names in lower case.</summary>
    public static string Suggest(SignalType signal, string fromTitle, string fromPort, string toTitle, string toPort, Role fromRole, Role toRole, bool labeledPort = false)
    {
        string port = toPort.ToLowerInvariant();
        string shown = labeledPort ? toPort : port;
        switch (signal)
        {
            case SignalType.Audio:
                return port.Contains("channel") || toRole is Role.Mixer or Role.Effect ? $"into {toTitle}" : "audio";
            case SignalType.Pitch:
                return port.Contains("pitch") || port.Contains("v/oct") ? "plays the notes" : $"pitch tracks {shown}";
            case SignalType.Gate:
                // A sequential switch's "clock" isn't a tempo: each rising edge moves it on to the next channel.
                if (toRole == Role.Router && (port.Contains("trig") || port.Contains("clock") || port.Contains("clk")))
                {
                    return "steps to the next channel";
                }
                if (port.Contains("retrig"))
                {
                    return "retriggers on each note";
                }
                if (port.Contains("gate") || port.Contains("trig"))
                {
                    return toRole == Role.Controller ? "fires the envelope / new value" : "triggers";
                }
                if (port.Contains("clock") || port.Contains("clk"))
                {
                    return "sets the tempo";
                }
                if (port.Contains("reset"))
                {
                    return "restarts";
                }
                return port.Contains("run") ? "starts / stops" : $"triggers {shown}";
            default:
                return fromRole is Role.Io or Role.Performance ? $"hand control of {shown}" : $"{Short(fromTitle)} moves {shown}";
        }
    }

    /// <summary>For a modulation-matrix input, name the knobs it moves, strongest first.</summary>
    public static string SuggestMod(string fromTitle, Role fromRole, IReadOnlyList<ModRoute> routes)
    {
        string targets = string.Join(" & ", routes.OrderByDescending(r => Math.Abs(r.Depth)).Select(r => r.Target.ToLowerInvariant()));
        return fromRole is Role.Io or Role.Performance ? $"hand control of {targets}" : $"{Short(fromTitle)} moves {targets}";
    }

    private static string Short(string title) => title.Split(' ')[0];
}