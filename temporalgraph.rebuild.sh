#!/usr/bin/env sh
# Re-index every book in data/books/books.json and promote each one. --dry-run shows the plan and runs nothing.
exec "$(dirname "$0")/temporalgraph.cli.sh" index rebuild "$@"
