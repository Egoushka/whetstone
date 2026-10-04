# Changelog

All notable changes are listed here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions:
[SemVer](https://semver.org/).

## [Unreleased]

### Added

- Vision, roadmap, the first-version spec, privacy and evaluation rules, ADRs 0001 and 0002.
- A going-public checklist (`docs/going-public.md`).
- The `enhance/v1` and `feedback/v1` contract schemas with examples, and a check that validates them.
- The .NET solution: contract types that round-trip every valid schema example, `scripts/check.sh`, and CI running it.
- An MCP server with `enhance` and `feedback` as a pass-through, over stdio (`mcp`) and HTTP (`serve`, bearer key,
  `GET /health`). A slow, blocking or failing enhancer yields the original prompt inside `deadline_ms`.
