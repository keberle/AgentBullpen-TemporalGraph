# AgentBullpen Temporal Graph — docs

A temporal knowledge graph of a novel, and a character to talk to who knows only what the reader knows. Three public-domain books are in; any Project Gutenberg book can be added with one command.

## Guide

| Page | What |
|---|---|
| [Getting started](guide/getting-started.md) | Prerequisites, a model, `aspire run`, the three tabs |
| [Configuration](guide/configuration.md) | Settings, profiles, every key |
| [Indexing a book](guide/indexing.md) | Commands, the run folder, gold, promotion |
| [Database schema](guide/schema.md) | The per-book SQLite file, two clocks |
| [Project layout](guide/project-layout.md) | Folders, projects, dependencies |
| [Cross-check tools](guide/tools.md) | LitBank and BookNLP |

## Design & research

| Page | Choice |
|---|---|
| [The graph model](design/graph-model.md) | Nodes, edge kinds, perception and discovery |
| [Two clocks](design/two-clocks.md) | Text position versus story time |
| [The indexing pipeline](design/pipeline.md) | The stages, one read per chapter |
| [Entities and grounding](design/grounding.md) | Evidence quotes and the check |
| [Character chat](design/chat.md) | Typed tools, knowledge pack |
| [Query engine](design/query-engine.md) | An own openCypher subset, in memory |
| [The graph view](design/graph-view.md) | Shapes, gating, 2D and 3D |
| [Models and gateway](design/models.md) | Which model does what; the adapters |

## The story

| Page | |
|---|---|
| [The question](story/the-question.md) | What the project set out to test |
| [What's next](story/whats-next.md) | What is planned, in order |

## Diagram sheets

`docs/diagrams/*.svg` — thirteen drawing sheets in the site's architecture style (dark, grayscale-safe).
