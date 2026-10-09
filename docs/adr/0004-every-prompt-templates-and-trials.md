# 0004. Every prompt, versioned templates and trials; retrieval becomes a source, not the product

- Status: proposed
- Date: 2026-10-10

## Context

Goal 0.3 shipped retrieval: append the most similar earlier prompt that went well. A replay over one owner's real store
(about 2,000 typed prompts, imported with implicit scores) did not support it:

- About a third of typed prompts steer a running session (approvals 21%, merge and pull-request steps 8%, "continue" 6%).
  Their best match is another such prompt; nothing is learned.
- Restricted to prompts of 200 characters or more (183 scored, 100 eligible), the best matches share a topic, not a
  better way to ask. Reading the pairs, none would have improved the new prompt except exact repeats.

The same transcripts hold a larger and more regular population the roadmap did not cover: prompts one agent writes for
another. In that owner's history: 1,084 subagent prompts, 4,565 page-extraction prompts, 460 prompts an orchestrator sent
into a session (median 1,421 characters), 109 spawned-task prompts. These repeat by kind, are written without a person
editing each one, and their runs carry objective cost: duration, tokens and tool calls are recorded for every foreground
subagent run.

Claude Code hooks make a live loop possible for them: a `PreToolUse` hook may replace a tool call's input
(`updatedInput`, documented for the `Agent` and `WebFetch` tools), and `PostToolUse` receives the run's telemetry.
A `UserPromptSubmit` hook cannot rewrite a typed prompt, only add context.

## Options

1. Keep tuning retrieval on typed prompts: a length cut-off, better matching. Cheap; the replay says the ceiling is low.
2. Templates for typed session openers only. Fewer prompts, still no objective outcome; misses the agent traffic.
3. Every prompt, any author: kinds, versioned templates per kind, and trials (A/B with a held-out arm) that promote a
   challenger only on measured outcomes. Retrieval stays as one way to propose a challenger. More to build; it targets
   the population that repeats and can be measured.

## Decision

Option 3. whetstone's unit is a **prompt kind** (for example a subagent asked to explore code, a page-extraction prompt,
a typed request to continue a repository's work). Each kind has a champion template and may have challengers. Every
`enhance` assigns an arm (champion, a challenger, or held out) deterministically from its request id, and `feedback`
reports the outcome. A challenger replaces the champion only when its outcomes are better by the rule in
[the design](../specs/2026-10-10-templates-everywhere-design.md). The best champion of each kind is that kind's gold prompt.

## Consequences

- The roadmap is reordered: capture every prompt kind first, then templates and trials, then challengers written by a model.
- The contract grows within v1 (additive): `feedback` gains optional run measures; `enhance` uses `template_id`,
  `template_version` and `held_out`, which v1 already has.
- A client that can only add context (typed prompts) runs trials of added context against none.
- Retrieval code stays and feeds challenger proposals; serving it directly stays off by default.

## Reopen if

- After 50 trials of one agent prompt kind, no challenger beats its champion on any measure.
- Clients stop exposing a way to replace a prompt before it runs.
