# 0003. The memory store: SQLite per user, redact first, export and forget from the command line

- Status: accepted (2026-10-05)
- Date: 2026-10-05

## Context

Goal 0.2 makes whetstone remember every prompt and its outcome (docs/privacy.md). Prompts carry secrets by accident: a pasted
error with a token, a connection string. The rules already fixed: one store per user, redaction before anything is written,
export and delete. Open: where the data lives, how a redaction miss is kept from persisting, and where `export` and `forget`
are exposed. ADR 0002 chose SQLite, one file per user.

## Options

**Where `export` and `forget` live**
1. MCP tools beside `enhance` and `feedback`. Any client could call them; so could an agent that has whetstone in its tool
   list, and `forget` is irreversible.
2. **Chosen.** Commands of the executable (`whetstone export`, `whetstone forget`) that open the user's store on disk. They
   are not in the tool list of any agent, the contract does not grow, and they work with the server stopped.

**Who a request belongs to**
1. A key file mapping tokens to users now. The isolation is exercised end to end; a second user is a goal nobody has (VISION:
   no hosted service).
2. **Chosen.** One key, one user (`WHETSTONE_USER`, default `owner`); the store path carries the user id, and the code reaches
   a store only through `IStores.For(user)`. Tests prove two ids never share a file. A key map is additive later.

**Redaction**
1. Run gitleaks as a library or process. Maintained rules; a process per request or a port of its rules, and a hard
   dependency on how its config changes.
2. **Chosen.** Our own list of patterns plus an entropy rule, with a corpus of invented examples that both must be redacted and
   must not be (git shas, UUIDs). gitleaks runs over exports in tests and by the owner as a second opinion.

## Decision

- SQLite file at `<data dir>/<user>/whetstone.db`, directory mode 0700, file 0600. Data dir: `WHETSTONE_DATA_DIR`, else the
  platform's user data directory. No encryption in the file: disk encryption is the user's, and the file never leaves the host.
- `enhance` records a row after it has its answer; `feedback` fills the outcome of the row with that `request_id`. Feedback
  for an unknown id stores nothing.
- The prompt and every context field pass the redactor before the write. The stored text is capped (32,000 characters, cut
  with a marker); a row records that it was cut.
- A storage failure never changes an answer: the client still gets its prompt (the 0.1 rule), the failure is logged by
  exception type only, and the miss is counted in `/health`.
- `export` writes JSON lines (schema `export/v1`); `forget` takes a filter (`--repository`, `--before`, `--all`) and only
  counts matches until `--confirm` is given.

## Consequences

- A redaction miss is persisted until `forget` removes it; the corpus and the replay test are the guard, and `export` plus
  gitleaks is the audit.
- Stores are per host: moving to another machine is `export` there and a later import (goal 0.7).
- A new dependency: Microsoft.Data.Sqlite.

## Reopen if

A second person needs the same server (key map), or redaction misses reach the owner's own export twice (move to gitleaks'
rules).
