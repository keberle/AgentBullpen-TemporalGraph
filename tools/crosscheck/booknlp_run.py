"""Run BookNLP once on a book's Stage 0 body text; write the cross-check files. Called by run-booknlp.ps1.

Why the model files are rewritten:
  BookNLP 1.0.8 loads its checkpoints with load_state_dict(strict=True). They were saved with an old transformers,
  whose BertEmbeddings stored a 'position_ids' buffer; transformers >= 4.31 no longer stores it, so strict loading
  fails on the unexpected key. The rewritten copies drop only keys ending in 'embeddings.position_ids'.
Why "custom" mode with forward slashes:
  BookNLP derives the BERT base name from the model file name with model_file.split("/")[-1]; with Windows
  backslash paths (its "big"/"small" modes use os.path.join) that yields the whole path. Custom mode takes our paths.

Offsets: BookNLP reads its input in text mode, so CRLF becomes LF and its token offsets point into that
LF-normalized text. They are mapped back to the source text (which keeps CRLF) before writing named-mentions.tsv.

Outputs (in --out): <book>.book (BookNLP's character list), named-mentions.tsv (PER/LOC/GPE/FAC mentions with
offsets into the book's source text), run-info.json (versions, model, hosts, timing, counts).

Stage 2's sidecar (the CLI's `index resolve` starts it): with --stage2-out <dir> instead of --out, and the body
range given as --body-start / --body-end / --sha256 instead of --stage0, the script writes into that folder
(runs/<runId>/stage2/booknlp/): mentions.tsv (EVERY mention of every category, pronouns included: the coreference
chains), <book>.book and run-info.json. Nothing under data/ is touched.
"""
import argparse, hashlib, json, os, platform, shutil, sys, time, urllib.parse, urllib.request
from pathlib import Path

MODEL_HOST = "people.ischool.berkeley.edu"
MODEL_BASE = f"https://{MODEL_HOST}/~dbamman/booknlp_models/"
MODELS = {
    "big": {
        "entity": "entities_google_bert_uncased_L-6_H-768_A-12-v1.0.model",
        "coref": "coref_google_bert_uncased_L-12_H-768_A-12-v1.0.model",
        "quote": "speaker_google_bert_uncased_L-12_H-768_A-12-v1.0.1.model",
    },
    "small": {
        "entity": "entities_google_bert_uncased_L-4_H-256_A-4-v1.0.model",
        "coref": "coref_google_bert_uncased_L-2_H-256_A-4-v1.0.model",
        "quote": "speaker_google_bert_uncased_L-8_H-256_A-4-v1.0.1.model",
    },
}


class SameHostRedirects(urllib.request.HTTPRedirectHandler):
    """Refuse any redirect that leaves the model host."""
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        host = urllib.parse.urlparse(newurl).hostname
        if host != MODEL_HOST:
            raise RuntimeError(f"redirect to {host} refused ({newurl})")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download(name, folder, log):
    path = folder / name
    if path.exists():
        log.append({"file": name, "url": MODEL_BASE + name, "cached": True, "bytes": path.stat().st_size})
        return path
    opener = urllib.request.build_opener(SameHostRedirects)
    print(f"downloading {MODEL_BASE + name}", flush=True)
    with opener.open(MODEL_BASE + name) as r, open(str(path) + ".part", "wb") as f:
        final = r.geturl()
        shutil.copyfileobj(r, f, 1 << 20)
    os.replace(str(path) + ".part", path)
    log.append({"file": name, "url": MODEL_BASE + name, "finalUrl": final, "bytes": path.stat().st_size})
    return path


def strip_position_ids(src, dst):
    import torch
    state = torch.load(src, map_location="cpu")
    dropped = [k for k in state if k.endswith("embeddings.position_ids")]
    for k in dropped:
        del state[k]
    torch.save(state, dst)
    return dropped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--book", required=True)
    ap.add_argument("--stage0")
    ap.add_argument("--body-start", type=int)
    ap.add_argument("--body-end", type=int)
    ap.add_argument("--sha256")
    ap.add_argument("--stage2-out", help="write the coreference chains for Stage 2 into this folder (instead of --out)")
    ap.add_argument("--model", choices=MODELS, default="big")
    ap.add_argument("--models-dir", required=True)
    ap.add_argument("--out")
    ap.add_argument("--reuse-output", action="store_true", help="skip BookNLP if its output already exists; only re-derive the files")
    a = ap.parse_args()
    os.environ.setdefault("HF_HUB_DISABLE_TELEMETRY", "1")

    if (a.out is None) == (a.stage2_out is None):
        ap.error("give exactly one of --out and --stage2-out")
    root, out = Path(a.root), Path(a.stage2_out or a.out)
    out.mkdir(parents=True, exist_ok=True)
    raw = (root / "data" / "books" / "source" / f"{a.book}.txt").read_bytes()
    text = raw.decode("utf-8")  # offsets: code points = UTF-16 units here (no characters outside the BMP)
    if a.stage0:
        stage0 = json.loads(Path(a.stage0).read_text(encoding="utf-8"))
        book = next(b for b in stage0["books"] if b["id"] == a.book)
    else:
        if a.body_start is None or a.body_end is None or not a.sha256:
            ap.error("without --stage0, give --body-start, --body-end and --sha256")
        book = {"textSha256": a.sha256, "body": {"charStart": a.body_start, "charEnd": a.body_end}}
    assert hashlib.sha256(raw).hexdigest() == book["textSha256"], "source text differs from the Stage 0 run"
    body_start, body_end = book["body"]["charStart"], book["body"]["charEnd"]
    body = text[body_start:body_end]

    work = Path(a.models_dir).parent / "booknlp-work" / a.book
    work.mkdir(parents=True, exist_ok=True)
    # An earlier output is reused only for the same input text.
    same_input = (work / "input.txt").exists() and (work / "input.txt").read_bytes() == body.encode("utf-8")
    (work / "input.txt").write_text(body, encoding="utf-8", newline="")

    # Models: original files from the BookNLP host, then rewritten copies (same names: BookNLP derives the BERT name from them).
    originals = Path(a.models_dir) / "original"
    fixed = Path(a.models_dir) / "fixed"
    originals.mkdir(parents=True, exist_ok=True)
    fixed.mkdir(parents=True, exist_ok=True)
    downloads, dropped = [], {}
    paths = {}
    for role, name in MODELS[a.model].items():
        src = download(name, originals, downloads)
        dst = fixed / name
        if not dst.exists():
            dropped[name] = strip_position_ids(src, dst)
        else:  # already rewritten: record what the rewrite removed
            import torch
            dropped[name] = sorted(set(torch.load(src, map_location="cpu")) - set(torch.load(dst, map_location="cpu")))
        paths[role] = dst.as_posix()

    from booknlp.booknlp import BookNLP
    import importlib.metadata as md
    params = {
        "pipeline": "entity,quote,supersense,event,coref",
        "model": "custom",
        "entity_model_path": paths["entity"],
        "coref_model_path": paths["coref"],
        "quote_attribution_model_path": paths["quote"],
    }
    started = time.time()
    reused = False
    previous = json.loads((out / "run-info.json").read_text(encoding="utf-8")) if (out / "run-info.json").exists() else {}
    if a.reuse_output and same_input and (work / "output" / f"{a.book}.book").exists():
        print("reusing existing BookNLP output", flush=True)
        seconds = previous.get("seconds")
        reused = True
    else:
        nlp = BookNLP("en", params)
        nlp.process(str(work / "input.txt"), str(work / "output"), a.book)
        seconds = round(time.time() - started, 1)

    # LF-normalized offset -> offset in the body as written (CRLF kept).
    to_original = []
    for i, ch in enumerate(body):
        if ch == "\r" and i + 1 < len(body) and body[i + 1] == "\n":
            continue
        to_original.append(i)
    to_original.append(len(body))

    # The character list, committed as-is.
    shutil.copyfile(work / "output" / f"{a.book}.book", out / f"{a.book}.book")
    characters = json.loads((out / f"{a.book}.book").read_text(encoding="utf-8"))["characters"]

    # Named and common PER/LOC/GPE/FAC mentions with offsets into the book text (token onsets are body offsets).
    tokens = {}
    with open(work / "output" / f"{a.book}.tokens", encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        idx = {h: i for i, h in enumerate(header)}
        for line in f:
            c = line.rstrip("\n").split("\t")
            tokens[int(c[idx["token_ID_within_document"]])] = (int(c[idx["byte_onset"]]), int(c[idx["byte_offset"]]))
    rows, chains, checked, exact = [], [], 0, 0
    with open(work / "output" / f"{a.book}.entities", encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        idx = {h: i for i, h in enumerate(header)}
        for line in f:
            c = line.rstrip("\n").split("\t")
            cat, prop = c[idx["cat"]], c[idx["prop"]]
            s = to_original[tokens[int(c[idx["start_token"]])][0]] + body_start
            e = to_original[tokens[int(c[idx["end_token"]])][1] - 1] + 1 + body_start
            chains.append((c[idx["COREF"]], cat, prop, s, e, c[idx["text"]]))
            if cat not in ("PER", "LOC", "GPE", "FAC") or prop not in ("PROP", "NOM"):
                continue
            checked += 1
            # BookNLP's text column joins tokens with spaces ("West - Country"), so compare without whitespace.
            exact += "".join(text[s:e].split()) == "".join(c[idx["text"]].split())
            rows.append((c[idx["COREF"]], cat, prop, s, e, c[idx["text"]]))
    # Stage 2 gets every mention (the chains); the cross-check keeps its named and common PER/LOC/GPE/FAC mentions.
    with open(out / ("mentions.tsv" if a.stage2_out else "named-mentions.tsv"), "w", encoding="utf-8", newline="\n") as f:
        f.write("coref\tcat\tprop\tchar_start\tchar_end\ttext\n")
        for r in (chains if a.stage2_out else rows):
            f.write("\t".join(str(x) for x in r) + "\n")

    info = {
        "book": a.book,
        "model": a.model,
        "pipeline": params["pipeline"],
        "seconds": seconds,
        "input": {"stage0": Path(a.stage0).resolve().relative_to(root.resolve()).as_posix() if a.stage0 else None,
                  "bodyCharStart": body_start, "bodyCharEnd": body_end, "textSha256": book["textSha256"]},
        "reusedOutput": reused,
        "versions": {p: md.version(p) for p in ("booknlp", "torch", "transformers", "tokenizers", "spacy", "en_core_web_sm", "setuptools", "numpy")}
                    | {"python": platform.python_version(), "platform": platform.platform(), "machine": platform.machine()},
        "modelFiles": downloads,
        "droppedKeys": dropped,
        "hosts": {"models": MODEL_HOST, "bertBases": "huggingface.co (files redirect to us.aws.cdn.hf.co)", "packages": "pypi.org / files.pythonhosted.org"},
        "counts": {"characters": len(characters), "mentions": len(chains if a.stage2_out else rows), "chains": len({r[0] for r in chains}),
                   "mentionOffsetsChecked": checked, "mentionOffsetsExact": exact},
    }
    (out / "run-info.json").write_text(json.dumps(info, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(info["counts"]), f"{seconds}s", flush=True)


if __name__ == "__main__":
    main()
