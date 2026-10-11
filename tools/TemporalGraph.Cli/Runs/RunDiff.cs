using System.Text.Json.Nodes;

namespace TemporalGraph.Cli.Runs;

/// <summary>
/// <c>runs diff</c>: differences between two run folders in checks (checks.json), counts and text hashes
/// (stage-0.json per book) and chapter boundaries (number, title, char_start, char_end).
/// </summary>
public static class RunDiff
{
    public static List<string> Compare(string runA, string runB)
    {
        var diffs = new List<string>();

        // Checks, keyed by book + name.
        var checksA = Checks(runA);
        var checksB = Checks(runB);
        foreach (var key in checksA.Keys.Union(checksB.Keys).Order())
        {
            checksA.TryGetValue(key, out var a);
            checksB.TryGetValue(key, out var b);
            if (a is null) diffs.Add($"check {key}: only in B ({Describe(b!)})");
            else if (b is null) diffs.Add($"check {key}: only in A ({Describe(a)})");
            else if (a.Passed != b.Passed || a.Details != b.Details) diffs.Add($"check {key}: A {Describe(a)} | B {Describe(b)}");
        }

        // Stage 0, per book.
        var booksA = Books(runA);
        var booksB = Books(runB);
        foreach (var id in booksA.Keys.Union(booksB.Keys).Order())
        {
            if (!booksA.TryGetValue(id, out var a)) { diffs.Add($"book {id}: only in B"); continue; }
            if (!booksB.TryGetValue(id, out var b)) { diffs.Add($"book {id}: only in A"); continue; }

            foreach (var field in new[] { "textSha256", "chapterCount", "paragraphCount", "chunkCount" })
                if (a[field]?.ToJsonString() != b[field]?.ToJsonString())
                    diffs.Add($"book {id} {field}: A {a[field]} | B {b[field]}");

            var chA = Chapters(a);
            var chB = Chapters(b);
            foreach (var n in chA.Keys.Union(chB.Keys).Order())
            {
                chA.TryGetValue(n, out var x);
                chB.TryGetValue(n, out var y);
                if (x != y) diffs.Add($"book {id} chapter {n}: A {x?.ToString() ?? "(none)"} | B {y?.ToString() ?? "(none)"}");
            }
        }

        // Stage 1 / Stage 2: counts, then the sets of entities and merges.
        foreach (var file in new[] { RunFolder.Stage1Json, RunFolder.Stage2Json })
        {
            var a = Load(runA, file);
            var b = Load(runB, file);
            if (a is null && b is null) continue;
            if (a is null || b is null) { diffs.Add($"{file}: only in {(a is null ? "B" : "A")}"); continue; }
            var ca = a["counts"]?.AsObject() ?? [];
            var cb = b["counts"]?.AsObject() ?? [];
            foreach (var key in ca.Select(kv => kv.Key).Union(cb.Select(kv => kv.Key)))
                if (ca[key]?.ToJsonString() != cb[key]?.ToJsonString())
                    diffs.Add($"{file} {key}: A {ca[key]?.ToJsonString() ?? "-"} | B {cb[key]?.ToJsonString() ?? "-"}");
        }
        var sA = Load(runA, RunFolder.Stage2Json);
        var sB = Load(runB, RunFolder.Stage2Json);
        if (sA is not null && sB is not null)
        {
            static HashSet<string> Entities(JsonNode s) => s["entities"]!.AsArray().Select(e => $"{e!["type"]} {e["canonicalName"]}").ToHashSet();
            static HashSet<string> Merges(JsonNode s) => s["merges"]!.AsArray().Select(m => $"{m!["a"]} + {m["b"]} → {m["canonicalName"]}").ToHashSet();
            foreach (var (label, x, y) in new[] { ("entity", Entities(sA), Entities(sB)), ("merge", Merges(sA), Merges(sB)) })
            {
                foreach (var only in x.Except(y).Order()) diffs.Add($"{label} only in A: {only}");
                foreach (var only in y.Except(x).Order()) diffs.Add($"{label} only in B: {only}");
            }
        }

        return diffs;
    }

    private static JsonNode? Load(string run, string file)
    {
        var path = Path.Combine(run, file);
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
    }

    private sealed record Check(bool Passed, string Details);
    private sealed record Boundary(string Title, int CharStart, int CharEnd);

    private static string Describe(Check c) => $"{(c.Passed ? "pass" : "FAIL")}: {c.Details}";

    private static Dictionary<string, Check> Checks(string run)
    {
        var path = Path.Combine(run, RunFolder.ChecksJson);
        if (!File.Exists(path)) return [];
        return JsonNode.Parse(File.ReadAllText(path))!.AsArray()
            .Select(n => n!)
            .ToDictionary(
                n => $"{n["book"]}/{n["name"]}",
                n => new Check(n["passed"]!.GetValue<bool>(), n["details"]?.GetValue<string>() ?? ""));
    }

    private static Dictionary<string, JsonNode> Books(string run)
    {
        var path = Path.Combine(run, RunFolder.Stage0Json);
        if (!File.Exists(path)) return [];
        return JsonNode.Parse(File.ReadAllText(path))!["books"]!.AsArray()
            .Select(n => n!)
            .ToDictionary(n => n["id"]!.GetValue<string>());
    }

    private static Dictionary<int, Boundary> Chapters(JsonNode book) =>
        book["chapters"]!.AsArray().Select(n => n!).ToDictionary(
            n => n["number"]!.GetValue<int>(),
            n => new Boundary(n["title"]!.GetValue<string>(), n["charStart"]!.GetValue<int>(), n["charEnd"]!.GetValue<int>()));

    /// <summary>Accepts a run id (looked up under runs/) or a folder path.</summary>
    public static string Resolve(string runOrPath, string runsRoot) =>
        Directory.Exists(runOrPath) ? runOrPath : Path.Combine(runsRoot, runOrPath);
}
