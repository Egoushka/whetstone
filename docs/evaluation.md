# How we know it helps

A rewrite that reads better is not evidence. whetstone helps only if runs that used a rewrite do at least as well and
cost no more than runs that did not.

## Signals (from `feedback`)

- **Acceptance:** the user sent the rewrite. Necessary, not sufficient: people accept text that looks good.
- **Score:** the client's own score for the run (chargehand: support-checked claims, tests passing, review verdict).
- **Cost:** dollars or tokens of the run.
- **Override:** the user changed the model the client picked; a signal for the task-kind hint.

## The held-out share

From goal 0.4 a share of requests (default 10%) gets a pass-through on purpose. Their outcomes are the baseline. The
report compares, per task kind: mean score, mean cost, and acceptance, with intervals; it states the number of runs
and says "not enough data" below 20 per side.

## Bar for each goal

- 0.2 (remembers): the redaction corpus passes (every seeded secret shape redacted, every must-keep string kept) and a
  replay of it through `enhance` leaves no seeded secret in the stored file; `export`, `forget --all --confirm`, `export`
  leaves nothing; two user ids never share a file; a failing store changes no answer; after a week of the owner's use,
  gitleaks over `export` finds nothing.
- 0.3 (retrieval): acceptance at least 40% over 50 rewrites. Not met and not pursued: a replay over real data showed
  the matches would not improve the prompt (ADR 0004). Retrieval stays as a source of challengers.
- 0.4 (every prompt): for one owner's week of use, at least 80% of agent-written prompts get a kind other than `other`,
  and at least 90% of foreground agent runs have run measures stored; the redaction corpus and the export audit pass
  with agent prompts included.
- 0.5 (trials): one kind completes a trial of at least 30 runs per arm with a held-out share, `whetstone report` shows
  it, and the promotion rule in docs/specs/2026-10-10-templates-everywhere-design.md decided it.
- 0.6 (challengers): a model-written challenger wins at least one trial against a hand-written champion at equal or
  lower cost, or the model stays off.

If after 0.5 champions do no better than the held-out share on any kind, stop and find out why before 0.6.
