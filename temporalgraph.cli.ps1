# The Temporal Graph CLI. No arguments: the menu. With arguments: passed straight through, e.g.
#   .\temporalgraph.cli.ps1 books add 2852 --json
# Works from any directory: the repo root is this script's folder, and the CLI is told so (--repo).
dotnet run --project "$PSScriptRoot/tools/TemporalGraph.Cli" -- @args --repo "$PSScriptRoot"; exit $LASTEXITCODE
