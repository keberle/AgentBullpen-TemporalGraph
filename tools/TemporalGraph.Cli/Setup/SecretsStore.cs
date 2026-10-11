using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TemporalGraph.Cli.Setup;

/// <summary>The user-secrets store <c>setup</c> reads and writes. Values never leave it except to the apps that read it.</summary>
public interface ISecretsStore
{
    /// <summary>The user-secrets id (<c>temporalgraph-cli</c>, or a temporary one in tests).</summary>
    string Id { get; }

    /// <summary>Every key in the store, with its value (keys compare case-insensitively, as configuration does).</summary>
    IReadOnlyDictionary<string, string> Read();

    /// <summary>Adds or replaces these keys; every other key is kept.</summary>
    void Set(IReadOnlyDictionary<string, string> values);

    /// <summary>Removes these keys; every other key is kept, and the store file is never deleted.</summary>
    void Remove(IReadOnlyCollection<string> keys);
}

public sealed class SecretsStoreException(string message) : Exception(message);

/// <summary>
/// The store behind <c>UserSecretsId</c>, read through the configuration API and written with the <c>dotnet user-secrets</c> tool.
/// Values go to the tool as JSON on standard input (<c>dotnet user-secrets set --id &lt;id&gt;</c> merges it into the store), never as a
/// command-line argument, so a key is not visible in the process list. The tool's own output is not echoed.
/// </summary>
public sealed class UserSecretsStore(string id) : ISecretsStore
{
    public string Id => id;

    public IReadOnlyDictionary<string, string> Read() =>
        new ConfigurationBuilder().AddUserSecrets(id, reloadOnChange: false).Build().AsEnumerable()
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);

    public void Set(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > 0) Run(["set", "--id", id], JsonSerializer.Serialize(values));
    }

    public void Remove(IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys) Run(["remove", key, "--id", id], stdin: null);
    }

    private static void Run(string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // --id needs no project; a neutral folder keeps the tool from looking for one.
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("user-secrets");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new SecretsStoreException("Could not start `dotnet user-secrets`.");
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        Task.WaitAll(stdout, stderr);
        if (p.ExitCode != 0) throw new SecretsStoreException($"`dotnet user-secrets {args[0]}` failed (exit {p.ExitCode}).");
    }
}

/// <summary>A copy of a store in memory. The walkthrough runs a change on one to show it on the review screen before writing.</summary>
public sealed class MemoryStore(string id, IReadOnlyDictionary<string, string> values) : ISecretsStore
{
    private readonly Dictionary<string, string> _values = new(values, StringComparer.OrdinalIgnoreCase);

    public string Id => id;

    public IReadOnlyDictionary<string, string> Read() => new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);

    public void Set(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values) _values[key] = value;
    }

    public void Remove(IReadOnlyCollection<string> keys)
    {
        foreach (var key in keys) _values.Remove(key);
    }
}

/// <param name="Old">The store's value before; null = not in the store.</param>
/// <param name="New">After; null = removed.</param>
public sealed record StoreChange(string Key, string? Old, string? New);

/// <summary>
/// What a walkthrough action will write, worked out on a <see cref="MemoryStore"/> copy (<see cref="Preview"/>). The review
/// screen shows it and Save writes exactly it (<see cref="Apply"/>), so what the user confirmed is what lands in the store.
/// </summary>
public sealed record ChangePlan(IReadOnlyList<StoreChange> Changes)
{
    public bool IsEmpty => Changes.Count == 0;

    public static ChangePlan Preview(ISecretsStore store, Action<SetupService> change)
    {
        var before = store.Read();
        var copy = new MemoryStore(store.Id, before);
        change(new SetupService(copy));
        var after = copy.Read();
        var changes = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(k => new StoreChange(k, before.GetValueOrDefault(k), after.GetValueOrDefault(k)))
            .Where(c => !string.Equals(c.Old, c.New, StringComparison.Ordinal))
            .ToList();
        return new ChangePlan(changes);
    }

    /// <summary>Writes the plan. Refuses (and writes nothing) if a key it changes was changed meanwhile, by another window say.</summary>
    public void Apply(ISecretsStore store)
    {
        if (IsEmpty) return;
        var now = store.Read();
        if (Changes.Any(c => !string.Equals(now.GetValueOrDefault(c.Key), c.Old, StringComparison.Ordinal)))
            throw new SecretsStoreException("The settings changed since this review (another setup window?); nothing was saved. Look at the home screen and try again.");
        var set = Changes.Where(c => c.New is not null).ToDictionary(c => c.Key, c => c.New!, StringComparer.OrdinalIgnoreCase);
        if (set.Count > 0) store.Set(set);
        var remove = Changes.Where(c => c.New is null).Select(c => c.Key).ToList();
        if (remove.Count > 0) store.Remove(remove);
    }
}
