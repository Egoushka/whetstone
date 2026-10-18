# Roadmap

Each goal is written as what is true when it is done. Finishing a goal is a minor release; 1.0 is when the contract is
declared stable. What changed in each release: [CHANGELOG.md](CHANGELOG.md).

Legend: ✅ done · ⏳ in progress · ▶ next · · later · ✗ superseded

## Stage 0 — It answers, and never gets in the way (0.1–0.3)

- ✅ **0.1** The contract and a pass-through: `enhance` and `feedback` as MCP tools with `schemas/*/v1`, a server that
  returns the prompt unchanged, stores nothing, answers within its latency budget, and a client in chargehand's tests.
- ✅ **0.2** It remembers: every prompt and its outcome is stored per user, with secrets redacted before storage, and can
  be exported and deleted (docs/privacy.md).
- ✗ **0.3** It retrieves (bar not met; superseded by ADR 0004, the code is kept as a source of challengers): `enhance` returns your most similar past prompt that went well, filled in for this request,
  with the reason. No model call yet.

## Stage 1 — It learns from every prompt (0.4–0.7), ADR 0004

The order after 0.5 follows dependency: a trial optimises what it measures, so the reward comes before more evidence,
and more evidence before the work that needs it. Each spec says where a goal bends [VISION.md](VISION.md).

- ⏳ **0.4** It sees every prompt: agent-written prompts (subagent, page extraction, orchestrator) are captured live and
  imported from transcripts with their run measures; every prompt gets a kind (docs/specs/2026-10-10-templates-everywhere-design.md).
- ⏳ **0.5** It runs trials: versioned templates per kind, arms assigned per request with a held-out share, and a
  challenger promoted only by the rule in the design. `whetstone report` shows each kind's record.
- ▶ **0.6** It knows what a good outcome is: delayed outcomes (commits, pull request merged, first CI run green, revert
  within 7 days, tracker item closed), the reason behind each correction, review findings on the produced change, the
  person's time, and cache tokens in the cost; a trial decides on rework and the kind's cost measure, with the rest as
  guardrails (docs/specs/2026-10-19-reward-signals-design.md).
- · **0.7** It gathers evidence faster: bandit allocation with a fixed held-out share, trait trials pooled across kinds,
  challengers written by a configured model from failed runs, a filler-stripping challenger, a library of losing
  prompts, rewrite pairs kept, and imports from OpenCode and a memory service (docs/specs/2026-10-19-evidence-volume-design.md).

## Stage 2 — It learns beyond live traffic (0.8–0.10)

- · **0.8** It replays: a gym client re-runs stored cases with challengers at the original commit; gym outcomes screen
  challengers, each champion keeps five unit cases, a model change triggers a canary, harnesses can be compared
  (docs/specs/2026-10-19-replay-gym-design.md).
- · **0.9** It fills context: template slots from a memory service and from recent failures, and trials of instruction
  rules and skill versions (docs/specs/2026-10-19-context-slots-design.md).
- · **0.10** It suggests levels, models and effort: a champion per price level per kind, and routing evidence the owner
  turns into rules elsewhere; the client decides (docs/specs/2026-10-19-levels-and-routing-design.md).

## Stage 3 — It helps the person and the agents (0.11–1.0)

- · **0.11** It helps while writing: a shorthand expander, a pre-send coach, a weekly report on the person's time, and a
  state-aware template trial behind a privacy decision (docs/specs/2026-10-19-person-facing-design.md).
- · **0.12** It understands agent structure: orchestrator prompts scored by their children, kinds discovered by
  clustering, graduation proposals for shared instructions, templates for what agents write, and a `brief` tool
  (docs/specs/2026-10-19-agent-structure-design.md).
- · **1.0** The contract is declared stable.

## Not planned

Running agents, choosing models, sharing prompts between users, a hosted service.
