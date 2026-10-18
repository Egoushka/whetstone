# Replay gym: trials without waiting for live traffic (goal 0.8)

Status: accepted by the owner 2026-10-19; decision recorded in [ADR 0006](../adr/0006-replay-gym-screens-challengers.md).

Live traffic finishes few trials. Stored prompts with their repository and commit can be run again, by a client, with a
challenger instead of the champion, on a cheap model, and scored the same way as live runs. whetstone does
not run them: a gym client does (chargehand first), and whetstone supplies cases and records outcomes.

What the research found: chargehand pins a run to any commit its clone holds, and its cheap preset runs a read-only
agent for about $0.045, so about ten control and challenger pairs fit in a dollar a night. Agent runs are a tool loop and
cannot use a batch discount. chargehand's request has no field for a template or an arm, so whetstone renders the
challenger itself and sends the finished prompt; chargehand's own whetstone seam must be switched off for these runs
(a small change in chargehand) so a replay is neither enhanced again nor reported as live feedback.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Cases | `whetstone gym select --kind K [--limit N]` picks stored requests that can be replayed: a repository and a commit the gym's clone holds, a read-only kind (explore, research, agent briefs), no steering prompt that needs the conversation, no page-extraction prompt (the page is not stored), no prompt about live state (CI, pull requests, "today"). The store keeps the case's id and a hash, not a copy of the prompt. |
| 2 | Outcomes | `whetstone gym run --challenger T --cases N --budget-usd B` runs control and challenger per case in alternating order, one run at a time, and stops at the budget; a control is reused for the same case, commit and model within 7 days. Gym runs are stored in tables of their own (`gym/v1`) and **never count toward a live promotion**. `whetstone gym report` compares the pairs (completion, cost, and one pairwise judge call per case) and prints "screen pass" (no worse on completion and cost, judge preference at least 55%) or "screen fail"; a pass means only that the challenger may enter a live trial. |
| 3 | Unit cases | Each champion keeps five replay cases that must still complete before a challenger replacing it is promoted, and before a new champion is served. |
| 4 | Drift canary | When the `model` reported for a kind changes (the rule already restarts evidence then), the champion's unit cases are queued for the gym, and the report flags the kind until they pass. |
| 5 | Harness comparison | The same cases may be run through different clients (Claude Code, OpenCode, chargehand on a cheap preset). The report compares them per kind. whetstone chooses nothing from it. Later than decisions 1 to 4. |
| 6 | Privacy | Replays send stored, redacted prompts to the gym client and a judge model. docs/privacy.md lists this path (ADR 0006). Answers are not stored. `forget` removes gym rows with the requests they came from. |

## Fit with the vision

The vision says whetstone runs no agents. The gym keeps that line: running is the client's job, and whetstone's part is
cases in and outcomes out. Decision 5 informs the owner's choice of client; it does not make one.
