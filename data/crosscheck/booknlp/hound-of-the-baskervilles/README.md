# BookNLP output — *The Hound of the Baskervilles* (#2852)

A one-off cross-check run of **BookNLP** (https://github.com/booknlp/booknlp, MIT license; David Bamman). It isn't part of the app or the build. Its output is a review prompt, not truth. BookNLP's README gives its coreference F1 as 79.0 (big model).

| File | What |
|---|---|
| `hound-of-the-baskervilles.book` | BookNLP's character list, unchanged: `characters[]` with `id`, `count`, `g` (gender), `mentions.proper/common/pronoun` (`n` = name, `c` = count), `agent`, `patient`, `mod`, `poss`. |
| `named-mentions.tsv` | Derived from BookNLP's `.entities` + `.tokens`: every PER/LOC/GPE/FAC mention tagged PROP or NOM, with its coreference cluster and **offsets into `data/books/source/hound-of-the-baskervilles.txt`** (UTF-16 units, `[start, end)`). Used by `index crosscheck`. |
| `run-info.json` | Versions, model, input, model files and the hosts used, timing and counts, written by the run. |

## How it was run
```
pwsh tools/crosscheck/run-booknlp.ps1 -Model big
```
- **Model:** `big` (entities `L-6_H-768`, coref and speaker `L-12_H-768`). This machine has 63 GB RAM, 12 cores and no CUDA, and the CPU run took 467 s.
- **Pipeline:** `entity,quote,supersense,event,coref`.
- **Input:** the Stage 0 body of #2852, `[910, 362395)`, i.e. the text between the Gutenberg START and END lines, taken from `runs/<id>/stage-0.json` (the script records which). That includes the title page, dedication, contents and "THE END" line, and BookNLP saw 70,717 words.
- **Versions:** booknlp 1.0.8, torch 2.14.1 (CPU), transformers 4.57.6, tokenizers 0.22.2, spaCy 3.8.16 with en_core_web_sm 3.7.1, setuptools 80.10.2, numpy 2.5.3, Python 3.14.6 (x64 build running emulated on Windows 11 ARM64).
- **Hosts:** packages from PyPI (`pypi.org`, `files.pythonhosted.org`). BookNLP's model files from `people.ischool.berkeley.edu`, over https with no redirect. BERT bases (`google/bert_uncased_L-6_H-768_A-12`, `google/bert_uncased_L-12_H-768_A-12`) and the spaCy model from `huggingface.co`, which redirects files to `us.aws.cdn.hf.co`.

### Differences from BookNLP's README install (Python 3.7, conda), and why
- **`booknlp` installed with `--no-deps`:** its declared `tensorflow>=1.15` is never imported and has no Python 3.14 wheel.
- **transformers 4.57.6 (the last 4.x):** BookNLP calls BERT with `return_dict=False` and unpacks tuples.
- **setuptools 80.10.2:** it still provides `pkg_resources`, which BookNLP imports.
- **spaCy model:** `en_core_web_sm` 3.7.1 is the only version on Hugging Face. spaCy 3.8 models are published only as GitHub release assets, which weren't an allowed host. spaCy warns `W095` about the minor-version mismatch.
- **Python's UTF-8 mode (`-X utf8`):** BookNLP opens files without an encoding, which on Windows means cp1252 and fails on the curly quotes.
- **Model files rewritten:** each had `bert.embeddings.position_ids` removed, which current transformers no longer stores, so strict loading failed on that one key. They're loaded in BookNLP's `custom` mode with forward-slash paths, because in `big`/`small` mode BookNLP derives the BERT name with `split("/")`, which breaks on Windows paths.
- **Offsets mapped back to CRLF:** BookNLP reads its input in text mode (CRLF → LF), so its token offsets point into LF-normalized text. `named-mentions.tsv` maps them back to the source text. All 3,847 mentions check out (text equal, whitespace ignored, because BookNLP joins tokens with spaces).

## Results
- **501 characters**, 52 with a proper name. 3,847 PER/LOC/GPE/FAC PROP/NOM mentions.
- Largest clusters: Holmes 973 mentions; Sir Henry 426; Dr. Mortimer 371; Watson 245; Sir Charles 226.
- **Visible quirks:**
  - Chapter titles run into the next sentence ("Mr. Sherlock Holmes Mr. Sherlock Holmes", "Fixing the Nets Sir Henry").
  - Stapleton is split across three clusters (including "Miss Stapleton" and "Mrs. Stapleton").
  - One cluster mixes "Barrymore" and "Mrs. Barrymore".
