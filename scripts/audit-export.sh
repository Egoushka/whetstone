#!/bin/sh
# A second opinion on the store (bar of goal 0.2, docs/evaluation.md): gitleaks over everything `whetstone export` writes.
# Usage: scripts/audit-export.sh [command that starts whetstone]
#   scripts/audit-export.sh                                    runs from this checkout (dotnet run)
#   scripts/audit-export.sh dotnet path/to/Whetstone.Server.dll  runs a published build
# The store comes from WHETSTONE_DATA_DIR and WHETSTONE_USER, as for the server. Exit 0: gitleaks found nothing in the export.
# A finding means the redactor missed a shape: add it to tests/Whetstone.Tests/Redaction/Corpus.cs first, then fix the redactor,
# then `whetstone forget` the row.
set -eu
if [ "$#" -eq 0 ]; then
  set -- dotnet run --project "$(dirname "$0")/../src/Whetstone.Server" --
fi
"$@" export | gitleaks stdin --redact --no-banner
