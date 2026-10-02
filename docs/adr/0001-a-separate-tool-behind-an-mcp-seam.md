# 0001. A separate tool behind an MCP seam

- Status: accepted
- Date: 2026-10-02

## Context

chargehand's client plan wants prompts improved before they are sent and learned from what the user does. The
maintainer decided that a user's prompts belong to the user and do not live in chargehand: chargehand's own prompts
(worker and review prompts, gated by its Prompt CI) are a different thing.

## Options

1. Inside chargehand: one process, direct access to the run log. Mixes the user's data into a public tool's
   repository and scope, and only chargehand could use it.
2. A separate tool behind an MCP seam: any client can use it, its data and learning stay out of chargehand, and
   chargehand only defines an extension point. Costs a second deployable.

## Decision

Option 2. whetstone is its own repository and service. chargehand reaches it through its `prompt-enhancer` extension
category with two calls, `enhance` and `feedback` (schemas in `schemas/`).

## Consequences

- The contract is the only coupling; it is versioned and additive within a major.
- chargehand must send `feedback`, or whetstone cannot learn.
- A second service to run, update and back up.

## Reopen if

No client other than chargehand uses it after 1.0, and running it separately costs more than it saves.
