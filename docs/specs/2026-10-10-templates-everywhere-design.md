# Every prompt: kinds, versioned templates and trials

ADR 0004 moves whetstone from retrieving earlier prompts to running trials of templates, for every prompt a person or an
agent writes. This page is the design; the roadmap orders the work.

## Terms

- **Kind**: a family of prompts that do the same job, such as `agent/explore`, `fetch/extract`, `typed/continue`.
- **Template**: versioned text that frames a prompt of one kind. It never replaces what was asked; it adds before and
  after it, and may fill slots from the request's context.
- **Champion**: the template a kind uses by default. **Challenger**: a template on trial against it. **Gold**: a kind's
  champion once it has beaten at least one challenger.
- **Arm**: what one request got: the champion, a challenger, or nothing (held out).
- **Run measures**: what a client can report about the run a prompt started: completed or not, tokens, duration, tool
  calls, whether the author asked again.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Which prompts | Every prompt a client sends: typed by a person, written by an agent for a subagent, for a page extraction, or by an orchestrator for a session. The client says which in `context.client` (`claude-code`, `claude-code/agent`, `claude-code/fetch`, an orchestrator's name). |
| 2 | Kinds | A client may send `task_kind`. Otherwise whetstone assigns one by rules kept in configuration: for agent prompts, the subagent type and the first verb of the call's description; for typed prompts, the opening words (`continue`, `research`, `review`, …). A prompt no rule covers is kind `other` and is never templated. |
| 3 | What a template is | `before` text, `after` text, and named slots (`{{repository}}`, `{{commit}}`, `{{task_kind}}`) filled from the request's context. The original prompt sits between them unchanged, so the change is always visible. Versions are immutable; a new text is a new version. |
| 4 | Arms | Assigned from a hash of the kind and the request id, so a replay assigns the same arm and each arm gets a balanced mix of kinds. With a challenger on trial: champion 60%, challenger 30%, held out 10%. Without one: champion 90%, held out 10%. A kind with no champion is held out entirely, and only recorded. |
| 5 | Outcome per kind | Agent and fetch prompts: completed (not failed, not asked again within the same parent turn), then tokens, duration and tool calls. Typed prompts: the implicit score from the next prompt and the session's commits, as for imports. A client sends what it knows through `feedback`; missing measures are missing, never zero. |
| 6 | Promotion | By the rule below ("Promotion rule"): fixed looks, a completion test, a non-inferiority gate and a cost measure that counts cache tokens. Every decision is written to the store with the counts that decided it. |
| 7 | Where templates come from | By hand; from the best-scored stored prompts of the kind (the retrieval code ranks them); later, written by a configured model under a cost cap. Each source is recorded on the version. A template from a source with strong outside evidence (`prior`) may start as champion without a trial and is watched by the regression monitor below. |
| 8 | Live clients | For agent prompts, a `PreToolUse` hook calls `enhance` and replaces the call's prompt with the answer (`updatedInput`); `PostToolUse` sends the run measures as `feedback`; a background run's measures come from its transcript when it stops. For typed prompts a `UserPromptSubmit` hook cannot rewrite the prompt, so the template is sent as added context, and the trial is that context against none. Every failure path sends the original prompt. |
| 9 | Contract | Within v1, additively: `feedback.outcome` gains optional `completed`, `tokens_in`, `tokens_out`, `cache_read_tokens`, `cache_write_tokens`, `duration_ms`, `tool_calls`, `asked_again` and `effort` (`model` and `cost_usd` exist already). `enhance` already returns `template_id`, `template_version`, `held_out`, `task_kind`. |
| 10 | Report | `whetstone report` prints, per kind and pooled per template class: runs per arm, completion, median cost and duration, the champion's version and record, the sample-ratio check, and any decision taken. It reads only. |

## Promotion rule

Replaces the first draft (30 runs, bootstrap intervals, either completion or tokens). Reasons are in the research
note behind it: at 30 runs per arm a real 10-point completion gain was found about 1 time in 6, and checking after every
10 runs promoted a template with no effect about 21% of the time. At one owner's volume most kinds will never finish a
trial, so the rule is built to be honest first and fast second.

- **Runs that count.** A run counts when it has an outcome with `completed` set. Missing measures are missing, never
  zero. Only runs of one model id count together: when the `model` reported for a kind changes, the evidence for that
  kind starts again (earlier runs stay stored and are reported apart). Runs without a model id are not mixed with the
  rest.
- **Looks.** A decision is taken only when the challenger arm reaches 30, 60, 100, 150 or 200 counted runs, never in
  between. After the look at 200 an undecided challenger is retired as "no difference found".
- **Promotion on completion.** The challenger replaces the champion at a look when the z statistic of the
  completion-rate difference (two proportions) is above 2.2. Planned error rate across all five looks is about 5%.
- **Promotion on cost.** At a look the challenger may instead replace the champion when its median run cost is at least
  10% lower with the bootstrap interval of the ratio, taken at the same 2.2 level, below 1, and both gates hold:
  completion is not worse by more than the margin (lower bound of the difference above minus 15 points; a 10-point
  margin needs about 260 runs per arm, so it is a setting, not a default) and the `asked_again` rate is not higher by
  more than the same margin.
- **Retirement.** A challenger is retired at a look when the completion z statistic is below minus 2.2.
- **Run cost.** `tokens_in`, `cache_write_tokens`, `cache_read_tokens` and `tokens_out`, each times a weight from
  configuration (starting weights: 1, 1.25, 0.1 and 5, to be updated when the provider's prices change). Duration and
  tool calls are reported and watched as guardrails; they never trigger a promotion. If a run has no cache measures,
  its cost is not computed and it does not count toward a cost decision.
- **Pooling.** Kinds of one template class (for example every kind a stop-rule paragraph is added to) may be pooled for
  a decision. The report shows per-kind counts beside any pooled number, and a pooled result stands only when no kind
  with 30 or more runs points the other way.
- **Sample-ratio check.** For every kind with a challenger, the arm counts are compared with the planned 60/30/10 split
  (chi-square, p below 0.001). A kind that fails takes no decision until the cause is found.
- **Regression monitor.** A champion is compared with the held-out arm at the same looks. A completion difference
  below minus 2.2 z, or a cost difference worse by 10% with the interval above 1, demotes it to challenger status; the
  record stays. Templates that start as champion on outside evidence rely on this alone.
- **Validity first.** Decisions are printed by `whetstone report` and applied only by the owner, until 30 to 50 runs
  have been labelled by hand and the share where "completed, not asked again" matches the label is recorded in the
  report. A proxy that disagrees with the labels in more than a quarter of runs is fixed before any decision.

## Privacy

Agent-written prompts are stored and redacted like typed ones (docs/privacy.md). They often quote file contents and
tool output, so the same exclusions apply at capture: by folder, by the paths the run touched, and by text. Templates
are text the owner can export and forget like any row.

## Not in this design

Sharing templates between users, choosing models, and templates for kinds with fewer than 30 prompts a month (they
cannot finish a trial in a reasonable time).
