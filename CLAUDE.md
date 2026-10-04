# CLAUDE.md — whetstone

A personal prompt enhancer behind two MCP tools, `enhance` and `feedback`. .NET 10 (ADR 0002): `src/Whetstone` holds
the contract types, `tests/Whetstone.Tests` checks them against the schema examples. The first client is chargehand (`Egoushka/chargehand`).

## Commands

```bash
scripts/check.sh                                                                    # the one check before a push: format, build with warnings as errors, tests
UV_CACHE_DIR=$TMPDIR/uv uv run --with jsonschema python3 scripts/check-schemas.py   # every schema example validates or fails as named
gitleaks git --redact -v                                                            # secret scan over history
```

Hooks: `git config core.hooksPath .githooks` (denylist + gitleaks on pre-commit, denylist + Conventional Commits on
commit-msg). Never `--no-verify`.

## Public vs private — the rule for every file

The GitHub repository is private until the maintainer marks it public (`docs/going-public.md`), but it is developed as a public one: every file, commit message, issue and PR text must be fit to be read by anyone today. Never commit: a real prompt from anyone, IP addresses, hostnames, key
aliases, employer or project names, absolute home paths, session ids, API keys. Tests and examples use invented
prompts. Personal setup lives in the gitignored `CLAUDE.local.md` and `data/`.

## Conventions

- Decisions: `docs/adr/NNNN-title.md` from `docs/adr/template.md`.
- Schemas: `schemas/<name>/v<major>/`; `$id` carries the major; additive changes only within a major; examples are
  `valid-*.json` or `invalid-*.json`.
- Specs in `docs/specs/YYYY-MM-DD-<topic>-design.md`, plans in `docs/plans/`.
- Commits: Conventional Commits. A PR title may end with the tracker key `(WHET-12)`; tracker URLs never appear in
  public text.

## Working rules

- The rules in `docs/privacy.md` bind every change that stores or sends data.
- A goal is done only when its bar in `docs/evaluation.md` is met, not when its code merges.
- whetstone never blocks a client: any failure path returns the original prompt.
