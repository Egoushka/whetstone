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
- 0.3 (retrieval): acceptance at least 40% over 50 rewrites.
- 0.5 (templates): a new template version replaces the old only if its score is no lower and cost no higher over at
  least 20 runs each.
- 0.6 (model rewrite): beats retrieval-only on score at equal or lower cost, or it stays off.

If after 0.4 rewrites score lower than the held-out share, stop and find out why before building templates.
