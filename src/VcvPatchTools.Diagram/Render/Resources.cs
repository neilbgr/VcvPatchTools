using System.Reflection;

namespace VcvPatchTools.Diagram.Render;

/// <summary>The stylesheet and script embedded in this assembly, shared by every renderer.</summary>
public static class Resources
{
    public static string Css { get; } = Read("render.diagram.css") ?? "";

    /// <summary>Static HTML script: pan/zoom, library preview and hover helpers first, then the page wiring that uses them.</summary>
    public static string Script { get; } = string.Join("\n", new[] { "render.panzoom.js", "render.library.js", "render.highlight.js", "render.diagram.js" }.Select(name => Read(name) ?? ""));

    /// <summary>Readable zoom range: below 70 % box titles get too small, so you pan instead.</summary>
    public const double MinZoom = 0.7;

    public const double MaxZoom = 2.0;

    /// <summary>On a phone (see vpdCompact in panzoom.js): low enough to see a whole diagram, pinching in to read it.</summary>
    public const double MinZoomCompact = 0.35;

    private static string? Read(string logicalName)
    {
        using Stream? stream = typeof(Resources).Assembly.GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            return null;
        }
        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}