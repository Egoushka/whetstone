# Changelog

All notable changes are listed here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions:
[SemVer](https://semver.org/).

## [Unreleased]

### Changed

- The roadmap after 0.5: goals 0.6 to 0.12 (reward signals, evidence volume, replay, context, levels and routing, help for the person, agent structure), each with a short spec under `docs/specs/2026-10-19-*` and a bar in docs/evaluation.md. The earlier 0.6 (challengers), 0.7 (imports) and 0.8 (task-kind suggestion) are folded into 0.7 and 0.10.
- A `feedback` report no longer erases `rewrite_accepted`, `model_overridden`, `score`, `cost_usd` or `model` that an earlier report gave and it leaves out, so a delayed report of its own keeps the run's model and score.

### Added

- Reward measures in `feedback/v1`, additively (goal 0.6, docs/specs/2026-10-19-reward-signals-design.md): `outcome.commits`, `pr_merged`, `ci_first_push_green`, `reverted`, `item_closed`, `correction_reason`, `review_findings`, `review_findings_severe` and `person_ms`; and `context.session_id` in `enhance/v1`. The store is schema version 5 (an older file is upgraded on first use, keeping every row), `export/v1` carries them, and `import claude-code` stores a hash of each transcript's session id.
- `whetstone outcomes collect --root DIR [--forge github] [--confirm]` (goal 0.6, decision 1): delayed outcomes for requests with a session and a repository, from the local checkouts under DIR (commits in the request's window, a revert within 7 days) and, with `--forge github`, from the `gh` client (pull request merged, CI on its first commit). Counts until `--confirm`.
- `WHETSTONE_COST_WEIGHTS` and `WHETSTONE_PERSON_TIME_KINDS` for `report` (goal 0.6, decision 5): the token weights move from a constant to a setting, and the kinds judged on the person's time can be named. The report shows each arm's cache-read share of prompt tokens and flags a challenger (against the champion) or a champion (against the held-out arm) whose share is more than 10 points lower: its text may break the cached prefix.
- The Claude Code mod sends an opaque `context.session_id` per session, so whetstone can name the reason a follow-up corrects the request before it and `outcomes collect` can attribute commits.
- Store schema 6: the owner's `hand_label`, `whetstone label list|set`, `hand_label` in `export/v1`, and a report line with how often the proxy (completed, no rework) agrees with the labels (WHET-35); a `template_decisions` table for the owner's template commands; and the replay gym's tables (`gym_cases`, `gym_runs`, `gym_verdicts`, ADR 0006), which `forget` empties along with the requests they came from.
- Correction reasons by rule: a request whose prompt reads as a correction of its session's previous request gives that request a `correction_reason` (and `asked_again` when unreported), unless the client reported one (goal 0.6, decision 2).
- `whetstone report` and the promotion rule use the reward signals (goal 0.6, decision 6): rework replaces `asked_again` in the cost gate (`asked_again` still counts when no reason is reported), typed kinds compare the person's time instead of token cost, and review findings, reverts and red first CI runs are guardrails on any promotion. The report shows rework, median person time and each kind's cost measure.
- `serve --allowed-hosts H1,H2` (or `WHETSTONE_ALLOWED_HOSTS`): the host names accepted besides `localhost` and `127.0.0.1`, so `serve --listen` can bind a non-loopback address.
- A `Dockerfile` and `.github/workflows/image.yml`: the image `ghcr.io/egoushka/whetstone` (linux/amd64), tagged `sha-<12 characters>` on each merge to `main` and `<Version>` on a `v*` tag. See the container section of the reference.

## [0.2.0] - 2026-10-12

### Added

- `scripts/audit-export.toml` allows a git commit hash in a `commit` field, which gitleaks's Sourcegraph rule read as a token in an imported agent prompt; a bare 40-hex string is still a finding. The file uses `[[allowlists]]`, as gitleaks 8.25 and later expect.
- Template trials (ADR 0004; WHET-31): a template store in the user's file (store schema version 4), arms assigned from a hash of the kind and the request id (champion 90 / held out 10, or 60 / 30 / 10 with a challenger), the agent brief completer as the first champion for subagent kinds, and `answer.arm` in `export/v1`. Off unless `WHETSTONE_TEMPLATES=on`.
- `whetstone report`: per kind, runs per arm, completion, median cost (tokens weighted by cache and output) and duration, the sample-ratio check, the regression monitor, and what the promotion rule v2 says at each look. It reads only.
- `whetstone import claude-code` also stores the prompts agents wrote: each `Agent` call's prompt, each `WebFetch` prompt and each turn an orchestrator sent into a session, with the run measures the transcript recorded (completed, duration, tool calls, token counts, model) and a kind by rule (`agent/<type>`, `agent/general-purpose/<job>`, `fetch/extract`, `orchestrator/turn`). The same folder, tool-path and text exclusions apply; the run prints counts of new prompts, those with measures and those with a kind other than `other` (WHET-30).
- Run measures in `feedback/v1`, additively: `outcome.completed`, `tokens_in`, `tokens_out`, `cache_read_tokens`, `cache_write_tokens`, `duration_ms`, `tool_calls`, `asked_again` and `effort`. The store is schema version 3 (an older file is upgraded on first use, keeping every row), a later report adds measures without erasing earlier ones, and `export/v1` carries them inside `outcome` when reported (WHET-29).
- `whetstone import claude-code DIR [--exclude DIR] [--exclude-text REGEX] [--confirm]` stores the prompts typed in past Claude Code sessions, redacted, each with an implicit score from the next prompt and the session's commits (goal 0.7, brought forward; docs/specs/2026-10-09-it-imports-design.md). It counts until `--confirm`, stores each message once, and scores rather than duplicates a prompt a live client already stored.
- `export` and `forget` take `--text REGEX`, matched against the stored prompt ignoring case; `forget --imported` undoes an import and keeps live rows.
- `import --exclude` also drops sessions whose tools opened, edited or searched a path in the folder, or whose shell commands moved into it.
- `WHETSTONE_RETRIEVAL_MIN_CHARS` and `replay --min-chars N`: prompts shorter than N are neither answered with a retrieval nor retrieved.
- `scripts/audit-export.toml`: gitleaks's default rules for the audit, with an allowlist of ordinary words the generic rule reads as keys.
- `whetstone replay` ranks stored prompts with BM25 and prints, for each scored request, the earlier one that best matches it and how many rows are eligible; it reads only.
- A full-text index over stored prompts, kept in step with the store, and `whetstone reindex` to upgrade an older file. `export/v1` gains the optional `source_request_id`.
- Retrieval, off by default: with `WHETSTONE_RETRIEVAL=on` and `WHETSTONE_RETRIEVAL_MIN_SCORE`, `enhance` appends the closest earlier prompt that was scored at or above the user's median. No match, no scored row or any failure returns the prompt unchanged, counted as `retrieval_failures` in `GET /health`.
- `GET /health` reports uptime, CPU seconds, threads and memory.
- `scripts/audit-export.sh`: gitleaks over `whetstone export`, the audit step of goal 0.2's bar (it fails on a store holding an unredacted token, which the script's author checked once by hand).
- The redactor (`Whetstone.Redaction.Redactor`): secret-shaped text becomes `[REDACTED:kind]` before anything is stored, in linear time, with a corpus of 31 secret shapes and 20 look-alikes that must be kept. The store calls it.
- The memory store (ADR 0003): every `enhance` is stored, redacted, in `<WHETSTONE_DATA_DIR>/<WHETSTONE_USER>/whetstone.db` (SQLite, directory 0700, file 0600; user data directory and `owner` by default), and `feedback` fills the outcome of the row with that `request_id`. The prompt is cut at 32,000 characters after redaction; the rewrite is not kept. A storage failure, a slow store or an unwritable directory changes no answer: it is counted as `store_failures` in `GET /health` and logged by exception type.
- `nuget.config` limits restore to nuget.org.
- `whetstone export [--repository R] [--before DATE]` writes everything stored for the user as JSON lines (schema `export/v1`, checked in CI), and `whetstone forget (--all | --repository R | --before DATE) [--confirm]` counts what matches and deletes it only with `--confirm`, overwriting deleted text and rebuilding the file. Options are strict, so a typo is a usage error, never a wider delete. They are commands, not MCP tools.

## [0.1.0] - 2026-10-05

### Added

- Vision, roadmap, the first-version spec, privacy and evaluation rules, ADRs 0001 and 0002.
- A going-public checklist (`docs/going-public.md`).
- The `enhance/v1` and `feedback/v1` contract schemas with examples, and a check that validates them.
- The .NET solution: contract types that round-trip every valid schema example, `scripts/check.sh`, and CI running it.
- An MCP server with `enhance` and `feedback` as a pass-through, over stdio (`mcp`) and HTTP (`serve`, bearer key,
  `GET /health`). A slow, blocking or failing enhancer yields the original prompt inside `deadline_ms`.

[Unreleased]: https://github.com/Egoushka/whetstone/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/Egoushka/whetstone/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/Egoushka/whetstone/releases/tag/v0.1.0
