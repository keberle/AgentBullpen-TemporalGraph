namespace TemporalGraph.Cli;

/// <summary>
/// The command a person types to run the CLI: the launch script at the repo root, not the assembly. Everything the CLI prints as a
/// command to run next (the help's usage line, "Continue: …", "Run … setup") starts with it, so it can be copied as it stands.
/// </summary>
public static class CliName
{
    /// <summary><c>.\temporalgraph.cli.ps1</c> on Windows, <c>./temporalgraph.cli.sh</c> elsewhere.</summary>
    public static string Wrapper { get; } = OperatingSystem.IsWindows() ? @".\temporalgraph.cli.ps1" : "./temporalgraph.cli.sh";
}
