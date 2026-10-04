# Changelog

All notable changes are listed here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions:
[SemVer](https://semver.org/).

## [Unreleased]

### Added

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

[Unreleased]: https://github.com/Egoushka/whetstone/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Egoushka/whetstone/releases/tag/v0.1.0
