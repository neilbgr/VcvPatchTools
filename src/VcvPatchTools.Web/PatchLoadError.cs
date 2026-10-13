using System.Text.Json;

namespace VcvPatchTools.Web;

/// <summary>What went wrong opening a patch, said the way the app speaks: which file, and why.</summary>
public static class PatchLoadError
{
    public const long MaxPatchBytes = 50 * 1024 * 1024;

    public static string Describe(string fileName, Exception error) => error switch
    {
        // Thrown by the patch reader with a message written for people.
        InvalidDataException => $"{fileName} can't be opened: {error.Message}",
        JsonException => $"{fileName} can't be opened: its patch.json isn't valid JSON ({error.Message})",
        IOException => $"{fileName} couldn't be read: {error.Message}",
        _ => $"{fileName} can't be opened: it doesn't look like a VCV Rack or Cardinal patch, or part of it is damaged ({error.Message})",
    };

    public static string TooBig(string fileName, long size) =>
        $"{fileName} can't be opened: it is {size / (1024.0 * 1024.0):0.#} MB, and patches over {MaxPatchBytes / (1024 * 1024)} MB aren't read here.";
}