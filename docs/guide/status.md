---
title: "Status and evidence"
description: "Each capability marked works, partial or not yet, with the test, script or document behind the mark, and what CI runs."
order: 5
section: "Project"
---

`works` means a test in this repository covers it. `partial` means part of it is untested or incomplete. `not yet` means a document describes it and it is not built. This page describes `main` after version 0.1.0, with the memory store still under `Unreleased` in [CHANGELOG.md](../../CHANGELOG.md).

## Status

| Capability | Status | Evidence |
|---|---|---|
| `enhance` and `feedback` over stdio | works | [StdioServerTests.cs](../../tests/Whetstone.Tests/StdioServerTests.cs) |
| The same over HTTP with a bearer key | works | [HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs) |
| Contract schemas and examples | works | [ContractTests.cs](../../tests/Whetstone.Tests/ContractTests.cs), [check-schemas.py](../../scripts/check-schemas.py) |
| The original prompt on a late, blocking or failing enhancer | works | [DeadlineTests.cs](../../tests/Whetstone.Tests/DeadlineTests.cs) |
| Redaction before storage | works | [RedactorTests.cs](../../tests/Whetstone.Tests/Redaction/RedactorTests.cs), [Corpus.cs](../../tests/Whetstone.Tests/Redaction/Corpus.cs) |
| A stored row per call, outcome filled by `feedback` | works | [StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs) |
| A failing store changes no answer | works | [StoreServerTests.cs](../../tests/Whetstone.Tests/Storage/StoreServerTests.cs) |
| One file per user, owner-only modes | works | [StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs) |
| `export` and `forget` | works | [CommandTests.cs](../../tests/Whetstone.Tests/Storage/CommandTests.cs) |
| `scripts/audit-export.sh` | partial | [audit-export.sh](../../scripts/audit-export.sh) |
| A week of real use with a clean audit (the 0.2 bar) | not yet | [docs/evaluation.md](../evaluation.md) |
| Retrieval of your best past prompt (0.3) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| A held-out share and a report (0.4) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| Templates, versioned (0.5) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| An optional model rewrite (0.6) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| Import of agent transcripts (0.7) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| A task-kind suggestion (0.8) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| A stable contract (1.0) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| Serving beyond loopback from the command line | not yet | [Program.cs](../../src/Whetstone.Server/Program.cs) |
| A release binary or package | not yet | [going-public.md](../going-public.md) |

The audit script is `partial`: the changelog says its author checked it once by hand against an unredacted token, and no automated test runs it ([CHANGELOG.md](../../CHANGELOG.md)).

## What `enhance` returns

Nothing learned. The only enhancer is `PassThrough`, and every answer has `changed: false` ([PassThrough.cs](../../src/Whetstone/PassThrough.cs)). Whether rewrites help is the question [docs/evaluation.md](../evaluation.md) sets up: acceptance, score and cost against a held-out share. That share is goal 0.4, so today there is no evidence that a rewrite helps, because there are no rewrites.

## Tests and CI

`scripts/check.sh` is the check before a push: `dotnet format --verify-no-changes`, a Release build with warnings as errors, then the tests ([check.sh](../../scripts/check.sh)). [CI](../../.github/workflows/ci.yml) runs on every pull request and every push to `main`:

- `scripts/check.sh` on Ubuntu;
- the schema check with `jsonschema` 4.23.0;
- gitleaks over the full history;
- on pull requests, a check of the commit messages ([check-commits.sh](../../scripts/check-commits.sh)).

The test suite covers the contract, the deadline, both transports, the redactor, the store and the two commands. This page states no test count. Nothing here measures speed beyond the deadline tests, and no benchmark exists.

## Limits

- **Redaction** is pattern based and has known gaps ([Privacy and data](privacy.md#what-the-redactor-replaces)).
- **No encryption** in the file; the directory and file are readable by their owner only ([ADR 0003](../adr/0003-memory-store.md)).
- **Permission modes** are set with Unix file modes. CI runs on Ubuntu only ([ci.yml](../../.github/workflows/ci.yml)).
- **The repository is private** until the maintainer decides to publish it ([going-public.md](../going-public.md)).
