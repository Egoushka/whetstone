---
title: "Status and evidence"
description: "Each capability marked works, partial or not yet, with the test, script or document behind the mark, and what CI runs."
order: 5
section: "Project"
---

`works` means a test in this repository covers it. `partial` means part of it is untested or incomplete. `not yet` means a document describes it and it is not built. `met` means a goal's bar in [docs/evaluation.md](../evaluation.md) is met. This page describes `main` at version 0.2.0 ([CHANGELOG.md](../../CHANGELOG.md)).

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
| A week of real use with a clean audit (the 0.2 bar) | met | [docs/evaluation.md](../evaluation.md) |
| `replay`: the best earlier match for each scored request, and how many are eligible | works | [ReplayTests.cs](../../tests/Whetstone.Tests/Retrieval/ReplayTests.cs) |
| A full-text index kept in step with the store, and `reindex` | works | [IndexTests.cs](../../tests/Whetstone.Tests/Storage/IndexTests.cs) |
| Retrieval of your best past prompt, off by default | works | [RetrieverTests.cs](../../tests/Whetstone.Tests/Retrieval/RetrieverTests.cs) |
| Retrieval in real use, 50 rewrites at 40% acceptance (the 0.3 bar) | not pursued | [ADR 0004](../adr/0004-every-prompt-templates-and-trials.md) |
| Agent-written prompts captured with run measures (0.4) | partial | [AgentImportTests.cs](../../tests/Whetstone.Tests/Import/AgentImportTests.cs), [the design](../specs/2026-10-10-templates-everywhere-design.md) |
| Templates per kind and trials (0.5) | partial | [TemplateTests.cs](../../tests/Whetstone.Tests/Templates/TemplateTests.cs), [TrialTests.cs](../../tests/Whetstone.Tests/Templates/TrialTests.cs), [ReportTests.cs](../../tests/Whetstone.Tests/Templates/ReportTests.cs) |
| Reward signals: delayed outcomes, correction reasons, review findings, person time (0.6) | partial | [the design](../specs/2026-10-19-reward-signals-design.md) |
| Challengers written by a model, bandit allocation, trait trials (0.7) | partial | [the design](../specs/2026-10-19-evidence-volume-design.md) |
| Import of Claude Code sessions with an implicit score | works | [ImportTests.cs](../../tests/Whetstone.Tests/Import/ImportTests.cs) |
| Removing stored prompts by content (`forget --text`) | works | [CommandTests.cs](../../tests/Whetstone.Tests/Storage/CommandTests.cs) |
| Import from OpenCode (part of 0.7) | works | [ROADMAP.md](../../ROADMAP.md) |
| Replay gym: case selection, pairs, screen (0.8) | partial |
| Context slots, levels and routing, person-facing help, agent structure (0.9 to 0.12) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| A stable contract (1.0) | not yet | [ROADMAP.md](../../ROADMAP.md) |
| Serving beyond loopback from the command line | not yet | [Program.cs](../../src/Whetstone.Server/Program.cs) |
| A release binary or package | not yet | [going-public.md](../going-public.md) |

The audit script is `partial`: the changelog says its author checked it once by hand against an unredacted token, and no automated test runs it ([CHANGELOG.md](../../CHANGELOG.md)). The 0.2 bar is `met`: its tests pass, and after a week of the owner's use (2026-10-04 to 2026-10-11) the audit over the owner's store found nothing on 2026-10-12. Goals 0.4 to 0.7 are `partial`: their code is built and tested, and their bars need live use measured with `whetstone report`. The gym (0.8) is `partial`: selection, pairs and the screen are tested with a fake client; no real gym client exists yet.

## What `enhance` returns

By default, the prompt unchanged: the enhancer is `PassThrough`, and every answer has `changed: false` ([PassThrough.cs](../../src/Whetstone/PassThrough.cs)). With `WHETSTONE_RETRIEVAL=on` and `WHETSTONE_RETRIEVAL_MIN_SCORE` set, the `Retriever` appends your closest earlier prompt that was scored well ([Retriever.cs](../../src/Whetstone/Retrieval/Retriever.cs), [Program.cs](../../src/Whetstone.Server/Program.cs)). Only a row with a `score` can be eligible, so a store whose clients send no score gets the pass-through even with retrieval on. Whether rewrites help is the question [docs/evaluation.md](../evaluation.md) sets up: acceptance, score and cost against a held-out share. With `WHETSTONE_TEMPLATES=on`, a request whose `task_kind` has a champion template gets the champion, a challenger, or, in the held-out share, the answer it would have had without templates; the arm comes from a hash of the kind and the request id ([TemplateEnhancer.cs](../../src/Whetstone/Templates/TemplateEnhancer.cs)). No trial has reached a look yet, so today there is no evidence that a rewrite helps.

## Tests and CI

`scripts/check.sh` is the check before a push: `dotnet format --verify-no-changes`, a Release build with warnings as errors, then the tests ([check.sh](../../scripts/check.sh)). [CI](../../.github/workflows/ci.yml) runs on every pull request and every push to `main`:

- `scripts/check.sh` on Ubuntu;
- the schema check with `jsonschema` 4.23.0;
- gitleaks over the full history;
- on pull requests, a check of the commit messages ([check-commits.sh](../../scripts/check-commits.sh)).

The test suite covers the contract, the deadline, both transports, the redactor, the store, the index, retrieval, the import and the commands. This page states no test count. Nothing here measures speed beyond the deadline tests, and no benchmark exists.

## Limits

- **Redaction** is pattern based and has known gaps ([Privacy and data](privacy.md#what-the-redactor-replaces)).
- **No encryption** in the file; the directory and file are readable by their owner only ([ADR 0003](../adr/0003-memory-store.md)).
- **Permission modes** are set with Unix file modes. CI runs on Ubuntu only ([ci.yml](../../.github/workflows/ci.yml)).
- **The repository is private** until the maintainer decides to publish it ([going-public.md](../going-public.md)).
