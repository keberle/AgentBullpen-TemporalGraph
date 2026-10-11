using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TemporalGraph.Cli.Runs;

/// <summary>
/// <c>runs/&lt;runId&gt;/run.log</c>: one line per stage start and end, per chunk done, per retry and per warning, each with its
/// UTC time. <c>--verbose</c> echoes the lines to stderr as they are written; <c>--debug</c> adds each model call's size and timing
/// (never its content). A log may start before its run folder exists (a download comes before the folder): the lines are kept
/// and written when <see cref="AttachTo"/> names the file.
/// </summary>
public sealed class RunLog
{
    public const string FileName = "run.log";

    /// <summary>The log for what no run folder holds (a crash before a run began): <c>runs/cli.log</c>.</summary>
    public const string CliLogName = "cli.log";

    private readonly Lock _lock = new();
    private readonly List<string> _pending = [];
    private readonly TextWriter? _echo;
    private string? _path;

    /// <summary>The log of the command that is running, for the line an unexpected error prints; null when none was opened.</summary>
    public static RunLog? Current { get; set; }

    /// <param name="path">The file; null: kept in memory until <see cref="AttachTo"/>.</param>
    /// <param name="echo">Where <c>--verbose</c> echoes; null: <see cref="CliContext.Stderr"/>.</param>
    public RunLog(string? path = null, TextWriter? echo = null)
    {
        _path = path;
        _echo = echo;
    }

    public static RunLog For(RunFolder run, TextWriter? echo = null) => new(run.File(FileName), echo);

    public string? Path => _path;

    public static string CliLogPath(string root) => System.IO.Path.Combine(RepoPaths.Runs(root), CliLogName);

    public void Write(string line)
    {
        var stamped = $"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} {line.Replace("\r", "").Replace("\n", "\n    ")}";
        lock (_lock)
        {
            if (_path is null) _pending.Add(stamped);
            else Append(_path, [stamped]);
        }
        if (CliContext.Verbose) (_echo ?? CliContext.Stderr).WriteLine(stamped);
    }

    /// <summary>A line only <c>--debug</c> asks for (a model call's size and timing).</summary>
    public void Debug(string line)
    {
        if (CliContext.Debug) Write(line);
    }

    /// <summary>Names the file and writes what was kept so far.</summary>
    public void AttachTo(string path)
    {
        lock (_lock)
        {
            if (_path == path) return;
            _path = path;
            Append(path, _pending);
            _pending.Clear();
        }
    }

    private static void Append(string path, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.AppendAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // a log never stops the work
    }

    /// <summary>An unexpected error: the message and the stack trace into the open log (else <c>runs/cli.log</c>); returns the file.</summary>
    public static string Crash(string root, Exception ex, string command)
    {
        var log = Current is { Path: not null } open ? open : new RunLog(CliLogPath(root));
        lock (log._lock) Append(log._path!, [$"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} unexpected error in `{command}`: {ex.GetType().FullName}: {ex.Message}\n{ex}"]);
        return log._path!;
    }
}

/// <summary>One line of calls.jsonl or failures.jsonl, as far as progress needs it (the request and reply texts are skipped, not read).</summary>
/// <param name="Type">"call" or "retry" (calls.jsonl); the failure's taxonomy type (failures.jsonl).</param>
public sealed record RunEvent(string Type, string? Stage, int? Chapter, int? ChunkIndex, long InputTokens, long OutputTokens, double? LatencyMs, bool Failed,
    DateTimeOffset? Timestamp, int? Attempt, string? Reason, double? DelayMs, long RequestBytes = 0, long ResponseBytes = 0, long CacheReadTokens = 0, long CacheWriteTokens = 0);

/// <summary>What a stage has done so far, from the lines of calls.jsonl.</summary>
public sealed class StageProgress(string stage)
{
    public string Stage { get; } = stage;
    public long Calls { get; set; }

    /// <summary>Every input token sent: what was billed as input, read from the prompt cache or written to it.</summary>
    public long InputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>The share of the input that was read from the prompt cache, 0 to 1.</summary>
    public double CacheShare => InputTokens > 0 ? CacheReadTokens / (double)InputTokens : 0;
    public long Retries { get; set; }
    public int? Chapter { get; set; }
    public int? ChunkIndex { get; set; }
    public double? LastLatencyMs { get; set; }
    public DateTimeOffset? FirstCall { get; set; }
    public DateTimeOffset? LastCall { get; set; }

    /// <summary>The chunks (or, for a stage per chapter, the chapters) that have had a call.</summary>
    public HashSet<int> Units { get; } = [];
}

/// <summary>
/// Reads what a run has appended to calls.jsonl and failures.jsonl since the last <see cref="Poll"/>, from this process
/// (<c>index run</c>'s progress) or from another (<c>index watch</c>). Only whole lines are taken; a line still being written waits.
/// </summary>
public sealed class RunMonitor(string runDir)
{
    private long _callsOffset, _failuresOffset;

    public string RunDir { get; } = runDir;

    /// <summary>Per stage ("stage-1" …), in the order first seen.</summary>
    public Dictionary<string, StageProgress> Stages { get; } = [];

    /// <summary>Warnings (failures.jsonl lines) by stage and type.</summary>
    public Dictionary<(string Stage, string Type), int> Warnings { get; } = [];

    public long TotalCalls => Stages.Values.Sum(s => s.Calls);
    public long TotalInputTokens => Stages.Values.Sum(s => s.InputTokens);
    public long TotalOutputTokens => Stages.Values.Sum(s => s.OutputTokens);

    /// <summary>The new lines, calls first; the totals are updated before it returns.</summary>
    public (IReadOnlyList<RunEvent> Calls, IReadOnlyList<RunEvent> Warnings) Poll()
    {
        var calls = ReadNew(Path.Combine(RunDir, RunFolder.CallsJsonl), ref _callsOffset);
        foreach (var e in calls)
        {
            var key = e.Stage ?? "";
            if (!Stages.TryGetValue(key, out var stage)) Stages[key] = stage = new StageProgress(key);
            if (e.Type == "retry") { stage.Retries++; continue; }
            if (e.Type != "call") continue;
            stage.Calls++;
            stage.InputTokens += e.InputTokens + e.CacheReadTokens + e.CacheWriteTokens;
            stage.CacheReadTokens += e.CacheReadTokens;
            stage.CacheWriteTokens += e.CacheWriteTokens;
            stage.OutputTokens += e.OutputTokens;
            stage.Chapter = e.Chapter ?? stage.Chapter;
            stage.ChunkIndex = e.ChunkIndex ?? stage.ChunkIndex;
            if (e.ChunkIndex is { } chunk) stage.Units.Add(chunk);
            else if (e.Chapter is { } chapter) stage.Units.Add(chapter);
            stage.LastLatencyMs = e.LatencyMs ?? stage.LastLatencyMs;
            stage.FirstCall ??= e.Timestamp;
            stage.LastCall = e.Timestamp is { } t && e.LatencyMs is { } ms ? t.AddMilliseconds(ms) : e.Timestamp ?? stage.LastCall;
        }
        var warnings = ReadNew(Path.Combine(RunDir, RunFolder.FailuresJsonl), ref _failuresOffset);
        foreach (var w in warnings)
        {
            var key = (w.Stage ?? "", w.Type);
            Warnings[key] = Warnings.GetValueOrDefault(key) + 1;
        }
        return (calls, warnings);
    }

    private static List<RunEvent> ReadNew(string path, ref long offset)
    {
        var events = new List<RunEvent>();
        if (!File.Exists(path)) return events;
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < offset) offset = 0;   // the file was replaced
            if (stream.Length == offset) return events;
            stream.Seek(offset, SeekOrigin.Begin);
            bytes = new byte[stream.Length - offset];
            var read = 0;
            while (read < bytes.Length && stream.Read(bytes, read, bytes.Length - read) is var n and > 0) read += n;
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        catch (IOException) { return events; }

        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (i > start && Parse(bytes.AsSpan(start, i - start)) is { } e) events.Add(e);
            start = i + 1;
        }
        offset += start;   // a last line without its end is read next time
        return events;
    }

    /// <summary>The top-level fields progress needs; <c>request</c> and <c>response</c> are skipped without being decoded.</summary>
    public static RunEvent? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? type = null, stage = null, reason = null;
            int? chapter = null, chunk = null, attempt = null;
            long input = 0, output = 0, requestBytes = 0, responseBytes = 0, cacheRead = 0, cacheWrite = 0;
            double? latency = null, delay = null;
            var failed = false;
            DateTimeOffset? timestamp = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    // TrySkip does nothing on a plain value and steps over an object or a list.
                    case "type": type = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; reader.TrySkip(); break;
                    case "stage": stage = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; reader.TrySkip(); break;
                    case "reason": reason = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; reader.TrySkip(); break;
                    case "chapter": chapter = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var c) ? c : null; reader.TrySkip(); break;
                    case "chunkIndex": chunk = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var k) ? k : null; reader.TrySkip(); break;
                    case "attempt": attempt = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var a) ? a : null; reader.TrySkip(); break;
                    case "latencyMs": latency = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : null; reader.TrySkip(); break;
                    case "delayMs": delay = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : null; reader.TrySkip(); break;
                    case "timestamp": timestamp = reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var t) ? t : null; reader.TrySkip(); break;
                    case "error": failed = reader.TokenType is not JsonTokenType.Null; reader.TrySkip(); break;
                    // The request and the reply are measured, not read: --debug shows their sizes, never their content.
                    case "request":
                    case "response":
                        var from = reader.TokenStartIndex;
                        reader.TrySkip();
                        if (name == "request") requestBytes = reader.BytesConsumed - from;
                        else responseBytes = reader.TokenType == JsonTokenType.Null ? 0 : reader.BytesConsumed - from;
                        break;
                    case "usage" when reader.TokenType == JsonTokenType.StartObject:
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var field = reader.GetString();
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var tokens))
                            {
                                if (field == "input") input = tokens;
                                else if (field == "output") output = tokens;
                                else if (field == "cache_read_input_tokens") cacheRead = tokens;
                                else if (field == "cache_creation_input_tokens") cacheWrite = tokens;
                            }
                            else reader.TrySkip();
                        }
                        break;
                    default: reader.TrySkip(); break;
                }
            }
            return type is null ? null : new RunEvent(type, stage, chapter, chunk, input, output, latency, failed, timestamp, attempt, reason, delay, requestBytes, responseBytes, cacheRead, cacheWrite);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>"310k" for 310,412; "1.2M"; "950".</summary>
    public static string Short(long n) => n switch
    {
        >= 10_000_000 => (n / 1_000_000d).ToString("0", CultureInfo.InvariantCulture) + "M",
        >= 1_000_000 => (n / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (n / 1_000d).ToString("0", CultureInfo.InvariantCulture) + "k",
        >= 1_000 => (n / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"1 h 12 min", "4 min 10 s", "38 s".</summary>
    public static string Span(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(0, (int)t.TotalSeconds)} s";
}
