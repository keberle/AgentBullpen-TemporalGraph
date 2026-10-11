# Configuration

_Guide_

Everything runs with no configuration. Only the chat and the indexing stages need a model; without one they say which keys are missing.

## Where settings live

Later sources win: `appsettings.json` (committed; defaults and `<placeholder>` templates only), a gitignored `appsettings.Development.json`, the user-secrets store `temporalgraph-cli` that the CLI and the API share, environment variables (`__` for `:`), then command-line options for one run. API keys go in user-secrets only. `setup` records what it wrote in `Setup:Written`, so `setup reset` removes exactly that.

## Model profiles

A profile is one model under `Llm:Profiles:<name>`: `Type` (`OpenAI`, which also covers OpenRouter and Azure, or `Anthropic`), `Endpoint`, `Model` and `ApiKey`. Optional: `TimeoutSeconds`, `DisplayName`, `Temperature`, `Seed`, `ReasoningEffort`, `MaxOutputTokens` and `PolicyId` (sent as `x-policy-id`, for a custom Azure content filter). The template has placeholders `gpt41mini`, `gpt54mini`, `gpt56luna` and `haiku55`. Why these models: [Models and gateway](../design/models.md).

## The settings

| Setting | Where | What |
|---|---|---|
| `Chat:Llm` | CLI, API | The chat's profile; empty = the unnamed `Llm:*` keys |
| `Chat:Models` | API | More profiles readers may pick; the panel gets a model picker |
| `Chat:KnowledgePack`, `RequireToolFirst`, `CharacterGate` | CLI, API | All `true`: the character's own rows first; a tool call forced each turn; only what the speaking character knows |
| `Chat:MaxRows` (80), `VoiceExamples` (4) | CLI, API | Rows per tool result; the character's example lines in the prompt |
| `Indexing:Llm` | CLI | The indexing stages' profile (`haiku55`); `--llm` overrides it for one run |
| `Indexing:Chunk:Tokens` (8000) | CLI | A chapter is one chunk unless it exceeds this |
| `Indexing:StageA:Glean`, `Judge`, `Thinking` | CLI | Stage 1: a second "what did you miss" call; a keep-or-drop call for borderline items; thinking effort (`medium`) |
| `Indexing:StageB:Samples` (3), `MinVotes` (2) | CLI | Stage 2's voting; only Stage 2 votes |
| `Indexing:Parallel` (8), `Cache` (`true`) | CLI | Calls in flight per stage; prompt caching on Anthropic profiles |
| `Query:MaxRows` (500), `TimeoutSeconds` (5) | API | Rows returned; evaluation timeout |
| `Auth:Gateway:SigningKey`, `Issuer`, `Audience`, `Gateway:LlmBaseUrl` | API | A signing key set = gateway mode (JWT required, `/health` open); all four empty = standalone. A key left as a `<placeholder>`, or an empty key beside one of the other three, stops the API at startup with the setting named, so it never runs without the authentication it was meant to have. `Issuer` defaults to `https://api.agentbullpen.ai`, `Audience` to `temporal`, `Gateway:LlmBaseUrl` to `https://api.agentbullpen.ai/llm` |
| `VITE_API_BASE_URL`, `VITE_SITE_URL` | web build | The site's API and pages, used only behind the gateway |

The book manifest, `data/books/books.json`, holds each book's `chat` roster, `hero`, `avatars` and `voices`; see [Indexing a book](indexing.md).
