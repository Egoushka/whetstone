---
title: "Decisions"
description: "The architecture decision records of whetstone, one line each, with the condition under which each would be reopened."
order: 6
section: "Project"
---

Each decision is a file in [docs/adr/](../adr/), written from [template.md](../adr/template.md).

| ADR | Decision | Status |
|---|---|---|
| [0001](../adr/0001-a-separate-tool-behind-an-mcp-seam.md) | whetstone is its own service behind two MCP calls, not part of chargehand | accepted |
| [0002](../adr/0002-stack.md) | .NET 10, the MCP C# SDK and SQLite; full-text search first, embeddings only if it misses the bar | accepted |
| [0003](../adr/0003-memory-store.md) | One SQLite file per user, redact before storing, `export` and `forget` as commands | accepted |
| [0004](../adr/0004-every-prompt-templates-and-trials.md) | Every prompt, any author; versioned templates per kind and trials with a held-out arm; retrieval only proposes challengers | proposed |

## Why each one matters to you

**0001** means any MCP client can use whetstone and your prompts stay out of chargehand's repository. The cost is a second thing to run. Reopen it if no client other than chargehand uses it after 1.0.

**0002** keeps the toolchain the same as chargehand's. Reopen it if retrieval needs a library with no maintained .NET equivalent.

**0003** is why `export` and `forget` are not MCP tools: an agent with whetstone in its tool list must not be able to read or delete your store. It also means a redaction miss stays on disk until `forget` removes it. Reopen it if a second person needs the same server, or if redaction misses reach the owner's own export twice.

**0004** follows a replay of retrieval over real data: matches shared a topic but would not have improved the prompt, while agent-written prompts repeat by kind and carry measurable cost. Reopen it if 50 trials of one agent kind produce no winning challenger on any measure.

Specs sit beside the ADRs: [first version](../specs/2026-10-02-first-version-design.md) and [it remembers](../specs/2026-10-05-it-remembers-design.md).
