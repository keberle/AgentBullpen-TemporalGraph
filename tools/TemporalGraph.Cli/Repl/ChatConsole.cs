using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using TemporalGraph.Chat;
using TemporalGraph.Cli.Books;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;
using TemporalGraph.Llm;
using TemporalGraph.Query;

namespace TemporalGraph.Cli.Repl;

/// <summary>
/// An open chat for the console: a book's graph, one model client and one run folder, in which the character, the chapter
/// and the spoiler mode can change. It builds the same <see cref="ChatSetup"/> and <see cref="ChatConversation"/> as <c>chat --message</c>
/// and the API. A change of chapter or mode makes a new pack and new tools at the new position and carries the turns so far along; a
/// change of character starts a new conversation.
/// </summary>
public sealed class ChatSession : IDisposable
{
    private readonly IConfiguration _config;
    private readonly string _root;
    private readonly string _dbPath;
    private readonly IChatClient _client;
    private readonly ChatJsonl _log;
    private readonly IReadOnlyDictionary<string, string> _queries;
    private readonly IReadOnlyList<(string Name, long? EvidenceStart)> _locations;
    private readonly BookVoice? _voice;
    private readonly LlmSettings _llm;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private ChatConversation? _conversation;
    private ChatSetup? _setup;

    public BookEntry Book { get; }
    public BookGraph Graph { get; }
    public IReadOnlyList<ChatCharacter> Characters { get; }
    public RunFolder Run { get; }
    public Pricing Pricing { get; }

    /// <summary>The canonical name of the character being talked to.</summary>
    public string Character { get; private set; } = "";
    public string DisplayName => _setup?.DisplayName ?? Character;
    public int Chapter { get; private set; }
    public bool Spoilers { get; private set; }

    /// <summary>The turns of the conversation with the current character.</summary>
    public List<ChatTurn> Turns { get; } = [];

    /// <summary>Every turn of the session, for the totals.</summary>
    public List<ChatTurn> AllTurns { get; } = [];

    private ChatSession(IConfiguration config, string root, string dbPath, BookEntry book, BookGraph graph, IReadOnlyList<ChatCharacter> characters, RunFolder run,
        IChatClient client, LlmSettings llm, IReadOnlyDictionary<string, string> queries)
    {
        (_config, _root, _dbPath, Book, Graph, Characters, Run, _client, _llm, _queries) = (config, root, dbPath, book, graph, characters, run, client, llm, queries);
        _locations = ChatCharacters.LoadLocations(dbPath);
        _voice = BookVoice.LoadFolder(book, RepoPaths.Books(root));
        _log = new ChatJsonl(run);
        Pricing = Pricing.From(config);
    }

    /// <summary>Opens the chat of a book, or refuses with the exit code: 3 no model or the book is not indexed, 2 an unknown book.</summary>
    public static (ChatSession? Session, int Exit) Open(string root, IConfiguration config, string bookId, string? llmProfile, string? anthropicBaseUrl = null)
    {
        var llm = LlmSettings.From(config, llmProfile ?? ChatSettings.LlmProfile(config));
        if (llm.Validate() is { Count: > 0 } problems) return (null, LlmCheckCommand.NotConfigured(problems));
        var manifest = Path.Combine(RepoPaths.Books(root), BookManifest.FileName);
        var book = File.Exists(manifest) ? BookManifest.Read(manifest).FirstOrDefault(e => e.Id == bookId) : null;
        if (book is null) return (null, CliContext.Fail(Exit.BadInput, $"Unknown book '{bookId}' (not in data/books/books.json)."));
        var dbPath = Path.Combine(RepoPaths.Books(root), bookId + ".sqlite");
        if (!File.Exists(dbPath)) return (null, CliContext.Fail(Exit.NotReady, $"Book database not found: {dbPath}", "book-not-indexed"));
        var queriesDir = Path.Combine(root, "queries", bookId);
        if (ChatTools.MissingFiles(queriesDir) is { Count: > 0 } missing)
            return (null, CliContext.Fail(Exit.NotReady, $"queries/{bookId}/ is missing: {string.Join(", ", missing)}.", "no-queries"));
        var graph = ChatCharacters.WithRosterNames(BookGraph.Load(dbPath), book.Chat);
        var characters = ChatCharacters.Load(dbPath, graph);
        if (characters.Count == 0)
            return (null, CliContext.Fail(Exit.NotReady, $"The characters table of '{bookId}' is empty (no graph has been promoted for it yet): run `index run --book {bookId} --promote`.", "book-not-indexed"));

        var run = RunFolder.Create(RepoPaths.Runs(root));
        File.WriteAllText(run.File(ChatJsonl.FileName), "");
        var context = new LlmCallContext { Run = run, Stage = ChatCommand.Stage, BookId = bookId };
        var client = ChatClientFactory.Create(llm, maxRetries: 0, anthropicBaseUrl, new LlmPipelineOptions(false, context, Sampling: LlmSampling.From(config, llm)));
        return (new ChatSession(config, root, dbPath, book, graph, characters, run, client, llm, ChatTools.LoadQueries(queriesDir)), Exit.Ok);
    }

    /// <summary>The roster's characters as canonical names (those the graph has), then every other character.</summary>
    public IReadOnlyList<string> Roster =>
        (Book.Chat ?? []).Select(n => ChatCharacters.Resolve(Characters, n)).OfType<string>().Distinct().ToList();

    /// <summary>Talks to <paramref name="character"/> (a canonical name or an alias) at a chapter and mode. Null: done; else what is wrong.</summary>
    public string? Begin(string character, int chapter, bool spoilers)
    {
        if (!Graph.Chapters.ContainsKey(chapter)) return $"No chapter {chapter}: the book has chapters {Graph.Chapters.Keys.Min()} to {Graph.Chapters.Keys.Max()}.";
        var canonical = ChatCharacters.Resolve(Characters, character);
        if (canonical is null) return $"No character goes by '{character}'.";
        // Another character: a new conversation. The same one at another chapter or mode: the talk goes on.
        var history = canonical == Character ? Turns.Select(t => (t.Question, t.Answer)).ToList() : [];
        if (canonical != Character) Turns.Clear();
        (Character, Chapter, Spoilers) = (canonical, chapter, spoilers);
        long pos = Graph.Chapters[chapter].CharEnd;
        _setup = ChatSetup.Create(Book, Graph, Characters, canonical, _queries, _locations, chapter, spoilers, _config,
            onCall: call => _log.Tool(call, pos, spoilers), voice: _voice);
        if (_setup.PackText is { } pack) _log.Pack(_setup.PackSections, pack.Length, pos, spoilers);
        _conversation = _setup.Conversation(_client, _log, history);
        return null;
    }

    public async Task<ChatTurn> AskAsync(string question, CancellationToken ct)
    {
        if (_conversation is null) throw new InvalidOperationException("No character chosen.");
        var turn = await _conversation.AskAsync(AllTurns.Count + 1, question, ct);
        Turns.Add(turn);
        AllTurns.Add(turn);
        return turn;
    }

    public (long Calls, long InputTokens, long OutputTokens) Totals =>
        _client.GetService<CallLogChatClient>() is { } log ? (log.Calls, log.InputTokens, log.OutputTokens) : (0, 0, 0);

    /// <summary>The cost of a number of tokens at the configured price; null when none is configured.</summary>
    public decimal? Cost(long inputTokens, long outputTokens) =>
        Pricing.Known ? Math.Round((inputTokens * Pricing.InputPerMillionTokens!.Value + outputTokens * Pricing.OutputPerMillionTokens!.Value) / 1_000_000m, 4) : null;

    /// <summary>Writes run.json and cost.json next to the chat.jsonl the turns were logged to; returns the run folder.</summary>
    public string Save()
    {
        var (calls, input, output) = Totals;
        var git = GitInfo.Current(_root);
        Run.Write(RunFolder.RunJson, new
        {
            runId = Run.RunId,
            command = $"chat --book {Book.Id} (console)",
            startedAt = _started,
            finishedAt = DateTimeOffset.UtcNow,
            git = new { commit = git.Sha, dirty = git.Dirty },
            book = Book.Id,
            chapter = Chapter,
            character = Character,
            displayName = DisplayName,
            spoilers = Spoilers,
            settings = new { llm = new { profile = _llm.Profile, type = _llm.Type, model = _llm.Model, policyId = _llm.PolicyId } },
            model = AllTurns.Select(t => t.ModelId).LastOrDefault(m => m is not null),
            questions = AllTurns.Count,
            passed = true,
        });
        Run.Write(RunFolder.CostJson, Pricing.CostReport(calls, input, output));
        return Run.Path;
    }

    /// <summary>The person went back before asking anything: the run folder made for the session (an empty chat.jsonl) is removed.</summary>
    public void Abandon()
    {
        if (AllTurns.Count > 0) return;
        try { Directory.Delete(Run.Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// The chat REPL. A line is a question; a line starting with "." is a command. Each reply is shown as the web panel shows it:
/// the answer, who answered with the tokens and the cost, and what was looked up.
/// </summary>
public sealed class ChatConsole(ChatSession session, IAnsiConsole console, ILineReader lines)
{
    public const string Help = """
        Type a question. Commands:
          .chapter N         move to the end of chapter N (a new knowledge pack; the conversation continues)
          .spoilers on|off   let the tools see everything true in the story so far, or only what the reader has been shown.
                             Spoilers moves the clock, not who knows what: the character still answers only from what
                             it has seen, been told or worked out
          .character <name>  talk to someone else (a new conversation)
          .looked            the last reply's lookups: each tool call and its rows
          .cost              the session's tokens and cost
          .save              write the transcript's run folder (chat.jsonl, run.json, cost.json) and show where it is
          .help              this text
          .quit              leave
        """;

    /// <summary>
    /// Ctrl+C at the input line says "Cancelled." and leaves, as <c>.quit</c> does (the transcript is saved; exit 130).
    /// Ctrl+C while the model is answering cancels that call only: "(cancelled)", and the input line again.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        console.MarkupLine($"Talking to [bold]{Markup.Escape(session.DisplayName)}[/] at the end of chapter {session.Chapter}, spoilers {(session.Spoilers ? "on" : "off")}. [grey].help lists the commands; .quit (or Ctrl+C) leaves; Ctrl+C during an answer stops that answer.[/]");
        var exit = Exit.Ok;
        var cancelled = false;
        while (true)
        {
            string? line;
            try { line = lines.Read($"{session.DisplayName} · ch {session.Chapter} › ", ct); }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            if (line is null) break;
            var text = line.Trim();
            if (text.Length == 0) continue;
            if (text.StartsWith('.'))
            {
                if (!Command(text)) break;
                continue;
            }
            using var call = Interrupts.Call(ct);
            try { Render(await session.AskAsync(text, call.Token)); }
            catch (OperationCanceledException) when (call.Interrupted && !ct.IsCancellationRequested)
            {
                // One Ctrl+C stops the answer, not the console: the question can be asked again.
                console.MarkupLine("[grey](cancelled)[/]");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The call failed; the conversation is still open, so the question can be asked again.
                console.MarkupLine($"[red]The model call failed:[/] {Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}");
                exit = Exit.Failed;
            }
        }
        var path = session.Save();
        console.MarkupLine($"Transcript: {Markup.Escape(Path.Combine(path, ChatJsonl.FileName))}");
        if (cancelled) return CliContext.Cancelled();
        return session.AllTurns.Count > 0 ? Exit.Ok : exit;
    }

    private void Render(ChatTurn turn)
    {
        console.Write(new Rule($"[grey]turn {turn.Turn}[/]").LeftJustified());
        console.Write(new Panel(new Text(turn.Answer)).Header(Markup.Escape(session.DisplayName)).Border(BoxBorder.Rounded));
        var cost = session.Cost(turn.InputTokens, turn.OutputTokens);
        console.MarkupLine($"[grey]Answered by {Markup.Escape(turn.ModelId ?? "(model not reported)")} · {turn.InputTokens:N0} tokens in, {turn.OutputTokens:N0} out · {(cost is { } c ? $"{c:0.0000} {Markup.Escape(session.Pricing.Currency ?? "")}".TrimEnd() : "cost not known")}[/]");
        console.MarkupLine("[grey]What I looked up:[/]");
        if (turn.ToolCalls.Count == 0) console.MarkupLine("[grey]  (nothing)[/]");
        foreach (var call in turn.ToolCalls) console.MarkupLine($"[grey]  {Markup.Escape(ChatCommand.Describe(call))}[/]");
    }

    /// <summary>One dot-command. False: leave.</summary>
    public bool Command(string line)
    {
        var space = line.IndexOf(' ');
        var (name, arg) = space < 0 ? (line, "") : (line[..space], line[(space + 1)..].Trim());
        switch (name)
        {
            case ".quit" or ".exit" or ".q":
                return false;
            case ".help":
                console.WriteLine(Help);
                break;
            case ".chapter":
                if (!int.TryParse(arg, out var n)) console.MarkupLine("[red].chapter N[/]");
                else Change(session.Character, n, session.Spoilers, $"Now at the end of chapter {n}; the conversation continues.");
                break;
            case ".spoilers":
                if (arg is not ("on" or "off")) console.MarkupLine("[red].spoilers on | off[/]");
                else Change(session.Character, session.Chapter, arg == "on", $"Spoilers {arg}; the conversation continues.");
                break;
            case ".character":
                if (arg.Length == 0) console.MarkupLine($"[red].character <name>[/] — the roster: {Markup.Escape(string.Join(", ", session.Roster))}");
                else Change(arg, session.Chapter, session.Spoilers, null);
                break;
            case ".looked":
                Looked();
                break;
            case ".cost":
                var (calls, input, output) = session.Totals;
                var cost = session.Cost(input, output);
                console.MarkupLine($"{session.AllTurns.Count} turn(s), {calls} model call(s): {input:N0} tokens in, {output:N0} out · {(cost is { } c ? $"{c:0.0000} {Markup.Escape(session.Pricing.Currency ?? "")}".TrimEnd() : "cost not known (no price configured)")}");
                break;
            case ".save":
                console.MarkupLine($"Saved {Markup.Escape(session.Save())} (chat.jsonl is the transcript).");
                break;
            default:
                console.MarkupLine($"[red]Unknown command {Markup.Escape(name)}.[/] .help lists them.");
                break;
        }
        return true;
    }

    private void Change(string character, int chapter, bool spoilers, string? said)
    {
        var before = session.Character;
        if (session.Begin(character, chapter, spoilers) is { } problem)
        {
            console.MarkupLine($"[red]{Markup.Escape(problem)}[/]");
            return;
        }
        console.MarkupLine(said ?? (session.Character == before ? $"Still talking to {Markup.Escape(session.DisplayName)}." : $"Now talking to [bold]{Markup.Escape(session.DisplayName)}[/]: a new conversation."));
    }

    private void Looked()
    {
        if (session.AllTurns.LastOrDefault() is not { } turn)
        {
            console.MarkupLine("Nothing has been asked yet.");
            return;
        }
        var tree = new Tree(Markup.Escape($"Turn {turn.Turn}: {turn.Question}"));
        if (turn.ToolCalls.Count == 0) tree.AddNode("(no tool calls)");
        foreach (var call in turn.ToolCalls)
        {
            var node = tree.AddNode(Markup.Escape($"{call.Tool}({string.Join(", ", call.Args.Select(a => $"{a.Key}: \"{a.Value}\""))})"));
            node.AddNode($"{call.Rows} row(s) in {call.ElapsedMs:0.0} ms");
            if (call.ResolvedName is { } resolved) node.AddNode(Markup.Escape($"resolved as \"{resolved}\""));
            if (call.UnknownName) node.AddNode("unknown name");
            if (call.Truncated) node.AddNode("truncated to the row limit");
            if (call.LateRowsSuppressed > 0) node.AddNode($"{call.LateRowsSuppressed} row(s) hidden by the spoiler rule");
            if (call.NodeIdList.Count > 0) node.AddNode($"{call.NodeIdList.Count} node(s), {call.EdgeIdList.Count} edge(s) of the graph");
        }
        console.Write(tree);
    }

    /// <summary>
    /// The opening: the book when none was given, the character from the roster (or any name), the chapter with the chapter table
    /// shown, and the spoiler mode; then the console. Only ever called at a terminal (the interactive path of <c>chat</c> and of the menu).
    /// </summary>
    public static async Task<int> OpenAsync(string root, IConfiguration config, string? bookId, int? chapter, string? character, bool? spoilers, string? llmProfile, CancellationToken ct,
        IAnsiConsole? on = null, Func<CancellationToken, Task<string?>>? addBook = null, ILineReader? lines = null, string? anthropicBaseUrl = null)
    {
        var console = on ?? AnsiConsole.Console;
        var askedBook = bookId is null;
        while (true)
        {
            if (askedBook)
            {
                // The manifest's indexed books, "+ Add a book…" first, "← Back" last.
                bookId = await BookPicker.PickAsync(console, root, b => b.IndexedRun is not null, b => $"{b.Id} — {b.Title}", addBook ?? BookPicker.AddAtConsole, ct);
                if (bookId is null) return Exit.Ok;
            }
            var (session, exit) = ChatSession.Open(root, config, bookId!, llmProfile, anthropicBaseUrl);
            if (session is null) return exit;
            using (session)
            {
                // Each question can be left: "← Back" (or Esc) on the list, an empty name, chapter 0. Back from the first one goes to
                // the books when the book was asked for, and out of the command when it was given.
                var (askCharacter, askChapter) = (character is null, chapter is null);
                var last = session.Graph.Chapters.Keys.Max();
                var back = false;
                while (!back && (character is null || chapter is null))
                {
                    if (character is null)
                    {
                        const string other = "Someone else…";
                        var choice = await console.AskAsync(new SelectionPrompt<string>().Title($"Who do you want to talk to? {Prompts.EscBack}").PageSize(12).MoreChoicesText("[grey](more below)[/]")
                            .UseConverter(Markup.Escape).AddChoices(session.Roster.Append(other)).AddChoices(Prompts.Back).AddCancelResult(Prompts.Back), ct);
                        if (choice == Prompts.Back) back = true;
                        else if (choice != other) character = choice;
                        else
                        {
                            var name = await console.AskAsync(new TextPrompt<string>("A character's name, any name the book uses (empty: back):").AllowEmpty()
                                .Validate(n => n.Trim().Length == 0 || ChatCharacters.Resolve(session.Characters, n) is not null ? ValidationResult.Success() : ValidationResult.Error("No character goes by that name.")), ct);
                            if (name.Trim().Length > 0) character = name;
                        }
                        continue;
                    }
                    var table = new Table().Border(TableBorder.Rounded).AddColumn(new TableColumn("Ch.").RightAligned()).AddColumn("Title");
                    using (var db = BookDatabase.OpenReadOnlyFile(BookDatabase.Readable(Path.Combine(RepoPaths.Books(root), bookId + ".sqlite"))))
                        foreach (var c in db.Chapters.OrderBy(c => c.Number).ToList()) table.AddRow(c.Number.ToString(), Markup.Escape(c.Title ?? ""));
                    console.Write(table);
                    var asked = await console.AskAsync(new TextPrompt<int>("The reader has read up to the end of chapter (Enter: the last; 0: back):").DefaultValue(last)
                        .Validate(n => n == 0 || session.Graph.Chapters.ContainsKey(n) ? ValidationResult.Success() : ValidationResult.Error($"1 to {last}, or 0 to go back.")), ct);
                    if (asked != 0) chapter = asked;
                    else if (askCharacter) character = null;
                    else back = true;
                }
                if (back)
                {
                    session.Abandon();
                    if (!askedBook) return Exit.Ok;
                    (character, chapter) = (askCharacter ? null : character, askChapter ? null : chapter);
                    continue;   // back to the books
                }
                spoilers ??= await console.ConfirmAsync("Spoilers on? (the tools then see everything true in the story so far, not only what the reader has been shown)", false, ct);
                if (session.Begin(character!, chapter!.Value, spoilers.Value) is { } problem) return CliContext.Fail(Exit.BadInput, problem);
                return await new ChatConsole(session, console, lines ?? new LineEditor()).RunAsync(ct);
            }
        }
    }
}
