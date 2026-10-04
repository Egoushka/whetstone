# Changelog

All notable changes are listed here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions:
[SemVer](https://semver.org/).

## [Unreleased]

### Added

- The redactor (`Whetstone.Redaction.Redactor`): secret-shaped text becomes `[REDACTED:kind]` before anything is stored, in linear time, with a corpus of 31 secret shapes and 20 look-alikes that must be kept. Nothing calls it yet; the store (WHET-19) does.

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
