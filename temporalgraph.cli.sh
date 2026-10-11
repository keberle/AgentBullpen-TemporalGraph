#!/usr/bin/env sh
# The Temporal Graph CLI. No arguments: the menu. With arguments: passed straight through, e.g.
#   ./temporalgraph.cli.sh books add 2852 --json
# Works from any directory: the repo root is this script's folder, and the CLI is told so (--repo).
root="$(cd "$(dirname "$0")" && pwd)"
exec dotnet run --project "$root/tools/TemporalGraph.Cli" -- "$@" --repo "$root"
