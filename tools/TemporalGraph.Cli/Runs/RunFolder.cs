using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemporalGraph.Cli.Runs;

/// <summary>
/// <c>runs/&lt;runId&gt;/</c> at the repo root: the system of record for one command.
/// Files: run.json, stage-0.json, stage-1.json, stage-2.json, stage-3.json, stage-4.json, book.sqlite, crosscheck.json, checks.json, calls.jsonl,
/// failures.jsonl, cost.json. Never holds the API key.
/// </summary>
public sealed class RunFolder : TemporalGraph.Llm.ILlmCallLog
{
    public const string RunJson = "run.json";
    public const string Stage0Json = "stage-0.json";
    public const string ChecksJson = "checks.json";
    public const string CallsJsonl = "calls.jsonl";
    public const string CostJson = "cost.json";
    public const string Stage1Json = "stage-1.json";
    public const string Stage2Json = "stage-2.json";
    public const string Stage3Json = "stage-3.json";
    public const string Stage4Json = "stage-4.json";
    /// <summary>The scratch book database of <c>index build-db</c> (not committed, not embedded).</summary>
    public const string BookSqlite = "book.sqlite";
    public const string CrosscheckJson = "crosscheck.json";
    public const string FailuresJsonl = "failures.jsonl";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep ’ and É readable
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonLine = new(Json) { WriteIndented = false };
    private readonly Lock _callsLock = new();
    private readonly Lock _failuresLock = new();

    public string RunId { get; }
    public string Path { get; }

    private RunFolder(string runId, string path)
    {
        RunId = runId;
        Path = path;
    }

    /// <summary>Creates <c>&lt;runsRoot&gt;/&lt;UTC timestamp&gt;-&lt;6 hex&gt;/</c> with an empty calls.jsonl.</summary>
    public static RunFolder Create(string runsRoot)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        var path = System.IO.Path.Combine(runsRoot, runId);
        Directory.CreateDirectory(path);
        System.IO.File.WriteAllText(System.IO.Path.Combine(path, CallsJsonl), "");
        return new RunFolder(runId, path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Reads a file of a run that may be running in another process, without holding it against its writer
    /// (<c>index watch</c> and <c>index status</c> read run.json and cost.json while <c>index run</c> rewrites them).</summary>
    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Writes a run's JSON file; a reader that has it open for a moment (an editor, a scanner) is waited out.</summary>
    private static void WriteText(string path, string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.File.WriteAllText(path, text);
                return;
            }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50); }
        }
    }

    /// <summary>An existing run folder (e.g. for <c>index resolve --run</c>).</summary>
    public static RunFolder Open(string runsRoot, string runIdOrPath)
    {
        var path = Directory.Exists(runIdOrPath) ? runIdOrPath : System.IO.Path.Combine(runsRoot, runIdOrPath);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Run not found: {path}");
        return new RunFolder(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path)), path);
    }

    /// <summary>Appends one JSON line to failures.jsonl (thread-safe). Each line has a taxonomy "type".</summary>
    public void AppendFailure(object entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonLine);
        lock (_failuresLock) System.IO.File.AppendAllText(File(FailuresJsonl), line + "\n");
    }

    /// <summary>Drops failures.jsonl lines matching <paramref name="remove"/> (e.g. an earlier scoring pass).</summary>
    public void RemoveFailures(Func<System.Text.Json.Nodes.JsonNode, bool> remove)
    {
        var path = File(FailuresJsonl);
        if (!System.IO.File.Exists(path)) return;
        lock (_failuresLock)
        {
            var keep = System.IO.File.ReadAllLines(path).Where(l => l.Length > 0 && !remove(System.Text.Json.Nodes.JsonNode.Parse(l)!)).ToList();
            System.IO.File.WriteAllText(path, keep.Count == 0 ? "" : string.Join("\n", keep) + "\n");
        }
    }

    /// <summary>Adds or replaces one stage's token totals in cost.json ({ stages: { name: … }, total: … }).</summary>
    public void WriteStageCost(string stage, object stageCost, Func<long, long, long, object> totalReport)
    {
        var path = File(CostJson);
        var stages = new System.Text.Json.Nodes.JsonObject();
        if (System.IO.File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject existing
            && existing["stages"] is System.Text.Json.Nodes.JsonObject old)
            stages = (System.Text.Json.Nodes.JsonObject)old.DeepClone();
        stages[stage] = JsonSerializer.SerializeToNode(stageCost, Json);
        long Sum(string field) => stages.Sum(kv => kv.Value?[field]?.GetValue<long>() ?? 0);
        Write(CostJson, new { stages, total = totalReport(Sum("calls"), Sum("inputTokens"), Sum("outputTokens")) });
    }

    /// <summary>The same with the prompt-cache counts (<c>cacheReadTokens</c>, <c>cacheWriteTokens</c>) summed into the total.</summary>
    public void WriteStageCostWithCache(string stage, object stageCost, Func<long, long, long, long, long, object> totalReport)
    {
        var path = File(CostJson);
        var stages = new System.Text.Json.Nodes.JsonObject();
        if (System.IO.File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject existing
            && existing["stages"] is System.Text.Json.Nodes.JsonObject old)
            stages = (System.Text.Json.Nodes.JsonObject)old.DeepClone();
        stages[stage] = JsonSerializer.SerializeToNode(stageCost, Json);
        long Sum(string field) => stages.Sum(kv => kv.Value?[field]?.GetValue<long>() ?? 0);
        Write(CostJson, new { stages, total = totalReport(Sum("calls"), Sum("inputTokens"), Sum("outputTokens"), Sum("cacheReadTokens"), Sum("cacheWriteTokens")) });
    }

    /// <summary>Appends a command to run.json's "commands" list, creating run.json if needed; other fields are merged in.</summary>
    public void RecordCommand(object command, IReadOnlyDictionary<string, object?> fields)
    {
        var path = File(RunJson);
        var run = System.IO.File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject o
            ? o : new System.Text.Json.Nodes.JsonObject { ["runId"] = RunId };
        var commands = run["commands"] as System.Text.Json.Nodes.JsonArray ?? [];
        run.Remove("commands");
        commands.Add(JsonSerializer.SerializeToNode(command, Json));
        run["commands"] = commands;
        foreach (var (k, v) in fields)
        {
            var node = JsonSerializer.SerializeToNode(v, Json);
            if (node is System.Text.Json.Nodes.JsonObject newObj && run[k] is System.Text.Json.Nodes.JsonObject oldObj)
                foreach (var (ck, cv) in newObj.ToList()) { newObj.Remove(ck); oldObj[ck] = cv; }
            else run[k] = node;
        }
        WriteText(path, run.ToJsonString(Json) + "\n");
    }

    /// <summary>Merges top-level fields into run.json (creating it), without adding a command: how <c>index run</c> says where it
    /// is (<c>startedAt</c>, <c>plan</c>, <c>currentStage</c>, then <c>finishedAt</c>) for <c>index watch</c> and <c>index status</c>.</summary>
    public void Merge(IReadOnlyDictionary<string, object?> fields)
    {
        var path = File(RunJson);
        var run = System.IO.File.Exists(path) && System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) is System.Text.Json.Nodes.JsonObject o
            ? o : new System.Text.Json.Nodes.JsonObject { ["runId"] = RunId };
        foreach (var (k, v) in fields) run[k] = JsonSerializer.SerializeToNode(v, Json);
        WriteText(path, run.ToJsonString(Json) + "\n");
    }

    public void Write(string name, object value) =>
        WriteText(File(name), JsonSerializer.Serialize(value, Json) + "\n");

    /// <summary>Appends one JSON line to calls.jsonl (thread-safe).</summary>
    public void AppendCall(object entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonLine);
        lock (_callsLock) System.IO.File.AppendAllText(File(CallsJsonl), line + "\n");
    }
}

/// <summary>The repo's current commit, for run.json.</summary>
public static class GitInfo
{
    public sealed record Commit(string Sha, bool Dirty);

    public static Commit Current(string repoRoot)
    {
        var sha = Git(repoRoot, "rev-parse HEAD")?.Trim();
        var status = Git(repoRoot, "status --porcelain --untracked-files=no");
        return new Commit(string.IsNullOrEmpty(sha) ? "unknown" : sha, !string.IsNullOrWhiteSpace(status));
    }

    private static string? Git(string dir, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception) // git not installed, not a repo, …
        {
            return null;
        }
    }
}

/// <summary>Finds the repo root, so runs/ and data/books/ resolve the same from any folder (Aspire runs the CLI in its project folder).</summary>
public static class RepoPaths
{
    public const string Marker = "TemporalGraph.slnx";

    /// <summary>The nearest folder at or above the current directory (then the executable's folder) that holds TemporalGraph.slnx; else the current directory.</summary>
    public static string FindRoot() =>
        Override ?? FindUp(Directory.GetCurrentDirectory()) ?? FindUp(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();

    /// <summary><c>--repo &lt;path&gt;</c>, set by the global options before a command runs; null: walk up from the current directory.</summary>
    public static string? Override { get; set; }

    private static string? FindUp(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, Marker))) return dir.FullName;
        return null;
    }

    public static string Runs(string root) => System.IO.Path.Combine(root, "runs");
    public static string Books(string root) => System.IO.Path.Combine(root, "data", "books");
}
