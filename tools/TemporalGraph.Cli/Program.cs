// The CLI: books from Project Gutenberg, the indexing pipeline, query and chat consoles, setup. Also an explicit-start Aspire resource ("cli").
//   dotnet run --project tools/TemporalGraph.Cli                                      (the menu, at a terminal)
//   dotnet run --project tools/TemporalGraph.Cli -- books add <gutenberg-number | url | file.txt> [--index]
//   dotnet run --project tools/TemporalGraph.Cli -- index run --book <id> [--chapters 1-3] [--stages 0-5] [--from-run <runId>] [--promote]
//   dotnet run --project tools/TemporalGraph.Cli -- query --book <id>   |   chat --book <id>      (the consoles; see README.md)
//   dotnet run --project tools/TemporalGraph.Cli -- create <id> --title "<title>" --author "<author>" [--force]
//   dotnet run --project tools/TemporalGraph.Cli -- index prepare --book <id> | --all
//   dotnet run --project tools/TemporalGraph.Cli -- index mentions --book <id> --chapters 1-3 [--glean]
//   dotnet run --project tools/TemporalGraph.Cli -- index resolve --run <runId>   (then index crosscheck / gold review --run <runId>)
//   dotnet run --project tools/TemporalGraph.Cli -- index facts --run <runId>   (then index time / index build-db --run <runId>)
//   dotnet run --project tools/TemporalGraph.Cli -- index score --run <runId> --gold gold/<id>/ch01-03.json
//   dotnet run --project tools/TemporalGraph.Cli -- gold import --book <id> --file gold/<id>/ch01-03.review.md
//   dotnet run --project tools/TemporalGraph.Cli -- runs diff <runA> <runB>
//   dotnet run --project tools/TemporalGraph.Cli -- query --db runs/<runId>/book.sqlite --file queries/hound-of-the-baskervilles/who-met.cypher --chapter 3 --param character=Holmes
//   dotnet run --project tools/TemporalGraph.Cli -- chat --book hound-of-the-baskervilles --chapter 3 --character "Sherlock Holmes" [--spoilers] (--message "<text>" | --script scripts/hound-of-the-baskervilles/ch03-holmes.txt) [--json] [--capture-content]
//   dotnet run --project tools/TemporalGraph.Cli -- chat vet [--llm <profile>]
//   dotnet run --project tools/TemporalGraph.Cli -- llm-check [--capture-content]
//   dotnet run --project tools/TemporalGraph.Cli -- setup [status|add|check|reset] [--json]   (see AGENTS.md)
using Microsoft.Extensions.Logging;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Setup;
using TemporalGraph.Llm;
using TemporalGraph.Cli.Telemetry;

// OpenTelemetry: exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set (Aspire sets it); disposing flushes.
using var telemetry = CliTelemetry.Start(LlmSettings.LoadConfiguration());
CliTelemetry.Logger("TemporalGraph.Cli").LogInformation("Arguments: {Arguments}", string.Join(" ", args));

return await CliApp.RunAsync(args);
