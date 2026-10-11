using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TemporalGraph.Cli.Setup;

/// <param name="Name">The tool (<c>dotnet</c>, <c>node</c>, <c>aspire</c>).</param>
/// <param name="Needed">The lowest version that works, as the README states it.</param>
/// <param name="Found">The version the tool reports; null if it isn't on the PATH.</param>
public sealed record Prerequisite(string Name, string Needed, string? Found, bool Ok);

/// <summary>The README's prerequisites, checked by running each tool's <c>--version</c>.</summary>
public static partial class Prerequisites
{
    /// <summary>Tool, the lowest version (the README's Quickstart, step 1; <c>global.json</c>; <c>web/package.json</c> engines).</summary>
    public static readonly (string Name, Version Needed)[] Required =
    [
        ("dotnet", new Version(10, 0, 100)),
        ("node", new Version(22, 0, 0)),
        ("aspire", new Version(13, 6, 0)),
    ];

    public static IReadOnlyList<Prerequisite> Probe() =>
        Required.Select(r => Evaluate(r.Name, r.Needed, VersionOf(r.Name))).ToList();

    public static Prerequisite Evaluate(string name, Version needed, string? found) =>
        new(name, needed.ToString(), found, Parse(found) is { } v && v >= needed);

    /// <summary>The first <c>major.minor.patch</c> in the text (<c>v22.11.0</c>, <c>10.0.300</c>, <c>13.6.0+abc</c>).</summary>
    public static Version? Parse(string? text) =>
        text is not null && VersionPattern().Match(text) is { Success: true } m ? new Version(m.Value) : null;

    private static string? VersionOf(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo(tool, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return Parse(output.Result)?.ToString();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;   // not on the PATH
        }
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
