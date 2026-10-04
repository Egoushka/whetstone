# whetstone 0.2: it remembers

- Status: draft for the maintainer's review. Decisions 1 to 3 are the ones to veto; they are argued in ADR 0003.
- Date: 2026-10-05

## Goal

Every `enhance` call and its outcome is stored for one user, with secrets redacted before storage, and the user can export
all of it and delete any part of it. Nothing is read back yet: retrieval is goal 0.3, so this goal changes what whetstone
keeps, not what it answers.

Done when (bar in docs/evaluation.md): (1) the redaction corpus passes, with no seeded secret left in a stored byte after a
replay through `enhance`; (2) `export` then `forget --all --confirm` then `export` leaves nothing, and `forget --repository`
removes only that repository's rows; (3) two user ids never share a file; (4) a storage failure leaves every answer
unchanged; (5) a week of the owner's own use, then `export` scanned with gitleaks, finds no secret.

## Where it stands

1. **The server** answers `enhance` with the prompt unchanged and `feedback` with `{}`; it stores nothing (0.1).
2. **The rules** are written (docs/privacy.md): what is stored, redact before storage, one store per user, export and delete.
3. **Request identity:** every `enhance` answer carries a `request_id`; `feedback` returns it. That is the join key.
4. **Deployment:** a login launchd agent with the API key in the Keychain; no data directory yet.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Where export and forget live | Commands of the executable, not MCP tools: `forget` is irreversible and must not sit in an agent's tool list. The contract does not grow. |
| 2 | Users | One key, one user (`WHETSTONE_USER`, default `owner`). The code reaches a store only through `IStores.For(user)`, the path carries the user id, and a test shows two ids never share a file. A key-to-user map comes when a second person exists. |
| 3 | Redaction | Our own patterns and an entropy rule, with a corpus of invented examples that must and must not match. gitleaks audits exports. |
| 4 | Store | SQLite, `<data dir>/<user>/whetstone.db`, directory 0700, file 0600, WAL off (one writer). One table `requests`, one row per `enhance`; `feedback` updates it. |
| 5 | What a row holds | request id, time, the redacted prompt, the redacted context (repository, commit, task kind, client), `changed`, `template_id`, `template_version`, `held_out`, `truncated`, and the outcome fields from `feedback/v1` (null until it arrives). Not the answer text beyond what the fields above say: the rewrite is derived and rebuilt in 0.3. |
| 6 | Size | The stored prompt is cut at 32,000 characters with a marker, and the row says so. |
| 7 | Never block | Storage runs inside the request's budget; any failure is swallowed, logged by exception type, and counted in `/health` as `store_failures`. |
| 8 | Redaction scope | Secret-shaped text only: private-key blocks, cloud and vendor tokens, JWTs, authorization headers, passwords in connection strings and `key=value` assignments, and long mixed high-entropy strings. Not names, emails or paths: those are context the user owns. Git shas and UUIDs are kept. |
| 9 | Export | `whetstone export [--repository R] [--before D]` writes JSON lines to stdout, one `export/v1` record per row. |
| 10 | Forget | `whetstone forget (--all \| --repository R \| --before D)`: prints how many rows match and stops; `--confirm` deletes them and compacts the file. |

## The marker

A redacted span becomes `[REDACTED:<kind>]` (`private-key`, `token`, `password`, `jwt`, `header`, `high-entropy`). The kind
helps the owner see why a prompt looks odd; it never carries part of the value.

## Tasks (each one pull request)

1. **Redactor and corpus:** the patterns, the entropy rule, the must-match and must-not-match corpus, no storage yet.
2. **Store:** `IStores`, the SQLite store, record on `enhance`, outcome on `feedback`, failure isolation, `/health` counter.
3. **Export and forget:** the two commands, the `export/v1` schema with examples checked in CI, the filter and confirm rules.
4. **Run it:** data directory in the launchd agent, a week of use, the audit, the release.

## Out of scope for 0.2

Reading stored prompts back, templates, similarity search, a key-to-user map, encryption inside the file, import,
redaction of names or emails.
