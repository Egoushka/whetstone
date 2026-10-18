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
- 0.5 (trials): one kind reaches a look of its trial (at least 30 counted runs per arm) with a held-out share,
  `whetstone report` shows it, and the promotion rule in docs/specs/2026-10-10-templates-everywhere-design.md decided it.
- 0.6 (reward): for one owner's week of live use, at least 80% of typed requests followed by a correction have a
  correction reason, and the rule-based reason agrees with 30 hand labels in at least 75% of them; delayed outcomes are
  collected for at least 80% of typed requests made in a git repository; `whetstone report` shows rework, the kind's
  cost measure, the guardrails and each arm's cache-read share.
- 0.7 (evidence): a model-written challenger wins at least one trial against a hand-written champion at equal or lower
  cost, or the model stays off; and one kind with two or more challengers reaches a look sooner than the fixed split
  would have (same traffic, counted from the store).
- 0.8 (replay): a challenger screened in the gym and then tried live reaches a look, and the gym's verdict and the live
  one are reported side by side.
- 0.9 (context): a template with the memory slot finishes a trial against the same template without it, and the
  instruction-ablation report lists every rule with its session counts.
- 0.10 (levels): routing evidence names a cheaper model for at least one kind at a look, and the owner's routing change
  that followed is linked from the report.
- 0.11 (person): the owner reads four weekly reports, and the expander or the coach is kept or dropped on its trial's
  record; the state-aware trial runs only after its privacy decision is recorded.
- 0.12 (agents): one orchestrator kind is scored by its children, and one graduation proposal is accepted or rejected
  by the owner with its evidence.

If after 0.5 champions do no better than the held-out share on any kind, stop and find out why before 0.7.
