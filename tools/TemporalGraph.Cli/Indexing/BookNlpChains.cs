using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli.Runs;

namespace TemporalGraph.Cli.Indexing;

// BookNLP (https://github.com/booknlp/booknlp, MIT) as Stage 2's sidecar. It runs once per book, as a
// separate Python process (tools/crosscheck/booknlp_run.py in the venv that tools/crosscheck/run-booknlp.ps1 installs), and
// its output is kept under the run folder: runs/<runId>/stage2/booknlp/ (mentions.tsv, <book>.book, run-info.json).
// There is no .NET port; the CLI only starts the process and reads the files.

/// <summary>One BookNLP mention. Offsets are into the book's source text.</summary>
/// <param name="Chain">BookNLP's coreference id: mentions with the same id are one chain.</param>
/// <param name="Cat">PER, LOC, GPE, FAC, ORG or VEH.</param>
/// <param name="Prop">PROP (a name), NOM (a noun phrase: "the baronet") or PRON (a pronoun).</param>
public sealed record ChainMention(int Chain, string Cat, string Prop, int CharStart, int CharEnd, string Text);

/// <summary><c>Indexing:BookNlp</c>: where the sidecar's Python and model files are. Defaults: the venv and models folder of
/// tools/crosscheck/run-booknlp.ps1, under the user's local application data.</summary>
public sealed record BookNlpSettings(string Python, string ModelsDir, string Model = "big", bool ReuseOutput = true)
{
    public static BookNlpSettings From(IConfiguration config)
    {
        var s = config.GetSection("Indexing:BookNlp");
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TemporalGraph");
        var python = OperatingSystem.IsWindows() ? Path.Combine(home, "booknlp-venv", "Scripts", "python.exe") : Path.Combine(home, "booknlp-venv", "bin", "python");
        static string? Value(string? v) => string.IsNullOrWhiteSpace(v) || v.StartsWith('<') ? null : v;
        return new BookNlpSettings(Value(s["Python"]) ?? python, Value(s["ModelsDir"]) ?? Path.Combine(home, "booknlp-models"),
            Value(s["Model"]) ?? "big", s.GetValue("ReuseOutput", true));
    }
}

public static class BookNlpChains
{
    public const string Folder = "stage2/booknlp";
    public const string MentionsFile = "mentions.tsv";
    public const string RunInfoFile = "run-info.json";

    public static string FolderOf(RunFolder run) => Path.Combine(run.Path, "stage2", "booknlp");

    public static bool Exists(RunFolder run) => File.Exists(Path.Combine(FolderOf(run), MentionsFile));

    /// <summary>The chains of a run (stage2/booknlp/mentions.tsv), in text order.</summary>
    public static List<ChainMention> Load(RunFolder run) => Parse(File.ReadLines(Path.Combine(FolderOf(run), MentionsFile)));

    public static List<ChainMention> Parse(IEnumerable<string> lines) =>
        lines.Skip(1).Where(l => l.Length > 0).Select(l =>
        {
            var c = l.Split('\t');
            return new ChainMention(int.Parse(c[0], CultureInfo.InvariantCulture), c[1], c[2],
                int.Parse(c[3], CultureInfo.InvariantCulture), int.Parse(c[4], CultureInfo.InvariantCulture), c[5]);
        }).OrderBy(m => m.CharStart).ThenBy(m => m.CharEnd).ToList();

    /// <summary>
    /// Copies another run's sidecar output into <paramref name="run"/> (the book's text is the same, so BookNLP is not run again).
    /// </summary>
    public static void CopyFrom(RunFolder source, RunFolder run)
    {
        Directory.CreateDirectory(FolderOf(run));
        foreach (var file in Directory.GetFiles(FolderOf(source)))
            File.Copy(file, Path.Combine(FolderOf(run), Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>
    /// Runs the sidecar for <paramref name="stage0"/>'s book into the run folder. Returns null on success, else what went wrong
    /// (the Python is missing, the process failed) with the tail of its output.
    /// </summary>
    public static string? Run(string root, RunFolder run, Stage0Result stage0, BookNlpSettings settings, Action<string>? onLine = null)
    {
        if (!File.Exists(settings.Python))
            return $"BookNLP's Python was not found at {settings.Python}. Install it once with tools/crosscheck/run-booknlp.ps1 (a venv outside the repo), or set Indexing:BookNlp:Python.";
        var script = Path.Combine(root, "tools", "crosscheck", "booknlp_run.py");
        var folder = FolderOf(run);
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo(settings.Python)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -I: isolated (no user site, no current directory on the path); -X utf8: BookNLP opens files without an encoding.
        foreach (var a in new[]
                 {
                     "-I", "-X", "utf8", script, "--root", root, "--book", stage0.BookId,
                     "--body-start", stage0.Body.CharStart.ToString(CultureInfo.InvariantCulture), "--body-end", stage0.Body.CharEnd.ToString(CultureInfo.InvariantCulture),
                     "--sha256", stage0.TextSha256, "--model", settings.Model, "--models-dir", settings.ModelsDir, "--stage2-out", folder,
                 })
            start.ArgumentList.Add(a);
        if (settings.ReuseOutput) start.ArgumentList.Add("--reuse-output");

        var tail = new Queue<string>();
        void Keep(string? line)
        {
            if (line is null) return;
            lock (tail) { tail.Enqueue(line); while (tail.Count > 15) tail.Dequeue(); }
            onLine?.Invoke(line);
        }
        using var process = Process.Start(start);
        if (process is null) return "the BookNLP process did not start";
        process.OutputDataReceived += (_, e) => Keep(e.Data);
        process.ErrorDataReceived += (_, e) => Keep(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        if (process.ExitCode != 0) return $"BookNLP exited with code {process.ExitCode}:\n{string.Join("\n", tail)}";
        return Exists(run) ? null : "BookNLP finished without writing " + MentionsFile;
    }
}
