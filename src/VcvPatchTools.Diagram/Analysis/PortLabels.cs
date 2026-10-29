using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Diagram.Analysis;

/// <summary>
/// Aluminium's Fader, Faders2, Faders4 and Buttons rename their ports after the label typed above each fader or button
/// ("Fader 2" becomes "Cutoff"), PadX after its one label ("Lane 3" becomes "Drums 3"): the name the module itself shows.
/// </summary>
public static class PortLabels
{
    /// <summary>The port named as its module names it from <see cref="PatchModule.Labels"/>; unlabeled ports are unchanged.</summary>
    public static string Name(PatchModule module, int index, string port, bool input)
    {
        if (module.Plugin != "Aluminium")
        {
            return port;
        }
        if (module.Model == "PadX")
        {
            return Label(module, 0) is string lane
                ? input ? $"{lane} (poly)" : $"{lane} {index + 1}"
                : port;
        }
        return module.Model is "Fader" or "Faders2" or "Faders4" or "Buttons" && Label(module, index) is string label ? label : port;
    }

    /// <summary>Whether <see cref="Name"/> names this port after a typed label.</summary>
    public static bool IsLabeled(PatchModule module, int index, bool input) => Name(module, index, "", input).Length > 0;

    private static string? Label(PatchModule module, int index) => index < module.Labels.Count && module.Labels[index].Length > 0 ? module.Labels[index] : null;
}