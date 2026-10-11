# Runs BookNLP (https://github.com/booknlp/booknlp, MIT) once on a book's Stage 0 body text, as an offline
# cross-check. Not part of the app or the build.
#
#   pwsh tools/crosscheck/run-booknlp.ps1 [-Model big|small] [-Book hound-of-the-baskervilles] [-Stage0Run <runId>]
#
# Installs into a venv OUTSIDE the repo (default %LOCALAPPDATA%\TemporalGraph\booknlp-venv). Downloads:
#   - Python packages from PyPI (https://pypi.org/simple);
#   - BookNLP's three model files from people.ischool.berkeley.edu (BookNLP's own source);
#   - BERT base models and the spaCy model from huggingface.co (which redirects files to its CDN, us.aws.cdn.hf.co).
# These hosts were allowed for this one install. Output: data/crosscheck/booknlp/<book>/.
#
# Versions are pinned. BookNLP 1.0.8's README targets Python 3.7; this runs it on Python 3.14 with:
#   - booknlp installed with --no-deps (its declared tensorflow dependency is never imported and has no cp314 wheel);
#   - transformers 4.57.6 (last 4.x; BookNLP calls BERT with return_dict=False and tuple unpacking);
#   - setuptools 80.10.2 (still provides pkg_resources, which BookNLP imports);
#   - spaCy 3.8.16 with en_core_web_sm 3.7.1, the only version on Hugging Face (spaCy warns about the minor mismatch);
#   - Python's UTF-8 mode (-X utf8): BookNLP opens files without an encoding, which on Windows means cp1252;
#   - the model files rewritten without the obsolete 'embeddings.position_ids' key and loaded in BookNLP's
#     "custom" mode with forward-slash paths (see booknlp_run.py for why).
param(
    [ValidateSet("big", "small")][string]$Model = "big",
    [string]$Book = "hound-of-the-baskervilles",
    [string]$Stage0Run = "",
    [string]$Venv = "$env:LOCALAPPDATA\TemporalGraph\booknlp-venv",
    [string]$ModelsDir = "$env:LOCALAPPDATA\TemporalGraph\booknlp-models",
    [string]$PythonVersion = "3.14",
    [switch]$ReuseOutput   # re-derive the committed files from an existing BookNLP output (no re-run)
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

# 1. Environment (PyPI only), outside the repo.
if (-not (Test-Path "$Venv\Scripts\python.exe")) {
    Write-Host "Creating venv $Venv (Python $PythonVersion)"
    & py "-$PythonVersion" -m venv $Venv
    if ($LASTEXITCODE -ne 0) { throw "venv creation failed" }
}
$py = "$Venv\Scripts\python.exe"
$pip = @("-m", "pip", "install", "--disable-pip-version-check", "--index-url", "https://pypi.org/simple")
& $py @pip "setuptools==80.10.2" "torch==2.14.1" "transformers==4.57.6" "spacy==3.8.16" "tqdm==4.67.1"
if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
& $py @pip --no-deps "booknlp==1.0.8"
if ($LASTEXITCODE -ne 0) { throw "pip install booknlp failed" }
# Hugging Face names the wheel en_core_web_sm-any-py3-none-any.whl, which pip rejects (no version), so it's saved
# under its real version (3.7.1, from the repo's meta.json) first.
New-Item -ItemType Directory -Force $ModelsDir | Out-Null
$spacyWheel = Join-Path $ModelsDir "en_core_web_sm-3.7.1-py3-none-any.whl"
if (-not (Test-Path $spacyWheel)) {
    Invoke-WebRequest -Uri "https://huggingface.co/spacy/en_core_web_sm/resolve/main/en_core_web_sm-any-py3-none-any.whl" -OutFile $spacyWheel
}
& $py -m pip install --disable-pip-version-check --no-deps $spacyWheel
if ($LASTEXITCODE -ne 0) { throw "spaCy model install failed" }

# 2. The Stage 0 output to take the body range from (latest run that has this book, unless given).
if (-not $Stage0Run) {
    $Stage0Run = Get-ChildItem (Join-Path $root "runs") -Directory |
        Where-Object { (Test-Path (Join-Path $_.FullName "stage-0.json")) -and ((Get-Content (Join-Path $_.FullName "stage-0.json") -Raw) -match "`"id`": `"$Book`"") } |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
    if (-not $Stage0Run) { throw "No runs/<id>/stage-0.json for $Book; run 'index prepare --book $Book' first." }
}
$stage0 = Join-Path $root "runs\$Stage0Run\stage-0.json"
Write-Host "Stage 0 output: $stage0"

# 3. Run.
& $py -I -X utf8 (Join-Path $PSScriptRoot "booknlp_run.py") --root $root --book $Book --stage0 $stage0 --model $Model --models-dir $ModelsDir `
    --out (Join-Path $root "data\crosscheck\booknlp\$Book") $(if ($ReuseOutput) { "--reuse-output" })
if ($LASTEXITCODE -ne 0) { throw "BookNLP run failed" }
