# Regenerates the openCypher lexer/parser/visitor in src/TemporalGraph.Query/Generated from grammar/Cypher.g4.
# Needs Java (any JRE 11+) and the ANTLR 4.13.1 tool jar, which the build does not: the generated C# is committed.
#   pwsh tools/grammar/generate.ps1 -Jar <path to antlr-4.13.1-complete.jar> [-Java <path to java(.exe)>]
# Jar: https://www.antlr.org/download/antlr-4.13.1-complete.jar or Maven Central org.antlr:antlr4:4.13.1 (complete).
param(
    [Parameter(Mandatory = $true)][string]$Jar,
    [string]$Java = "java"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$project = Join-Path $root "src/TemporalGraph.Query"
$out = Join-Path $project "Generated"
if (Test-Path $out) { Get-ChildItem $out -File | Remove-Item }
Push-Location (Join-Path $project "grammar")
try {
    & $Java -jar $Jar -Dlanguage=CSharp -visitor -no-listener -package TemporalGraph.Query.Parsing -o $out Cypher.g4
    if ($LASTEXITCODE -ne 0) { throw "ANTLR failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }
# Keep only the C# sources (ANTLR also writes .interp/.tokens files used by its own tooling).
Get-ChildItem $out -File | Where-Object { $_.Extension -ne ".cs" } | Remove-Item
Get-ChildItem $out -File | ForEach-Object { $_.Name }
