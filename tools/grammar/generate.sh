#!/usr/bin/env sh
# Regenerates the openCypher lexer/parser/visitor in src/TemporalGraph.Query/Generated from grammar/Cypher.g4.
# Needs Java (any JRE 11+) and the ANTLR 4.13.1 tool jar, which the build does not: the generated C# is committed.
#   sh tools/grammar/generate.sh <path to antlr-4.13.1-complete.jar> [java]
# Jar: https://www.antlr.org/download/antlr-4.13.1-complete.jar or Maven Central org.antlr:antlr4:4.13.1 (complete).
set -eu
jar="$1"
java="${2:-java}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
project="$root/src/TemporalGraph.Query"
out="$project/Generated"
mkdir -p "$out"
rm -f "$out"/*
cd "$project/grammar"
"$java" -jar "$jar" -Dlanguage=CSharp -visitor -no-listener -package TemporalGraph.Query.Parsing -o "$out" Cypher.g4
# Keep only the C# sources (ANTLR also writes .interp/.tokens files used by its own tooling).
find "$out" -type f ! -name '*.cs' -delete
ls "$out"
