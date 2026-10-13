using VcvPatchTools.Bridge;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Cli;

/// <summary>vcvpatch convert: a patch from Cardinal to VCV Rack, or the other way round.</summary>
internal static class ConvertCommand
{
    public const string Usage = "vcvpatch convert <input.vcv> [output.vcv] [--to cardinal|rack] [--force] [--no-cable-colors]";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length < 2)
        {
            error.WriteLine($"Usage: {Usage}");
            return 1;
        }

        string inputPath = InputPath.Resolve(args[1]);
        if (!File.Exists(inputPath))
        {
            error.WriteLine($"File not found: {inputPath}");
            return 1;
        }

        string? outputPath = null;
        PatchOrigin? explicitTarget = null;
        bool force = false;
        bool noCableColors = false;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--to")
            {
                explicitTarget = i + 1 < args.Length ? ParseTarget(args[i + 1]) : null;
                if (explicitTarget is null)
                {
                    error.WriteLine($"--to must be followed by \"cardinal\" or \"rack\"{(i + 1 < args.Length ? $", got \"{args[i + 1]}\"" : "")}. Usage: {Usage}");
                    return 1;
                }
                i++;
            }
            else if (args[i] == "--force")
            {
                force = true;
            }
            else if (args[i] == "--no-cable-colors")
            {
                noCableColors = true;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error.WriteLine($"Unknown option: \"{args[i]}\". Usage: {Usage}");
                return 1;
            }
            else if (outputPath is null)
            {
                outputPath = InputPath.Resolve(args[i]);
            }
            else
            {
                error.WriteLine($"Unexpected argument: \"{args[i]}\"");
                return 1;
            }
        }

        PatchArchive patch;
        ConversionResult result;
        PatchOrigin? target;
        try
        {
            patch = PatchArchive.Read(File.ReadAllBytes(inputPath));
            DetectionResult detection = PatchDetector.Detect(patch);

            target = explicitTarget ?? ConvertedName.OppositeOf(detection.Origin);
            if (target is null)
            {
                error.WriteLine("Cannot infer the conversion direction (no Cardinal- or Rack-specific module found): specify --to cardinal|rack.");
                return 1;
            }

            output.WriteLine($"Detected origin : {ConvertedName.Describe(detection.Origin)}");
            output.WriteLine($"Converting to   : {ConvertedName.Describe(target.Value)}");
            result = PatchConverter.Convert(patch, target.Value, remapCableColors: !noCableColors);
        }
        catch (InvalidDataException e)
        {
            error.WriteLine($"Invalid patch {inputPath}: {e.Message}");
            return 1;
        }

        outputPath ??= OutputPathResolver.Resolve(inputPath, target.Value, force);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllBytes(outputPath, patch.Write());

        output.WriteLine($"Written: {outputPath}");

        if (result.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Warnings ({result.Warnings.Count}):");
            foreach (string w in result.Warnings)
            {
                output.WriteLine($"  - {w}");
            }
        }

        return 0;
    }

    private static PatchOrigin? ParseTarget(string value) => value.ToLowerInvariant() switch
    {
        "cardinal" => PatchOrigin.Cardinal,
        "rack" => PatchOrigin.Rack,
        _ => null,
    };

    /// <summary>The "detect" part of vcvpatch info: file format and Rack/Cardinal origin, with the modules that tell.</summary>
    public static void WriteOrigin(PatchArchive patch, TextWriter output)
    {
        DetectionResult detection = PatchDetector.Detect(patch);
        output.WriteLine($"Format: {(patch.WasArchive ? "tar+zstd archive" : "raw JSON")}");
        output.WriteLine($"Origin: {ConvertedName.Describe(detection.Origin)}");
        WriteModules(output, $"Cardinal modules ({detection.CardinalOnlyModules.Count})", detection.CardinalOnlyModules);
        WriteModules(output, $"VCV Rack Core modules, never resaved by Cardinal ({detection.RackOnlyModules.Count})", detection.RackOnlyModules);
    }

    private static void WriteModules(TextWriter output, string heading, List<ModuleSlug> modules)
    {
        if (modules.Count == 0)
        {
            return;
        }
        output.WriteLine($"{heading}: {string.Join(", ", modules.Select(s => s.Model).Distinct().OrderBy(s => s, StringComparer.Ordinal))}");
    }
}