using VcvPatchTools.Cli;
using VcvPatchTools.Core.Patch;
using VcvPatchTools.Diagram;
using VcvPatchTools.Diagram.Analysis;
using VcvPatchTools.Diagram.Layout;

try
{
    return Run(args);
}
catch (InvalidDataException e)
{
    // A broken or foreign file: one readable line, not a stack trace.
    Console.Error.WriteLine($"Invalid patch: {e.Message}");
    return 1;
}

static int Run(string[] args)
{
    if (args.Length >= 2 && args[0] == "catalog" && args[1] == "build")
    {
        string? src = Option(args, "--src");
        if (src is null)
        {
            PrintUsage();
            return 1;
        }
        return CatalogCommand.Build(src, Option(args, "--out") ?? "catalog/ports.json", Option(args, "--report"));
    }

    // "inspect" and "render" are the names these had in vcvdiagram.
    if (args.Length >= 2 && args[0] is "info" or "inspect")
    {
        return Info(args[1]);
    }

    if (args.Length >= 1 && args[0] == "convert")
    {
        return ConvertCommand.Run(args, Console.Out, Console.Error);
    }

    if (args.Length >= 2 && args[0] is "diagram" or "render")
    {
        return Render(args);
    }

    PrintUsage();
    return 1;
}

static int Render(string[] args)
{
    string path = InputPath.Resolve(args[1]);
    string? output = Option(args, "-o");
    string formatText = Option(args, "-f") ?? (output is not null ? Path.GetExtension(output) : "html");
    if (!PatchDiagram.TryParseFormat(formatText, out DiagramFormat format))
    {
        Console.Error.WriteLine($"Unknown format \"{formatText}\" (html, svg, dot, mmd, json)");
        return 1;
    }

    PatchAnalysis? analysis = Load(path);
    if (analysis is null)
    {
        return 1;
    }

    string title = Path.GetFileNameWithoutExtension(path);
    // --unfold all | <group,group…> ; default: the folded overview.
    string? unfoldOption = Option(args, "--unfold");
    IReadOnlySet<string>? unfolded = unfoldOption switch
    {
        null => null,
        "all" => PatchDiagram.GroupKeys(analysis),
        _ => unfoldOption.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(),
    };
    // --scopes (or --scopes=badge): a badge on what they watch; --scopes=modules: boxes in a column on the right.
    MonitorView monitors = args.Contains("--scopes=modules") ? MonitorView.Modules
        : args.Contains("--scopes") || args.Contains("--scopes=badge") ? MonitorView.Badge : MonitorView.Hidden;
    string content = PatchDiagram.Export(PatchDiagram.Layout(analysis, title, unfolded, monitors: monitors, functionNames: args.Contains("--functions"), portTabs: !args.Contains("--no-ports"), libraryLinks: !args.Contains("--no-links")), format);
    output ??= Path.ChangeExtension(Path.GetFileName(path), PatchDiagram.Extension(format));
    File.WriteAllText(output, content);
    Console.WriteLine($"{format} → {output}");
    return 0;
}

static int Info(string rawPath)
{
    byte[]? bytes = Read(rawPath);
    if (bytes is null)
    {
        return 1;
    }
    ConvertCommand.WriteOrigin(PatchArchive.Read(bytes), Console.Out);
    Console.WriteLine();
    return InspectCommand.Run(PatchDiagram.Analyze(bytes), Console.Out);
}

static PatchAnalysis? Load(string rawPath) => Read(rawPath) is byte[] bytes ? PatchDiagram.Analyze(bytes) : null;

static byte[]? Read(string rawPath)
{
    string path = InputPath.Resolve(rawPath);
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return null;
    }
    return File.ReadAllBytes(path);
}

static string? Option(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  vcvpatch info <patch.vcv>");
    Console.Error.WriteLine("    (format, Rack or Cardinal origin, modules by band, voices, groups, typed cables, diagnostics)");
    Console.Error.WriteLine($"  {ConvertCommand.Usage}");
    Console.Error.WriteLine("    (direction from the detected origin unless --to; output next to the input, e.g. Patch.rack.vcv)");
    Console.Error.WriteLine("  vcvpatch diagram <patch.vcv> [-f html|svg|dot|mmd|json] [-o <output file>] [--unfold all|<group,...>]");
    Console.Error.WriteLine("                   [--scopes[=badge|modules]] [--functions] [--no-ports] [--no-links]");
    Console.Error.WriteLine("    (default: folded overview; group keys are listed by \"info\")");
    Console.Error.WriteLine("  vcvpatch catalog build --src <Cardinal/plugins> [--out catalog/ports.json] [--report catalog/scan-report.txt]");
}