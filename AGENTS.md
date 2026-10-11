# Notes for coding agents

For an agent (Claude Code, Codex, …) working in this repo on its user's behalf. Below, `setup …` stands for `./temporalgraph.cli.sh setup …` (Windows: `.\temporalgraph.cli.ps1 setup …`; `./setup.sh …` and `.\setup.ps1 …` are the same).

## Set up this repo for someone

Only the chat needs a model; everything else runs without configuration.

1. Run `setup status --json`.
   - **Exit 0:** the chat is ready; go to step 6.
   - **Exit 3:** the `missing` list says what to fix, prerequisites first; then continue with step 2.
2. Ask the user (unless they already said) which provider they want (`openai`, `anthropic`, `azure` or `openrouter`) and which model. For Azure, also ask for the endpoint (`https://<resource>.openai.azure.com/openai/v1/`); the model is the deployment's name.
3. Ask the user (unless they already have) to put their API key in an environment variable **themselves**, where your commands can see it. For example, they set it before starting you: `export OPENAI_API_KEY=…` or `$env:OPENAI_API_KEY = "…"`.
   - **Never ask them to paste the key to you.**
   - **Never put a key on a command line.**
   - Or the user can run `./setup.sh` (or `./setup.ps1`) themselves, which asks for the key with a hidden prompt.
4. Run `setup add <profile> --provider <provider> --model <id> [--endpoint <url>] --key-env <VAR> --default`.
   - **The key is copied** into the user-secrets store. The variable isn't needed afterwards; to change the key, set the variable again and rerun the same `setup add`.
   - **Profile name:** a new name is simplest. A name `status` lists with `writtenBySetup: false` can be reused, but setup refuses it if that profile is in user-secrets. If it only comes from the appsettings files, setup's values go on top, and the files are never changed.
   - **Other flags:** `--indexing` also makes it the indexing stages' model, and `--pickable` offers it in the chat's model picker.
5. Run `setup check --json`.
   - **Exit 0:** the model answered.
   - **Exit 1:** the provider's error is in `profiles[].error`; the key is never shown.
   - **`skipped`:** profiles that are incomplete (no key, for example) are listed there. They are not failures and don't affect the chat.
6. Run `aspire run`. The web app's URL is on the Aspire dashboard, under the `web` resource.

**To undo:** run `setup reset --yes`. It removes only what `setup` wrote and restores any setting it replaced.

**Exit codes for every `setup` command:** 0 ok, 1 a model call or the store failed, 2 bad input (or a prompt is needed and there is no terminal), 3 not ready, 130 cancelled. Every `setup` command takes `--json`. Without a terminal, `setup` never waits for input; it prints the command to run instead. The same holds for every other command (next section).

## Add, index and talk to a book without a person

Run every command through the launch script at the repo root: `./temporalgraph.cli.sh …` (Windows: `.\temporalgraph.cli.ps1 …`). It works from any directory, passes its arguments straight through and returns the CLI's exit code. This is the canonical sequence, from nothing to a book that answers:

1. `./temporalgraph.cli.sh books add 2852 --json` — a Project Gutenberg number, a gutenberg.org URL, a `.txt` URL or a local `.txt` file. `result.id` is the book id; `result.chapters` and `result.checks` say what Stage 0 found.
   - **Already there:** the same book with the same text is exit 0 with `result.unchanged: true`; nothing is written. `--dry-run` shows what would be fetched and the id, and fetches nothing.
   - **Exit 4:** the book was added and a Stage 0 check failed (usually the chapter count). Rerun with `--id <id> --force --expect-chapters <n>`, or edit `data/books/profiles/<id>.json`.
2. `./temporalgraph.cli.sh index run --book <id> --budget-tokens <n> --promote --json` — Stage 0 to Stage 5, the graph and the leak tests in one run folder, then the promotion. One line per finished stage goes to stderr.
   - **Exit 3:** `problems[0].type` says why: `model-not-configured` (run the setup steps above with `--indexing`), `stage0-check`, `stage-input-missing`, `over-budget` (the detail quotes the estimate), `price-unknown`. Nothing was run.
   - **Budgets:** `--budget-tokens <n>` refuses when the estimated tokens, in plus out, are more than n; it needs no price, so use it. `--budget-usd <n>` refuses when the estimated cost is higher, and also (`price-unknown`) when the profile has no price configured (`Llm:Profiles:<name>:Pricing`), which is the usual case today. Both may be given; either one refuses. To see the estimate without running anything, give `--budget-tokens 0`: the refusal's detail carries it.
   - **Exit 4:** a leak test failed; nothing was promoted.
   - **Exit 1:** a stage failed. Continue with `result.continueWith` (`index run --from-run <result.runId> --stages <n>-5 --promote`; the book comes from the run).
   - **Not promoted yet:** `./temporalgraph.cli.sh index promote --run <result.runId> --json` (the book is the run's).
   - **Exit 130:** the run was stopped (Ctrl+C, SIGTERM, or `index cancel`). The calls already sent finished, nothing was promoted, `result.cancelled` is true, `result.stoppedAtStage` says where and `result.continueWith` is the command that goes on from there.
   - **While it runs:** `result.log` is `runs/<runId>/run.log`. From another process, `./temporalgraph.cli.sh index status --run <runId> --json` gives `running`, `elapsedSeconds`, `callsSoFar`, `currentStage`, `currentChunk`; `index watch --run <runId>` waits for the run to end (exit 0). Without a terminal, stderr has a line per stage start and end and one a minute in between.
   - **`--dry-run`** runs the pre-flight and returns the estimate (`result.dryRun: true`); no run folder, no model call. The budgets still refuse (exit 3).
   - `--chapters 1-3` indexes part of a book; `./temporalgraph.cli.sh index cancel --run <runId>` stops a run from another process; `./temporalgraph.cli.sh index status --run <runId> --json` reports one.
   - **The stages:** `0 prepare`, `1 extract`, `2 resolve`, `3 link`, `4 time`, `5 perceive`, then `build-db`. The text is read once: Stage 1 makes one model call per chapter-sized chunk (no voting), Stage 2 one call per block of names (asked 3 times, 2 votes), Stage 3 is code only, Stages 4 and 5 make one call per chapter over the items, with no book text. Each stage has its own command for a run that exists: `index extract --book <id> --chapters A-B`, then `index resolve`, `index link`, `index time`, `index perceive` and `index build-db`, each with `--run <runId>`; `index --stages 2..5 --run <runId>` runs several in order.
   - **Options for one run** on `index run`, `index rebuild`, `index extract` and `index --stages`, each over the configuration key of the same name:

     | Option | Key | Default |
     |---|---|---|
     | `--chunk-tokens <N>` | `Indexing:Chunk:Tokens` (with `Indexing:Chunk:ChapterAligned`, true) | 8000: a chapter is one chunk, split at paragraph starts into equal parts above this size; `1000` gives the earlier 1,000-token windows |
     | `--glean` | `Indexing:StageA:Glean` | false: a second "what did you miss" call per chunk |
     | `--judge <BOOL>` | `Indexing:StageA:Judge` | true: one "keep or drop" call per chunk about items whose evidence is just under the threshold |
     | `--judge-band <LOW-HIGH>` | `Indexing:StageA:JudgeBand` | `0.80-0.90`: the evidence scores the judge is asked about |
     | `--max-groups-per-call <N>` | `Indexing:StageB:MaxGroupsPerCall` | 25: name groups in one resolution call |
     | `--samples <K>` | `Indexing:StageB:Samples` | 3: answers asked per block of name groups |
     | `--min-votes <M>` | `Indexing:StageB:MinVotes` | 2: answers that must put two groups in one set |
     | `--parallel <N>` | `Indexing:Parallel` | 8: model calls in flight per stage (429 and 529 are retried with backoff) |
     | `--cache <BOOL>` | `Indexing:Cache` | true: prompt caching on Anthropic profiles |

   - **Tokens:** `runs/<runId>/cost.json` counts cache reads and writes apart (`cacheReadTokens`, `cacheWriteTokens`, per stage and in the total); the estimate counts cache reads as input.
3. `./temporalgraph.cli.sh query --book <id> --chapter 3 --file queries/<id>/who-met.cypher --character "<name>" --json` — `./temporalgraph.cli.sh query samples --book <id> --json` lists the query files and their parameters; `./temporalgraph.cli.sh query explain --text "<cypher>" --json` parses one without running it.
4. `./temporalgraph.cli.sh chat --book <id> --chapter 3 --character "<name>" --message "<question>" --json`.

**The contract, for every command:**

- **Never prompts without a terminal.** A command that would ask a person exits 2 and names the command to run instead. `--json`, `--quiet` and `--no-input` never prompt either.
- **`--json`:** stdout is exactly one object, `{ "ok", "command", "exit", "result", "problems": [ { "type", "title", "detail" } ] }`; everything else is on stderr. A command that printed JSON before has the same object as `result`.
- **Exit codes:** 0 ok · 1 a model call, a download or a store failed · 2 bad input, or a prompt was needed and there is no terminal · 3 not ready (no model configured, the book is not indexed, a stage's input is missing) · 4 a check failed (a leak test, a Stage 0 check, `chat vet`) · 130 cancelled (Ctrl+C, SIGTERM, `index cancel`; the problem's `type` is `cancelled`).
- **Stopping:** send SIGINT or SIGTERM once and wait: the command stops cleanly and exits 130. A second SIGINT quits at once.
- **Global options:** `--json`, `--quiet`, `--no-color` (or `NO_COLOR`), `--no-input`, `--verbose`, `--debug`, `--repo <path>` (the launch script sets it).
- **The next step:** after a state change a line `Next: <command>` goes to stderr; not with `--json` or `--quiet`.
- **An unexpected error** is exit 1 with one line that names the log holding the stack trace (`runs/<runId>/run.log`, else `runs/cli.log`).

**Rebuild every book:** `./temporalgraph.cli.sh index rebuild --dry-run --json` (also `./temporalgraph.rebuild.sh --dry-run`) pre-flights every book in `data/books/books.json` and returns the plan: `result.books[]` with each book's estimate and `result.estimate` with the sum. It runs nothing. Then `./temporalgraph.cli.sh index rebuild --budget-tokens <n> --json` runs `index run --stages 0-5 --promote` for each book in order (`--book <id>`, repeatable, for a subset; the budgets are per book).

- **Exit 0:** every book was rebuilt (`result.books[].promoted` says which were promoted). With `--dry-run`: every book would start.
- **Exit 3:** a book would be refused (`over-budget`, `price-unknown`, `model-not-configured`, `stage0-check`); `problems[0].detail` starts with the book's id. Nothing was run, for any book.
- **Exit 4:** a leak test failed; that book was not promoted and the later books were not started.
- **Exit 130:** the rebuild was stopped; the book it was on says how to continue, and the later books were not started.
- **Exit 1:** a stage failed; the later books were not started. Continue that book with `index run --book <id> --from-run <runId> --stages <n>-5 --promote`, then rebuild the others with `--book`.
- **Exit 2:** an unknown book.

## Rules

- **Never print or commit a key.** Keys live only in the user-secrets store `temporalgraph-cli`. The repo is public.
- **Gitignored local files:** `appsettings.Development.json`, `runs/` and `.archive/` stay out of commits.
- **Build and test:** run `dotnet build`, `dotnet test`, and `npm ci && npm test` in `web/`. The tests make no network calls.
