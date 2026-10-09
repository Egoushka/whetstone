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
| 4 | Arms | Assigned from a hash of the request id, so a replay assigns the same arm. With a challenger on trial: champion 60%, challenger 30%, held out 10%. Without one: champion 90%, held out 10%. A kind with no champion is held out entirely, and only recorded. |
| 5 | Outcome per kind | Agent and fetch prompts: completed (not failed, not asked again within the same parent turn), then tokens, duration and tool calls. Typed prompts: the implicit score from the next prompt and the session's commits, as for imports. A client sends what it knows through `feedback`; missing measures are missing, never zero. |
| 6 | Promotion | A challenger replaces the champion when both arms have at least 30 runs with outcomes, its completion rate is not lower, and either its completion rate is higher or its median tokens are at least 10% lower, each with a 95% bootstrap interval that excludes no difference. A challenger that loses on completion after 30 runs is retired. Promotion and retirement are written to the store with the counts that decided them. |
| 7 | Where templates come from | By hand; from the best-scored stored prompts of the kind (the retrieval code ranks them); later, written by a configured model under a cost cap. Each source is recorded on the version. |
| 8 | Live clients | For agent prompts, a `PreToolUse` hook calls `enhance` and replaces the call's prompt with the answer (`updatedInput`); `PostToolUse` sends the run measures as `feedback`; a background run's measures come from its transcript when it stops. For typed prompts a `UserPromptSubmit` hook cannot rewrite the prompt, so the template is sent as added context, and the trial is that context against none. Every failure path sends the original prompt. |
| 9 | Contract | Within v1, additively: `feedback.outcome` gains optional `completed`, `tokens`, `duration_ms`, `tool_calls`, `asked_again`. `enhance` already returns `template_id`, `template_version`, `held_out`, `task_kind`. |
| 10 | Report | `whetstone report` prints, per kind: runs per arm, completion, median tokens and duration, the champion's version and record, and any decision taken. It reads only. |

## Privacy

Agent-written prompts are stored and redacted like typed ones (docs/privacy.md). They often quote file contents and
tool output, so the same exclusions apply at capture: by folder, by the paths the run touched, and by text. Templates
are text the owner can export and forget like any row.

## Not in this design

Sharing templates between users, choosing models, and templates for kinds with fewer than 30 prompts a month (they
cannot finish a trial in a reasonable time).
