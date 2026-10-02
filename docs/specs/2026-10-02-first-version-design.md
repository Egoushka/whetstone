# whetstone first version: the contract, a pass-through, and the chargehand seam

- Status: draft for the maintainer's review. The direction (a separate tool that owns each user's prompts and returns
  a better prompt, connected to chargehand) was set by the maintainer on 2026-10-02.
- Date: 2026-10-02

## Goal

A client can call whetstone's `enhance` and `feedback` over MCP, gets an answer inside a latency budget, and is never
worse off for having called it. This is goal 0.1; it fixes the contract before any learning is built, so goals
0.2 to 0.6 change what is inside, not the seam.

Done when: (1) `schemas/enhance/v1` and `schemas/feedback/v1` validate their examples in CI; (2) an MCP server exposes
both tools and returns the prompt unchanged with `template_id: null`; (3) chargehand's `prompt-enhancer` extension
calls it in a test, shows no diff for a pass-through, and sends the original when whetstone times out or errors.

## Where it stands

1. **chargehand plans the seam** as an extension category `prompt-enhancer` with `enhance` and `feedback` calls
   (chargehand spec `docs/specs/2026-10-02-a-client-of-your-own-design.md`, decision 7; chargehand PR #158, open).
2. **chargehand already measures the outcomes** whetstone learns from: per-run score, cost and model in its run log and
   routing report (chargehand ADR 0019), and a model override would be a new field it records.
3. **The owner already has memory services** that hold past sessions: Hindsight (session retain job) and chronicle
   (memory over the chat archive). Whether whetstone stores its own prompt history or reads it from one of them is
   decision 4.
4. **No code, no storage, no deployment exist.**

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Transport | MCP over streamable HTTP, plus stdio for local use. Same tools on both. |
| 2 | Stack | .NET 10 and the official MCP C# SDK, like chargehand, so its contract types and test helpers can be reused. Proposed in ADR 0002; reopen if the retrieval goal (0.3) needs a library that only exists in Python. |
| 3 | Never block | The contract carries a `deadline_ms` the client sets (default 1500). whetstone answers inside it or the client sends the original. A pass-through is a valid answer, not an error. |
| 4 | Where history lives | whetstone keeps its own store (SQLite, one file per user) for prompts, outcomes and templates. Memory services are an import source (goal 0.7), not the store: they hold sessions, not the prompt-outcome pairs whetstone needs, and outcomes must join to template versions. |
| 5 | Untrusted output | A rewrite is text the client shows and the user accepts. The contract has no field for tool calls, system prompts or model choice; `task_kind` is a hint. A client must treat the rewrite as user input, never as instructions to itself. |
| 6 | Identity | One store per user. On HTTP the caller's bearer token selects the store; there is no cross-user query. |
| 7 | What is sent | The prompt, and only the context the user allowed: repository name, commit, task kind, client name. Never file contents, secrets or other runs. Secrets in the prompt are redacted before storage (goal 0.2). |
| 8 | Measuring help | From goal 0.4, a configurable share of requests (default 10%) is held out: whetstone records them but returns a pass-through, so outcomes with and without rewrites can be compared (docs/evaluation.md). |

## The contract

`enhance` request: `prompt` (string), `context` (`repository`, `commit`, `task_kind`, `client`, all optional),
`deadline_ms` (integer). Response: `prompt` (string), `changed` (boolean), `template_id` (string or null),
`template_version` (string or null), `reason` (string), `task_kind` (string or null), `request_id` (string),
`held_out` (boolean).

`feedback` request: `request_id`, `outcome`: `rewrite_accepted` (boolean or null), `model_overridden` (boolean or
null), `score` (number 0 to 1 or null), `cost_usd` (number or null), `model` (string or null). Response: empty.

Schemas: [`schemas/enhance/v1`](../../schemas/enhance/v1), [`schemas/feedback/v1`](../../schemas/feedback/v1).
Additive changes only within a major.

## Tasks (each one pull request)

1. Schema validation in CI: every example under `schemas/*/v1/examples` validates (`valid-*`) or fails (`invalid-*`).
2. Solution skeleton, `scripts/check.sh` (format, build with warnings as errors, tests), CI running it.
3. MCP server with both tools as a pass-through, stdio and HTTP, deadline honoured, a health route.
4. chargehand side (in chargehand's repository): the `prompt-enhancer` extension with a fake enhancer, timeout and
   error fall back to the original.
5. Run it locally as a launchd agent and connect chargehand's local profile to it.

## Out of scope for 0.1

Storage, retrieval, templates, model calls, import, a UI of its own.
