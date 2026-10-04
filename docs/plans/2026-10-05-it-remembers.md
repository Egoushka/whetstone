# Plan: whetstone 0.2

Spec: [docs/specs/2026-10-05-it-remembers-design.md](../specs/2026-10-05-it-remembers-design.md). ADR: [0003](../adr/0003-memory-store.md).

| # | Task | After | Size |
|---|---|---|---|
| 1 | Redactor and corpus | ADR 0003 accepted | small |
| 2 | Store: `IStores`, SQLite, record and outcome, failure isolation | 1 | medium |
| 3 | `export` and `forget` commands, `export/v1` schema | 2 | medium |
| 4 | Run it: data directory in the agent, a week of use, audit, release | 3 | small, then waiting |

Before task 1: accept or change ADR 0003 (export and forget as commands, one key one user, own redactor).
