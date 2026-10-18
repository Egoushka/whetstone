# Reward signals: what a good outcome is (goal 0.6)

Every trial optimises what it measures. Today that is completion, tokens, duration and `asked_again`. For a person's
typed prompts, about 92% of tokens are cache reads, so token cost barely moves, and `asked_again` counts a follow-up that
adds new information the same as one that corrects a mistake. A champion chosen on these signals can be one that reads
well and costs the person more. This goal makes the reward match the job before more trials run.

## Terms

- **Delayed outcome**: what happened to the work a request started, known hours or days later: commits, a pull request
  merged, its first CI run green, a revert of its commits within 7 days, a tracker item closed.
- **Correction reason**: why the next prompt corrected the run, when it did.
- **Rework**: a correction whose reason blames the prompt or the run (see decision 2). A follow-up that only adds new
  information is not rework.
- **Review findings**: problems a reviewer (a person or a review agent) found in the change a run produced.
- **Person time**: the minutes the person spent on a request: typing it, answering approvals, and typing follow-ups that
  were rework.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Delayed outcomes | `feedback/v1` gains optional `commits` (count), `pr_merged`, `ci_first_push_green`, `reverted` (within 7 days of the commit) and `item_closed`. They arrive by a later `feedback` for the same `request_id`; a later report adds measures and never erases earlier ones, as today. A command, `whetstone outcomes collect`, fills them for stored requests from the local git history of the request's repository (the commits made in its session between it and the next typed request) and, when the owner configures it, from the forge's command-line client for pull request and CI state. whetstone itself makes no network call. Only typed prompts and orchestrator turns get delayed outcomes; an agent prompt's link to a commit is too indirect. |
| 2 | Correction reason | `feedback/v1` gains optional `correction_reason`, one of `wrong_target` (the wrong file, function or thing), `incomplete` (part of the request was missed), `wrong_approach`, `misread` (the request was misunderstood), `too_much` (more was done than asked), `style` (format or length), `new_info` (the person adds information the prompt did not have), `stop` (the person interrupted) and `other`. A client may send it; otherwise whetstone sets it when it stores the next typed prompt of the same session, by rules over its opening words in English and Ukrainian. **Rework** is `asked_again` with a reason other than `new_info`, `stop` and `other`. |
| 3 | Review findings | `feedback/v1` gains optional `review_findings` (count) and `review_findings_severe` (count of those the reviewer marked blocking). whetstone never runs a review; a client that reviews the produced diff reports what it found. |
| 4 | Person time | `feedback/v1` gains optional `person_ms`: typing time of the prompt, plus time the run waited on the person's approvals, plus typing time of rework follow-ups until the next new request. The client measures it; whetstone stores and sums. |
| 5 | Cache tokens | The run cost already weights cache reads (0.1) and writes (1.25). The weights move from a constant to configuration (`WHETSTONE_COST_WEIGHTS`), and the report shows each arm's cache-read share. An arm whose share is lower by more than 10 points is flagged: a template whose `before` text breaks the cached prefix costs more even when its token counts look equal. |
| 6 | What decides a trial | The primary measure stays completion, with `asked_again` replaced by **rework** wherever the promotion rule names it. Each kind has one cost measure, set in configuration: **person time** by default for typed kinds, **run cost** for agent, fetch and orchestrator kinds. Review findings, `reverted` and `ci_first_push_green` are guardrails: a challenger is not promoted when any of them is worse by more than the non-inferiority margin. Delayed outcomes arrive late, so a look counts only runs older than 7 days for the guardrails that need them. |
| 7 | Validity | The hand-label check in the promotion rule extends to reasons: 30 corrections labelled by hand, and the rule-based reason agrees with the label in at least 75% of them before rework replaces `asked_again` in any applied decision. Until then the report shows both. |

## What changes in the contract and the store

All additive within `feedback/v1`, `enhance/v1` and `export/v1`. Decisions 1 and 2 need to know which requests share a
session, which the store does not record today: `enhance/v1` gains an optional `context.session_id` (an opaque id the
client chooses). Imports store a hash of the transcript's session id, never the id itself, because docs/privacy.md
keeps transcript session ids out of the store. The store gains the new measures as nullable columns on `requests`
(the next store schema version); an older file is upgraded on first use, keeping every row. Missing measures are
missing, never zero, as for the run measures.

## Fit with the vision

Learning from outcomes, not opinions, is the vision's first line; these signals are better outcomes. whetstone runs no
agent, review or forge request of its own: clients report, and the collector reads only local git unless the owner
points it at the forge's client.

## Not in this design

A single combined score (each measure stays apart, so a regression in one cannot hide behind a gain in another), and
reasons classified by a model (rules first; a model is a later challenger to the rules, checked against the same labels).
