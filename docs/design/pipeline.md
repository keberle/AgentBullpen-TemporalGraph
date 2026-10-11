# The indexing pipeline

_Design & research_

The index reads the book once: one extraction call per chapter-sized chunk, then resolution, linking, dating and seeding by code and by one call per chapter.

![The indexing pipeline](../diagrams/pipeline.svg)

## The stages

| Stage | What it does | Model calls |
|---|---|---|
| 0 | Chunks the text: a chapter is one chunk unless it exceeds `Indexing:Chunk:Tokens`, then it is cut at paragraph starts into the fewest equal parts, no overlap | none |
| 1 | Extracts names, events, facts with their discovery route, perceptions, retractions and identity reveals | one per chunk, plus `--glean` |
| 2 | Resolves mentions to entities in blocks on the BookNLP coreference backbone; the model decides only the residue, no transitive merging | three answers per block, kept at two votes |
| 3 | Links facts and edges between the resolved entities | none |
| 4 | Picks the anchor date and dates events relative to it, given only the undated rows of the two chapters before | one per chapter |
| 5 | Seeds perceptions from discovery routes, then asks which seeded perceptions change and why; code closes the old ones | one per chapter |

Entities come before facts so that every edge joins resolved entities.

## One read per chapter

Extraction degrades past about 64k tokens, so whole-book passes are out. Chapter-sized chunks raised entity precision from 0.74 to 0.89 and cut recall from 0.67 to 0.49, losing the names a chapter uses once. `--glean`, a second "what did you miss" call on the cached chunk, brought entity F1 back to within 0.001 of the previous pipeline's floor. The system block and chunk text are cached at the provider, so a glean call re-reads the chapter at a tenth of the price. Every item carries an evidence quote that code checks ([Entities and grounding](grounding.md)); one whose quote scores just under the threshold is not dropped outright, a judge call decides it.

## Where voting survives

The earlier pipeline sampled every stage five times and kept items by vote. Measured, that raised precision, cost recall and did not cut variance: runs disagree on which facts they find, not on correctness. Voting survives only in Stage 2's block resolution (`Indexing:StageB:Samples` / `MinVotes`); the paired keep rule survives as the way a skill change is judged ([Indexing a book](../guide/indexing.md)).
