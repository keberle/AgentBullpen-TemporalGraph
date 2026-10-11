using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;
using Spectre.Console.Rendering;

namespace TemporalGraph.Cli;

/// <summary>
/// A command's help lists its own options first and the options every command takes (<see cref="GlobalSettings"/>:
/// <c>--json</c>, <c>--quiet</c>, …) after them. Spectre lists a base class's options first; everything else is its help as it is.
/// </summary>
public sealed class CommandFirstHelp(ICommandAppSettings settings) : HelpProvider(settings)
{
    /// <summary>The long names of the options every command takes.</summary>
    public static readonly IReadOnlySet<string> Global = typeof(GlobalSettings).GetProperties()
        .SelectMany(p => p.GetCustomAttributes(typeof(CommandOptionAttribute), false).OfType<CommandOptionAttribute>())
        .SelectMany(a => a.LongNames).ToHashSet(StringComparer.Ordinal);

    public override IEnumerable<IRenderable> GetOptions(ICommandModel model, ICommandInfo? command) =>
        base.GetOptions(model, command is null ? null : new OwnOptionsFirst(command));

    private static bool IsGlobal(ICommandParameter parameter) => parameter is ICommandOption option && option.LongNames.Any(Global.Contains);

    private sealed class OwnOptionsFirst(ICommandInfo inner) : ICommandInfo
    {
        public string Name => inner.Name;
        public string? Description => inner.Description;
        public bool IsBranch => inner.IsBranch;
        public bool IsDefaultCommand => inner.IsDefaultCommand;
        public bool IsHidden => inner.IsHidden;
        public IReadOnlyList<ICommandParameter> Parameters { get; } = [.. inner.Parameters.Where(p => !IsGlobal(p)), .. inner.Parameters.Where(IsGlobal)];
        public ICommandInfo? Parent => inner.Parent;
        public IReadOnlyList<string[]> Examples => inner.Examples;
        public IReadOnlyList<ICommandInfo> Commands => inner.Commands;
        public ICommandInfo? DefaultCommand => inner.DefaultCommand;
    }
}
