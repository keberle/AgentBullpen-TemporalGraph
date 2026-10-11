# Re-index every book in data/books/books.json and promote each one. --dry-run shows the plan and runs nothing.
& "$PSScriptRoot/temporalgraph.cli.ps1" index rebuild @args; exit $LASTEXITCODE
