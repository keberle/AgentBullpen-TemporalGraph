# Cross-check tools

_Guide_

Two outside sources question the extractor's output; neither is truth, part of the app or part of the build. `index crosscheck --run <runId>` writes `crosscheck.json` with, per source and only over the run's chapters: **not in ours** (a proper name they annotated that none of the run's mentions overlaps), **not in theirs** (a run entity with no overlapping mention of theirs) and **alias difference** (their span overlapping a run entity but not among its aliases). The lists feed the review checklist as prompts. Why: [Entities and grounding](../design/grounding.md).

## LitBank

`data/crosscheck/litbank/` holds hand-annotated literary entities (David Bamman et al., CC BY 4.0), three files for *Hound* copied unchanged with an attribution README; `.gitattributes` keeps them byte-for-byte because the `.ann` offsets point into the LF `.txt`.

LitBank's text is tokenized and is the #3070 edition; the repo's is #2852. The CLI aligns tokens in order with two substitutions (`--` ↔ `—`; the repo's `_…_` italics skipped). The slice is the start of chapter 1 only, so a run outside chapter 1 reports LitBank as "not compared". LitBank has no proper/common flag, so a capitalization heuristic picks proper names; its `coref/` annotations are not used.

## BookNLP

BookNLP (MIT) does entity tagging, coreference and quote attribution. `data/crosscheck/booknlp/hound-of-the-baskervilles/` holds its character list unchanged, `named-mentions.tsv` (every PER/LOC/GPE/FAC mention with its cluster and offsets into the repo's text, derived for `index crosscheck`), `run-info.json` and a README. Known quirks: chapter titles fused into the next sentence, Stapleton split across three clusters, Barrymore and Mrs. Barrymore mixed in one.

Reproduce it:

```
pwsh tools/crosscheck/run-booknlp.ps1 -Model big
```

The script builds a venv outside the repo and runs `booknlp_run.py`, which maps BookNLP's LF offsets back to the CRLF source. It reaches PyPI, BookNLP's model files and Hugging Face. Running a tool whose README targets Python 3.7 on a current Python takes `--no-deps`, a pinned transformers 4.x, setuptools for `pkg_resources`, `python -X utf8` for the curly quotes and one obsolete key removed from each model file; none of it changes BookNLP's code.

The committed run is for *Hound* only. Another book needs its own run of the script and, for the LitBank side, must be among LitBank's annotated works.
