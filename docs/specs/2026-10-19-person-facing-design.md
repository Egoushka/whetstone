# For the person: expander, coach, weekly report, state-aware trial (goal 0.11)

The earlier goals improve prompts after the fact. This goal helps the person while writing, and shows them where their
time goes.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Shorthand expander | A model-rewrite challenger for terse typed prompts (English or Ukrainian, under a length the owner sets) that writes a full brief: goal, files, stop rule. It is shown before sending, the original is kept, and it is scored like any challenger. |
| 2 | Pre-send coach | `enhance` may return an optional `advice` line: the kind's rework rate and its most common correction reason, when the kind has enough runs. The client shows it; it never blocks or delays the prompt beyond the deadline. |
| 3 | Weekly report | `whetstone report --person` prints, per week: person time by kind, rework by reason, the kinds where advice or the expander changed the outcome. It reads only. |
| 4 | State-aware templates | A trial, off by default: a template may vary by coarse state the client sends (`context.state`, a few named buckets such as time of day or a low-energy flag). Raw readings from wearables or presence sensors are never sent or stored. Which buckets, and whether this runs at all, is the owner's privacy decision, recorded in docs/privacy.md before any code. |

## Fit with the vision

Decision 1 is a model rewrite, allowed by ADR 0005 as a challenger with checks. Decision 4 touches data more personal
than prompts; it stays a trial behind a privacy decision, stores buckets only, and is forgotten with the rest.
