# 0006. A replay gym screens challengers before a live trial

- Status: accepted
- Date: 2026-10-19

## Context

At one owner's volume most kinds never reach the promotion rule's first look (30 counted runs per arm), so a challenger
waits weeks for a verdict. Stored prompts that carry a repository and a commit can be run again, by a coding-agent
client pinned to that commit, once as written (control) and once with the challenger (challenger).

A research pass over chargehand (the first client) found:

- chargehand pins a run to any commit its clone holds, and only tracked files at that commit reach the worker.
- Its cheap preset runs a read-only agent for about $0.045 a run (8 priced runs, so plus or minus 2x). About ten control
  and challenger pairs, each with one judge call, fit in a dollar.
- An agent run is a tool loop and cannot use a provider's batch discount; only the judge call could.
- Its request has no field for a template or an arm, so whetstone must render the challenger and send the finished text,
  and chargehand's own whetstone seam must not enhance or report a replay as live feedback.
- Many stored prompts cannot be replayed: steering prompts need the conversation, page extractions need the page, prompts
  about live state (CI, pull requests, "today") change meaning, and imported rows often have no commit.

`docs/privacy.md` says the only outbound call is the rewriting model, sent the redacted prompt and the template. A replay
sends stored redacted prompts to a worker and to a judge model.

## Options

1. No gym: wait for live traffic. Honest and free; most challengers never finish.
2. A gym whose results count toward promotion. Fast; mixes a different harness, model and context with live evidence,
   and the promotion rule's error rates no longer hold.
3. A gym that only screens: a challenger that passes may enter a live trial; only live runs promote it.

## Decision

Option 3. whetstone selects cases, renders the challenger, sends both texts to a gym client (chargehand first), stores
the measures and a pairwise judge verdict in tables of its own (`gym/v1`), and prints "screen pass" or "screen fail".
Gym rows never reach `Trials.Evaluate`. whetstone does not run agents: the client does.

Privacy: a replay may send a stored, redacted prompt and a template's text to the gym client the owner configures and to
the judge model the owner configures. whetstone stores the case id, a hash of the prompt, the measures and the verdict,
not the answers or a second copy of the prompt. `forget` removes gym rows with the requests they came from. Cases come
only from repositories the gym client already holds; folders excluded from import are never replayed.

## Consequences

- `docs/privacy.md` lists the gym as a second outbound path.
- The store gains `gym_cases`, `gym_runs` and `gym_verdicts`.
- chargehand needs a request-level way to skip its own whetstone seam before the first real replay.
- Ideas built on the gym (unit cases per champion, a drift canary on a model change, harness comparison) reuse its tables.

## Reopen if

- Screened challengers do no better in live trials than unscreened ones after ten of each.
- Replays cost more than about $0.10 each on the cheap preset, or chargehand's rate limits stop a night's batch.
