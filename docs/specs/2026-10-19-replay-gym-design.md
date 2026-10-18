# Replay gym: trials without waiting for live traffic (goal 0.8)

Status: outline. The detailed design waits for a research note on how chargehand can replay runs; this page fixes the
seam on whetstone's side and is revised when that note lands.

Live traffic finishes few trials. Stored prompts with their repository and commit can be run again, by a client, with a
challenger instead of the champion, on cheap models and in batch, and scored the same way as live runs. whetstone does
not run them: a gym client does (chargehand first), and whetstone supplies cases and records outcomes.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Cases | `whetstone cases export --kind K [--limit N]` writes stored requests of a kind that have a repository, a commit and a known outcome, redacted, as JSON lines. A case never leaves the machine except to the gym client the owner runs. |
| 2 | Outcomes | Gym runs come back through `feedback` with `context.client` naming the gym. They are stored apart from live runs and **never count toward a live promotion**: they screen which challenger goes on live trial. |
| 3 | Unit cases | Each champion keeps five replay cases that must still complete before a challenger replacing it is promoted, and before a new champion is served. |
| 4 | Drift canary | When the `model` reported for a kind changes (the rule already restarts evidence then), the champion's unit cases are queued for the gym, and the report flags the kind until they pass. |
| 5 | Harness comparison | The same cases may be run through different clients (Claude Code, OpenCode, chargehand on a cheap preset). The report compares them per kind. whetstone chooses nothing from it. |

## Fit with the vision

The vision says whetstone runs no agents. The gym keeps that line: running is the client's job, and whetstone's part is
cases in and outcomes out. Decision 5 informs the owner's choice of client; it does not make one.
