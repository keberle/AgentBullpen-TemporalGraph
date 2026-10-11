using Spectre.Console;
using Spectre.Console.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Setup;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Telemetry;

namespace TemporalGraph.Cli;

/// <summary>The CLI's commands (moved out of Program.cs so the tests can run the parser) and its exit code.</summary>
public static class CliApp
{
    /// <summary>
    /// Runs the CLI. With no arguments, the home menu at a terminal, else the help and exit 2. With <c>--json</c>, stdout is one
    /// envelope ({ ok, command, exit, result, problems }) and everything else goes to stderr; with <c>--quiet</c>, only the problems of
    /// a failed command are printed. A parser error (an unknown command, a missing value) exits 2 for every command, with an example
    /// and where the help is. Every command runs under one token that Ctrl+C, SIGTERM or a closed terminal cancels
    /// (<see cref="Interrupts"/>): a cancelled command says "Cancelled." and exits 130. An exception a command did not handle exits 1
    /// with one line that names the log holding its stack trace.
    /// </summary>
    /// <param name="outer">Tests: a token that cancels the command as Ctrl+C would.</param>
    public static async Task<int> RunAsync(string[] args, CancellationToken outer = default)
    {
        args = HelpCommand(args);
        var json = args.Contains("--json");
        var quiet = !json && args.Contains("--quiet");
        CliContext.Reset(json, quiet, args.Contains("--no-input"), args.Contains("--verbose"), args.Contains("--debug"));
        RepoPaths.Override = null;
        RunLog.Current = null;

        // NO_COLOR (any value) and --no-color both turn colour off, for this command.
        var colours = AnsiConsole.Profile.Capabilities.ColorSystem;
        if (args.Contains("--no-color") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        using var listening = Interrupts.Listen(cts);
        using var capture = json || quiet ? new OutputCapture(json) : null;
        // After the capture: the app writes its help and its version to the console that is current now.
        var app = new CommandApp<HomeCommand>();
        app.Configure(Configure);
        int code;
        try
        {
            if (NoCommand(args) && !CliContext.Interactive)
            {
                await app.RunAsync(["--help"]);
                code = CliContext.NoInput
                    ? CliContext.Fail(Exit.BadInput, $"No command given, and --no-input was given: the menu asks. `{Setup.SetupRunner.Cli} --help` lists the commands.", "no-input")
                    : CliContext.Fail(Exit.BadInput, $"No command given, and there is no terminal for the menu. `{Setup.SetupRunner.Cli} --help` lists the commands.", "no-terminal");
            }
            else code = ExitCode(await app.RunAsync(args, cts.Token), args);
            // A command that came back cancelled without saying so (Spectre turns a cancelled token into 130 itself).
            if (code == Exit.Cancelled && CliContext.Problems.All(p => p.Type != "cancelled")) code = CliContext.Cancelled();
            else if (code == Exit.BadInput) Usage(args);
        }
        catch (Exception ex) when (ex is CommandAppException or DirectoryNotFoundException)
        {
            code = CliContext.Fail(Exit.BadInput, ex.Message);
            Usage(args);
        }
        catch (OperationCanceledException)
        {
            code = CliContext.Cancelled();
        }
        catch (Exception ex)
        {
            code = Unexpected(ex, args);
        }
        finally
        {
            AnsiConsole.Profile.Capabilities.ColorSystem = colours;
            RepoPaths.Override = null;   // --repo is for this command
        }

        if (capture is not null)
        {
            var (captured, said) = (capture.Captured, capture.Said);
            capture.Dispose();
            if (json) Console.Out.WriteLine(OutputCapture.Envelope(CommandName(args), code, captured, said, CliContext.Result, CliContext.Problems));
            else if (code != Exit.Ok)
                foreach (var p in CliContext.Problems.Count > 0 ? CliContext.Problems.Select(x => x.Detail) : [said.Trim()]) Console.Error.WriteLine(p);
        }
        return code;
    }

    /// <summary><c>help [command]</c> is <c>[command] --help</c>.</summary>
    public static string[] HelpCommand(string[] args) =>
        args.Length > 0 && args[0] == "help" ? [.. args.Skip(1).Where(a => a is not ("--help" or "-h")), "--help"] : args;

    /// <summary>
    /// After bad input (exit 2), not the whole help: one example of the command and where its help is. A refusal that
    /// already names the command to run instead (no terminal, <c>--no-input</c>) has said enough.
    /// </summary>
    private static void Usage(IReadOnlyList<string> args)
    {
        if (CliContext.Problems.Any(p => p.Type is "no-terminal" or "no-input") || args.Contains("--help") || args.Contains("-h")) return;
        var name = CommandName(args);
        if (name == "home" || !Examples.TryGetValue(name, out var examples))
        {
            AnsiConsole.MarkupLine($"See: [bold]{Markup.Escape(CliName.Wrapper)} --help[/]");
            return;
        }
        AnsiConsole.MarkupLine(Markup.Escape($"Usage: {CliName.Wrapper} {name} [OPTIONS]"));
        AnsiConsole.MarkupLine(Markup.Escape($"Example: {CliName.Wrapper} {string.Join(" ", examples[0].Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}"));
        AnsiConsole.MarkupLine($"See: [bold]{Markup.Escape($"{CliName.Wrapper} {name} --help")}[/]");
    }

    /// <summary>
    /// The repository's issues page, for the line an unexpected error prints: the first link to a GitHub repository in README.md,
    /// else in NOTICE.md, at the repo root, with <c>/issues</c>. Null when neither names the repository: the line then leaves it out.
    /// </summary>
    public static string? IssuesUrl(string root)
    {
        foreach (var file in new[] { "README.md", "NOTICE.md" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            var link = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(path), @"https://github\.com/[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+?(?=[/#?)\s""'>]|\.git|$)");
            if (link.Success) return link.Value + "/issues";
        }
        return null;
    }

    /// <summary>
    /// An exception no command handled. One line on the terminal (the message, the log that has the stack trace, where to
    /// report it), the stack trace in that log (the run's run.log when a run was going, else runs/cli.log), exit 1. The stack trace
    /// is printed too with <c>--debug</c> only.
    /// </summary>
    internal static int Unexpected(Exception ex, IReadOnlyList<string> args)
    {
        string where;
        string? report = null;
        try
        {
            var root = RepoPaths.FindRoot();
            where = Path.GetRelativePath(root, RunLog.Crash(root, ex, CommandName(args))).Replace('\\', '/');
            report = IssuesUrl(root);
        }
        catch (Exception log) when (log is IOException or UnauthorizedAccessException or ArgumentException) { where = "(the log could not be written)"; }
        var message = ex.Message.TrimEnd().TrimEnd('.');
        var code = CliContext.Fail(Exit.Failed, $"Unexpected error: {ex.GetType().Name}: {message}. Details: {where}." + (report is null ? "" : $" Report: {report}"), "unexpected-error");
        if (CliContext.Debug) AnsiConsole.WriteLine(ex.ToString());
        return code;
    }

    /// <summary>What <c>--version</c> prints: the assembly's informational version (the version set in Directory.Build.props;
    /// the SDK adds the commit it was built from). Git is not run.</summary>
    public static string Version() =>
        typeof(CliApp).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    /// <summary>
    /// One or two examples per command, the commonest first. Each command's help shows its own, and bad input shows the first.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[][]> Examples = new Dictionary<string, string[][]>
    {
        ["books add"] = [["books", "add", "2852"], ["books", "add", "2852", "--dry-run"]],
        ["books list"] = [["books", "list"]],
        ["books remove"] = [["books", "remove", "treasure-island", "--yes"]],
        ["books refetch"] = [["books", "refetch", "treasure-island"]],
        ["create"] = [["create", "my-book", "--title", "My Book", "--author", "A. Writer"]],
        ["index"] = [["index", "--stages", "2..5", "--run", "<runId>"]],
        ["index run"] = [["index", "run", "--book", "hound-of-the-baskervilles", "--budget-tokens", "25000000", "--promote"], ["index", "run", "--from-run", "<runId>", "--stages", "3-5"]],
        ["index rebuild"] = [["index", "rebuild", "--dry-run"], ["index", "rebuild", "--book", "treasure-island", "--budget-tokens", "25000000"]],
        ["index status"] = [["index", "status", "--run", "<runId>"], ["index", "status", "--book", "hound-of-the-baskervilles"]],
        ["index watch"] = [["index", "watch", "--run", "<runId>"]],
        ["index cancel"] = [["index", "cancel", "--run", "<runId>"]],
        ["index prepare"] = [["index", "prepare", "--book", "hound-of-the-baskervilles"], ["index", "prepare", "--all"]],
        ["index extract"] = [["index", "extract", "--book", "hound-of-the-baskervilles", "--chapters", "1-3"]],
        ["index resolve"] = [["index", "resolve", "--run", "<runId>"]],
        ["index link"] = [["index", "link", "--run", "<runId>"]],
        ["index dedupe"] = [["index", "dedupe", "--run", "<runId>"]],
        ["index time"] = [["index", "time", "--run", "<runId>"]],
        ["index perceive"] = [["index", "perceive", "--run", "<runId>"]],
        ["index build-db"] = [["index", "build-db", "--run", "<runId>"]],
        ["index layout"] = [["index", "layout", "--book", "hound-of-the-baskervilles"]],
        ["index promote"] = [["index", "promote", "--run", "<runId>", "--book", "hound-of-the-baskervilles"]],
        ["index score"] = [["index", "score", "--run", "<runId>", "--gold", "gold/hound-of-the-baskervilles/ch01-03.json"]],
        ["index keep"] = [["index", "keep", "--baseline", "<runA>", "--candidate", "<runB>"]],
        ["index voice"] = [["index", "voice", "--book", "hound-of-the-baskervilles"]],
        ["index crosscheck"] = [["index", "crosscheck", "--run", "<runId>"]],
        ["gold review"] = [["gold", "review", "--run", "<runId>"]],
        ["gold facts-review"] = [["gold", "facts-review", "--run", "<runId>"]],
        ["gold convert"] = [["gold", "convert", "--book", "hound-of-the-baskervilles", "--file", "gold/hound-of-the-baskervilles/ch01-03.facts.json"]],
        ["gold perceptions-review"] = [["gold", "perceptions-review", "--run", "<runId>"]],
        ["gold import"] = [["gold", "import", "--book", "hound-of-the-baskervilles", "--file", "gold/hound-of-the-baskervilles/ch01-03.review.md"]],
        ["runs diff"] = [["runs", "diff", "<runA>", "<runB>"]],
        ["query"] = [["query", "--book", "hound-of-the-baskervilles", "--chapter", "3", "--file", "queries/hound-of-the-baskervilles/who-met.cypher", "--character", "Dr. Watson"], ["query", "--book", "hound-of-the-baskervilles"]],
        ["query samples"] = [["query", "samples", "--book", "hound-of-the-baskervilles"]],
        ["query explain"] = [["query", "explain", "--text", "MATCH (c:Character) RETURN c.name"]],
        ["chat"] = [["chat", "--book", "hound-of-the-baskervilles", "--chapter", "3", "--character", "Sherlock Holmes", "--message", "Who have you met?"], ["chat", "--book", "hound-of-the-baskervilles"]],
        ["chat vet"] = [["chat", "vet"]],
        ["llm-check"] = [["llm-check"]],
        ["setup"] = [["setup"]],
        ["setup status"] = [["setup", "status", "--json"]],
        ["setup add"] = [["setup", "add", "openai", "--provider", "openai", "--model", "gpt-4o-mini", "--key-env", "OPENAI_API_KEY", "--default"]],
        ["setup check"] = [["setup", "check"]],
        ["setup reset"] = [["setup", "reset", "--yes"]],
    };

    private static void Example(ICommandConfigurator command, string name)
    {
        foreach (var example in Examples[name]) command.WithExample(example);
    }

    /// <summary>No command word and no request for the help or the version (the launch script adds <c>--repo &lt;root&gt;</c> to every
    /// call, so "no arguments" is "no command"): the home menu at a terminal, the help and exit 2 without one.</summary>
    public static bool NoCommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--repo") { i++; continue; }
            if (!args[i].StartsWith('-') || args[i] is "-h" or "--help" or "-v" or "--version") return false;
        }
        return true;
    }

    /// <summary>Spectre's −1 (a parser error) is 2, bad input, for every command.</summary>
    public static int ExitCode(int code, IReadOnlyList<string> args) => code == -1 ? Exit.BadInput : code;

    private static readonly HashSet<string> Branches = ["index", "gold", "runs", "query", "chat", "setup", "books"];

    /// <summary>The command's name for the envelope: "books add", "index run", "query", "home" with no arguments.</summary>
    public static string CommandName(IReadOnlyList<string> args)
    {
        var words = args.TakeWhile(a => !a.StartsWith('-')).ToList();
        if (words.Count == 0) return args.Count > 0 && Branches.Contains(FirstWord(args)) ? FirstWord(args) : "home";
        return words.Count > 1 && Branches.Contains(words[0]) && Names.Contains(words[1]) ? $"{words[0]} {words[1]}" : words[0];
    }

    private static string FirstWord(IReadOnlyList<string> args) => args.FirstOrDefault(a => !a.StartsWith('-')) ?? "";

    private static readonly HashSet<string> Names =
    [
        "prepare", "extract", "resolve", "link", "dedupe", "time", "perceive", "build-db", "promote", "score", "keep", "voice",
        "crosscheck", "run", "rebuild", "status", "watch", "cancel", "review", "facts-review", "convert", "perceptions-review", "import", "diff", "samples", "explain",
        "vet", "add", "check", "reset", "list", "remove", "refetch",
    ];
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName(CliName.Wrapper);
        // Spectre.Console.Cli keeps the first console it wrote to for the life of the process; naming it per app keeps the help and
        // the version on the console of this command (with --json that is stderr).
        config.Settings.Console = AnsiConsole.Console;
        config.SetApplicationVersion(Version());
        config.SetHelpProvider(new CommandFirstHelp(config.Settings));
        // The app's own help: the few commands a person starts with.
        foreach (var name in new[] { "books add", "index run", "query", "chat", "setup" }) config.AddExample(Examples[name][0]);
        // The global options, applied once; exceptions reach RunAsync, which turns them into exit codes and problems.
        config.SetInterceptor(new GlobalInterceptor());
        config.PropagateExceptions();
        // A book from Project Gutenberg into the repo.
        config.AddBranch("books", books =>
        {
            books.SetDescription("The books: add one from Project Gutenberg (a number, a URL or a .txt file), list them, remove one, fetch one again.");
            Example(books.AddCommand<Books.BooksAddCommand>("add")
                .WithDescription("Download a book's text to data/books/source/<id>.txt, add it to data/books/books.json, detect its chapters and run Stage 0. --index: then index it. Exit 4: added, and a Stage 0 check failed."), "books add");
            Example(books.AddCommand<Books.BooksListCommand>("list")
                .WithDescription("Every book: chapters, whether it is indexed, its newest run and that run's cost."), "books list");
            Example(books.AddCommand<Books.BooksRemoveCommand>("remove")
                .WithDescription("Remove a book's manifest entry, source text, database, profile override and provenance file (runs/ are kept). --yes to skip the question."), "books remove");
            Example(books.AddCommand<Books.BooksRefetchCommand>("refetch")
                .WithDescription("Download a book's text again and report whether its SHA-256 still matches the committed text. Writes nothing."), "books refetch");
        });
        Example(config.AddCommand<CreateCommand>("create")
                .WithDescription("Create data/books/<id>.sqlite with the book schema and add it to data/books/books.json."), "create");
        config.AddBranch("index", index =>
        {
            index.SetDescription("Indexing pipeline.");
            // "index --stages 2..5 (--run <id> | --stage1-from <id>)" runs the listed stages in order (the branch's default command).
            index.SetDefaultCommand<IndexStagesCommand>();
            index.AddExample(Examples["index"][0]);
            // The whole pipeline in one command, its status and its stop.
            Example(index.AddCommand<IndexRunCommand>("run")
                .WithDescription("Stages 0 to 5 and build-db in one run folder (the same stage code as the stage commands), with a cost estimate first. --from-run continues a failed run; --promote promotes after the leak tests pass."), "index run");
            Example(index.AddCommand<IndexRebuildCommand>("rebuild")
                .WithDescription("re-index books through the whole pipeline and promote each (index run --stages 0-5 --promote per book, each in its own run folder). No --book: every book. --dry-run prints the plan and the summed estimate and runs nothing. The first book that fails stops it."), "index rebuild");
            Example(index.AddCommand<IndexWatchCommand>("watch")
                .WithDescription("Follow a run from another terminal: its stages, the bar of the stage it is in and the time left, read from the run folder once a second. Ends when the run does; Ctrl+C leaves the watch, not the run."), "index watch");
            Example(index.AddCommand<IndexStatusCommand>("status")
                .WithDescription("The books; with --book, a book's runs; with --run, a run's stages, tokens, cost, leak tests and files."), "index status");
            Example(index.AddCommand<IndexCancelCommand>("cancel")
                .WithDescription("Ask a running `index run` to stop from another terminal (it checks once a second, lets the calls already sent finish, and exits 130 with the command that continues it)."), "index cancel");
            Example(index.AddCommand<IndexPrepareCommand>("prepare")
                .WithDescription("Stage 0: chapters, paragraphs and chunk plan from the embedded source text; Layer 1 checks; run folder; book database."), "index prepare");
            Example(index.AddCommand<IndexExtractCommand>("extract")
                .WithDescription("Stage 1: one model call per chunk for everything it tells: names, events, facts with how they were discovered, perceptions, retractions, identity reveals (skills/extract-graph.md), one sample, evidence checked by code. A chunk is a chapter."), "index extract");
            Example(index.AddCommand<IndexResolveCommand>("resolve")
                .WithDescription("Stage 2: resolve a run's names into entities on a BookNLP coreference backbone; the model is asked a block of name groups at a time, 3 samples and 2 votes (skills/resolve-blocks.md)."), "index resolve");
            Example(index.AddCommand<IndexLinkCommand>("link")
                .WithDescription("Stage 3 (code only): point the names of Stage 1's items at Stage 2's entities, run the checks and the Fact de-duplication, and write stage-3.json."), "index link");
            Example(index.AddCommand<IndexDedupeCommand>("dedupe")
                .WithDescription("Re-apply the Stage 3 run-wide de-duplication to a run's Stage 3 output, into a new run (no LLM calls)."), "index dedupe");
            Example(index.AddCommand<IndexTimeCommand>("time")
                .WithDescription("Stage 4: one made-up anchor date, then story time with one call per chapter over its Events and Facts, no text (skills/assign-story-time.md); code resolves relative dates and fills the rest."), "index time");
            Example(index.AddCommand<IndexPerceiveCommand>("perceive")
                .WithDescription("Stage 5: PERCEIVES edges, seeded by code from the DISCOVERED edges, revised by one call per chapter over the seeded edges and the events (skills/revise-perceptions.md), closed by code."), "index perceive");
            Example(index.AddCommand<IndexBuildDbCommand>("build-db")
                .WithDescription("Write a run's graph, both clocks on every node and edge, and the timeline into runs/<runId>/book.sqlite (scratch; not committed), then run the leak tests."), "index build-db");
            Example(index.AddCommand<IndexLayoutCommand>("layout")
                .WithDescription("Compute the graph's layout once (web/scripts/layout.mjs, Node.js; no model) and store it in the book database, so the browser runs no force simulation. build-db does it too."), "index layout");
            Example(index.AddCommand<PromoteCommand>("promote")
                .WithDescription("Copy a run's graph (runs/<runId>/book.sqlite) into the committed data/books/<id>.sqlite and write data/books/provenance/<id>.json."), "index promote");
            Example(index.AddCommand<ScoreCommand>("score")
                .WithDescription("Score a run's entities against a gold JSON: P/R/F1, merge, alias and type errors; writes scores.json and failures.md."), "index score");
            Example(index.AddCommand<KeepCommand>("keep")
                .WithDescription("Apply the improvement loop's keep rule (paired, 2x SE, held-out guard) to scored baseline and candidate runs."), "index keep");
            Example(index.AddCommand<IndexVoiceCommand>("voice")
                .WithDescription("The roster characters' own lines from the text (speech tags, then the model with checked evidence), selected for manner; writes data/books/<id>/voice/lines.json and lines.review.md (--apply-review: the marked lines)."), "index voice");
            Example(index.AddCommand<IndexCrosscheckCommand>("crosscheck")
                .WithDescription("Compare a run's entities with LitBank (and BookNLP, when present): review prompts, not truth."), "index crosscheck");
        });
        config.AddBranch("gold", gold =>
        {
            gold.SetDescription("Gold sets: the reviewed answer keys.");
            Example(gold.AddCommand<GoldReviewCommand>("review")
                .WithDescription("Write the review checklist (gold/<book>/chNN-NN.review.md) from a run."), "gold review");
            Example(gold.AddCommand<GoldFactsReviewCommand>("facts-review")
                .WithDescription("Write the draft facts review (gold/<book>/chNN-NN.facts.review.md) from the union of runs' Stage 3/4 output."), "gold facts-review");
            Example(gold.AddCommand<GoldConvertCommand>("convert")
                .WithDescription("Convert the edge-shaped facts gold (chNN-NN.facts.json) into the shape of the full schema (chNN-NN.graph.json), keeping every mark."), "gold convert");
            Example(gold.AddCommand<GoldPerceptionsReviewCommand>("perceptions-review")
                .WithDescription("Write a run's PERCEIVES edges as a checklist to mark later (gold/<book>/chNN-NN.perceptions.review.md); candidates, not gold."), "gold perceptions-review");
            Example(gold.AddCommand<GoldImportCommand>("import")
                .WithDescription("Parse a reviewed checklist into gold JSON; refuses lines it can't parse."), "gold import");
        });
        config.AddBranch("runs", runs =>
        {
            runs.SetDescription("Run folders under runs/.");
            Example(runs.AddCommand<RunsDiffCommand>("diff")
                .WithDescription("Differences between two runs: checks, counts, chapter boundaries."), "runs diff");
        });
        // "query" is a branch; "query --book …" still runs a query (the default command), and without --text or --file at a
        // terminal it opens the query console.
        config.AddBranch("query", query =>
        {
            query.SetDescription("Run an openCypher query (supported subset) on a book database, read-only: --book <id> | --db <path>, --text | --file. Without --text or --file at a terminal: the query console.");
            query.SetDefaultCommand<QueryCommand>();
            foreach (var example in Examples["query"]) query.AddExample(example);
            Example(query.AddCommand<Repl.QuerySamplesCommand>("samples")
                .WithDescription("List a book's query files (queries/<id>/ and its samples/) with their question and parameters."), "query samples");
            Example(query.AddCommand<Repl.QueryExplainCommand>("explain")
                .WithDescription("Parse a query without running it: the parameters it needs, its clauses and its tree."), "query explain");
        });
        // "chat" is a branch so "chat vet" can sit under it; "chat --book …" still runs the chat (the branch's default command).
        config.AddBranch("chat", chat =>
        {
            chat.SetDescription("Chat with a character who knows only what the graph holds at the end of a chapter (the committed queries as tools; reveal-time gate unless --spoilers).");
            chat.SetDefaultCommand<ChatCommand>();
            foreach (var example in Examples["chat"]) chat.AddExample(example);
            Example(chat.AddCommand<ChatVetCommand>("vet")
                .WithDescription("Run the fixed checks a model must pass before it goes on the site's allowlist: spoiler gate, tool use, voice."), "chat vet");
        });
        Example(config.AddCommand<LlmCheckCommand>("llm-check")
                .WithDescription("Send one short prompt to the configured LLM through an agent and print the reply, model and token counts."), "llm-check");
        // Configure (and undo) a local machine. Exit codes: 0 ok / chat ready, 1 a call or the store failed, 2 bad input, 3 not ready.
        config.AddBranch("setup", setup =>
        {
            setup.SetDescription("Configure a model for the chat on this machine (interactive), or undo it. Keys only from a hidden prompt, --key-env or --key-stdin; never printed.");
            setup.SetDefaultCommand<SetupCommand>();
            setup.AddExample(Examples["setup"][0]);
            Example(setup.AddCommand<SetupStatusCommand>("status")
                .WithDescription("What is configured: prerequisites, profiles (key set/missing, never the value), chat, picker, indexing, gateway. Exit 0 = chat ready, 3 = not ready."), "setup status");
            Example(setup.AddCommand<SetupAddCommand>("add")
                .WithDescription("Add one model profile without prompts: <profile> --provider --model [[--endpoint]] --key-env <VAR> | --key-stdin [[--default]] [[--pickable]] [[--indexing]]."), "setup add");
            Example(setup.AddCommand<SetupCheckCommand>("check")
                .WithDescription("Send one short prompt through each profile (or --profile); --vet also runs chat vet. Exit 1 if a call fails, 3 if a profile is incomplete."), "setup check");
            Example(setup.AddCommand<SetupResetCommand>("reset")
                .WithDescription("Remove only what setup wrote (and restore what it replaced); --profile for one, --yes to skip the question."), "setup reset");
        });
    }
}
