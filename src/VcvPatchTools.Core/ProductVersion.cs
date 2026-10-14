using System.Reflection;

namespace VcvPatchTools.Core;

/// <summary>The version every binary of the solution is built with (Directory.Build.props), and the commit it was built from.</summary>
public static class ProductVersion
{
    /// <summary>"1.0.0 (d6a0afb1)": the version, then the commit when the build knew it.</summary>
    public static string Display { get; } = Format(typeof(ProductVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");

    /// <summary>Shortens an informational version "1.0.0+&lt;full sha&gt;" to "1.0.0 (&lt;8 hex digits&gt;)".</summary>
    internal static string Format(string informationalVersion)
    {
        int plus = informationalVersion.IndexOf('+');
        if (plus < 0)
        {
            return informationalVersion;
        }
        string commit = informationalVersion[(plus + 1)..];
        return $"{informationalVersion[..plus]} ({commit[..Math.Min(8, commit.Length)]})";
    }
}