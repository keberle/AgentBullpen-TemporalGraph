# Project layout

_Guide_

One solution, `TemporalGraph.slnx`: an Aspire AppHost, an API, a Vite web app, an indexing CLI and the libraries they share. The book data, skills, gold and queries are committed beside the code.

```
AGENTS.md · CLAUDE.md          how a coding agent sets the repo up
setup.sh · setup.ps1           run the CLI's `setup` with the same arguments
src/
  TemporalGraph.AppHost/       Aspire AppHost: api, web (Vite), cli (explicit start)
  TemporalGraph.ServiceDefaults/ OpenTelemetry, health, service discovery
  TemporalGraph.Api/           ASP.NET Core + HotChocolate GraphQL; the chat mutation; embeds the book files
  TemporalGraph.Data/          EF Core model of a book database; EDTF parser; ontology constants
  TemporalGraph.Query/         openCypher subset: ANTLR parser (Generated/, committed), in-memory engine
  TemporalGraph.Llm/           model profiles, the call pipeline (sampling, retry, call log, telemetry), pricing
  TemporalGraph.Chat/          the typed tools, the prompt, the roster, the voice layer
tools/
  TemporalGraph.Cli/           Spectre.Console CLI: setup, books, index *, gold *, query, chat, chat vet
  crosscheck/                  the BookNLP script (offline, outside the build)
  grammar/                     regenerate the parser (needs Java + ANTLR)
tests/
  TemporalGraph.Testing/       FakeLlmServer, shared by the test projects
  TemporalGraph.Data.Tests/    schema, stages, gold, scoring, keep rule, promote, chat, setup
  TemporalGraph.Query.Tests/   parser, evaluator, the query files on a fixture
  TemporalGraph.Api.Tests/     over HTTP against the committed books
data/
  books/                       books.json (the manifest), <id>.sqlite, <id>/voice/, provenance/, source/
  crosscheck/                  LitBank and BookNLP files for the Hound
skills/                        one markdown skill file per LLM stage
gold/<id>/                     review checklists and the gold JSON made from them
queries/<id>/                  the chat's tool queries and samples/ (ten per book)
runs/                          run folders, gitignored
docs/                          guide/ design/ story/ (served at /docs) and diagrams/
web/
  src/                         App, BookView, ChatPanel, graph/ (2D, 3D, the gate), docs/, design-kit/
  public/books/                hero images and avatars
```

## Who depends on whom

The API references `Data`, `Query`, `Chat`, `Llm` and `ServiceDefaults`, and embeds the book files, the voice files and the query files at build time, so after `index promote` or `index voice` change them the API is rebuilt. The CLI references the same libraries and embeds the source texts. `Chat` uses `Query`, `Data`, `Llm` and the Agent Framework; `Llm` wraps the OpenAI and Anthropic SDKs behind one call pipeline. `Query` opens a book file read-only and knows nothing about time filtering; the queries do. Nothing references the site's repo; the design kit under `web/src/design-kit/` is a copy.

## Generated and committed

`src/TemporalGraph.Query/Generated/` holds the C# lexer, parser and visitor produced by ANTLR from the unmodified openCypher grammar, so a fresh clone builds without Java. `data/books/*.sqlite` are built by the CLI (`index prepare`, then `index promote`), never by hand; each holds a promoted full-book graph.
