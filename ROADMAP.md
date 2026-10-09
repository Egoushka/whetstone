# Roadmap

Each goal is written as what is true when it is done. Finishing a goal is a minor release; 1.0 is when the contract is
declared stable. What changed in each release: [CHANGELOG.md](CHANGELOG.md).

Legend: ✅ done · ⏳ in progress · ▶ next · · later · ✗ superseded

## Stage 0 — It answers, and never gets in the way (0.1–0.3)

- ✅ **0.1** The contract and a pass-through: `enhance` and `feedback` as MCP tools with `schemas/*/v1`, a server that
  returns the prompt unchanged, stores nothing, answers within its latency budget, and a client in chargehand's tests.
- ⏳ **0.2** It remembers: every prompt and its outcome is stored per user, with secrets redacted before storage, and can
  be exported and deleted (docs/privacy.md).
- ✗ **0.3** It retrieves (bar not met; superseded by ADR 0004, the code is kept as a source of challengers): `enhance` returns your most similar past prompt that went well, filled in for this request,
  with the reason. No model call yet.

## Stage 1 — It learns from every prompt (0.4–0.6), ADR 0004

- · **0.4** It sees every prompt: agent-written prompts (subagent, page extraction, orchestrator) are captured live and
  imported from transcripts with their run measures; every prompt gets a kind (docs/specs/2026-10-10-templates-everywhere-design.md).
- · **0.5** It runs trials: versioned templates per kind, arms assigned per request with a held-out share, and a
  challenger promoted only by the rule in the design. `whetstone report` shows each kind's record.
- · **0.6** It writes challengers: from the best stored prompts of a kind and by an optional configured model under a
  cost cap per call.

## Stage 2 — It knows you beyond one client (0.7–1.0)

- ⏳ **0.7** It imports history: past prompts from agent transcripts (Claude Code done; OpenCode next) and from a memory
  service, redacted on the way in.
- · **0.8** It suggests the kind of task, so a client's model picker can use it.
- · **1.0** The contract is declared stable.

## Not planned

Running agents, choosing models, sharing prompts between users, a hosted service.
