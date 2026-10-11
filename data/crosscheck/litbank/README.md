# LitBank entity annotations — *The Hound of the Baskervilles*

These three files are copied unchanged from **LitBank** (https://github.com/dbamman/litbank), commit `master` as of 2026-10-06:

| File | Source |
|---|---|
| `2852_the_hound_of_the_baskervilles_brat.ann` | `entities/brat/2852_the_hound_of_the_baskervilles_brat.ann` |
| `2852_the_hound_of_the_baskervilles_brat.txt` | `entities/brat/2852_the_hound_of_the_baskervilles_brat.txt` |
| `2852_the_hound_of_the_baskervilles_brat.tsv` | `entities/tsv/2852_the_hound_of_the_baskervilles_brat.tsv` |

Downloaded from `https://raw.githubusercontent.com/dbamman/litbank/master/entities/…` (HTTP 200, `text/plain; charset=utf-8`, no redirects).

**License:** LitBank is licensed under the [Creative Commons Attribution 4.0 International License](http://creativecommons.org/licenses/by/4.0/).

**Attribution:** David Bamman, Sejal Popat and Sheng Shen (2019), "An Annotated Dataset of Literary Entities," NAACL 2019. LitBank also includes the work of Matthew Sims, Jong Ho Park and David Bamman (2019), "Literary Event Detection," ACL 2019, and David Bamman, Olivia Lewke and Anya Mansoor (2020), "An Annotated Dataset of Coreference in English Literature," LREC.

**How it's used here:** as a cross-check (review prompts, not truth) for the entity extraction of chapter 1, via `index crosscheck` in `tools/TemporalGraph.Cli`. It isn't part of the app or the build.

**Alignment with our text (#2852):** LitBank's text is tokenized and covers the start of chapter 1 (2,075 tokens; our offsets `[1872, 12657)`). It is the **#3070** edition, not #2852. The CLI maps LitBank tokens onto #2852 in order, allowing two spelling differences:
- LitBank `--` = #2852 `—` (em dash);
- #2852 marks italics with `_…_`, which LitBank doesn't have.

One word doesn't match and stays unaligned: LitBank `gray`, #2852 `grey`. The other 2,074 tokens align.

## Coreference annotation (`coref/`)

Two more files, copied unchanged from LitBank, `master` as of 2026-10-10:

| File | Source |
|---|---|
| `coref/2852_the_hound_of_the_baskervilles_brat.ann` | `coref/brat/2852_the_hound_of_the_baskervilles_brat.ann` |
| `coref/2852_the_hound_of_the_baskervilles_brat.txt` | `coref/brat/2852_the_hound_of_the_baskervilles_brat.txt` (byte-identical to the entity annotation's text above) |

Downloaded from `https://raw.githubusercontent.com/dbamman/litbank/master/coref/…` (HTTP 200, `text/plain; charset=utf-8`, no redirects). Same license and attribution; the coreference layer is Bamman, Lewke and Mansoor (2020).

**Format:** every mention is listed twice in the `.ann` file, with the same span: once with its kind and category (`PROP_PER`, `NOM_PER`, `PRON_PER`, …) and once with its chain's label (`Mr__Sherlock_Holmes-0`, `Watson-1`, `our_visitor-2`, …). The CLI joins the two by span.

**How it's used here:** read-only, as a second check on Stage 2 for the start of chapter 1 (`index crosscheck`): over LitBank's person mentions that fall on one of Stage 2's chains, the share of mention pairs both sides answer alike ("the same person?"), and link precision and recall with LitBank's answer as the reference. It is not training data and not gold.