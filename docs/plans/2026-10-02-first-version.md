# Plan: whetstone 0.1

Spec: [docs/specs/2026-10-02-first-version-design.md](../specs/2026-10-02-first-version-design.md).

| # | Task | After | Size |
|---|---|---|---|
| 1 | Schema validation in CI (`scripts/check-schemas.py`) | — | done in the first commit |
| 2 | Solution skeleton, `scripts/check.sh`, CI running it | ADR 0002 accepted | small |
| 3 | MCP server: both tools as a pass-through, stdio and HTTP, deadline, health | 2 | medium |
| 4 | chargehand: `prompt-enhancer` extension, fake enhancer, fallback to the original | 3 (contract only) | medium, in chargehand |
| 5 | Run locally as a launchd agent; connect chargehand's local profile | 3, 4 | small |

Before task 2: accept or change ADR 0002 (stack), and decide public or private for the GitHub repository.
