# 0005. A Claude Code mod rewrites typed prompts, so typed prompts run full template trials

- Status: accepted
- Date: 2026-10-19
- Amends: [0004](0004-every-prompt-templates-and-trials.md), which says a typed prompt can only get added context

## Context

ADR 0004 and the design (decision 8) rest on one limit: a `UserPromptSubmit` hook cannot rewrite a typed prompt, so a
typed prompt's trial could only be "added context against none". That limit holds for hooks. It does not hold for a
Claude Code **mod** (a plugin whose hooks module runs in the client):

- its `prompt.submit` hook receives the prompt and passes `next({ ...e, text })`; the model reads the replaced text;
- its `turn.complete` hook receives `usage` (token counts including cache reads and writes), `durationMs`, `isAborted`
  and the `turnId` that `turn.start` announced for the same turn;
- `$.http.fetch` reaches a local HTTP service, so the mod can call whetstone's MCP endpoint itself.

A working mod, `prompt-edit`, already rewrites typed prompts with a small model under fixed rules. It is a single
hard-coded variant with no arms and no outcome record.

## Options

1. Keep typed prompts on added context only. No client change; typed kinds can never be A/B tested as whole prompts.
2. Make the mod a whetstone client. `prompt.submit` calls `enhance`, `turn.complete` calls `feedback`. The Haiku
   rewrite becomes one template (a challenger) among others instead of a separate path.

## Decision

Option 2. A typed prompt is a first-class case of the loop in ADR 0004: it gets a kind, an arm from the request id, a
template (framing text around the unchanged original, or a model rewrite as a challenger) and run measures.

- The original prompt is still never replaced by a different prompt: a template keeps it byte for byte between `before`
  and `after` (design decision 3). A model rewrite is a challenger whose source is `model`, and it passes the checks
  `prompt-edit` already applies (identifiers, numbers and language kept, length within bounds).
- The mod shows every change to the person (the vision requires it), keeps the original, and a leading `*` sends the
  prompt as typed.
- Run measures come from `turn.complete`: `completed` (not aborted), tokens, cache tokens, `duration_ms`. `asked_again` is
  set on the previous request when the next typed prompt reads as a correction.
- Exclusions stay: prompts in the employer's folder, anything that looks like a credential, pasted or long text,
  attachments, slash commands, and prompts not typed by the person.
- Fail open: any error, timeout or bad answer sends the prompt as typed (CLAUDE.md working rule).

## Consequences

- Design decision 8 changes for typed prompts: the trial is template against template against held-out, not context
  against none. Hooks stay the client for agent and fetch prompts.
- The mod needs the whetstone key. It reads it from the login keychain through `$.process` at start and keeps it in
  memory only.
- Subscription traffic is about 92% cache reads, so a trial compares quality and rework (`asked_again`, completion),
  not token savings; cost is still reported with the cache weights.
- A prompt rewritten ahead of the model changes what the session transcript contains. `whetstone import` of those
  transcripts will see the rewritten text; live records, not imports, are the source for kinds the mod serves.

## Reopen if

- Claude Code removes `prompt.submit` rewriting, or changes what `turn.complete` reports.
- After a month of live turns, no kind reaches a first look because typed volume is too low (the design's 30 runs a kind).
