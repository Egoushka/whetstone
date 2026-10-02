# 0002. Stack: .NET 10, the MCP C# SDK, SQLite

- Status: proposed
- Date: 2026-10-02

## Context

The first client, chargehand, is .NET 10 with the official MCP C# SDK and JSON-schema contracts. Goal 0.3 needs text
similarity over a few thousand prompts per user; goal 0.6 needs one model call.

## Options

1. .NET 10, MCP C# SDK, SQLite (one file per user). Same toolchain, analyzers and CI as chargehand; contract types
   shareable. Similarity search needs either SQLite's FTS5 (lexical) or a small embedding call.
2. Python, the MCP Python SDK, SQLite. More retrieval libraries. A second toolchain to keep up.

## Decision

Option 1, proposed. Start with FTS5 for 0.3; add embeddings only if lexical retrieval misses the acceptance bar
(docs/evaluation.md).

## Consequences

- Shared engineering standards (analyzers, Sonar, CI templates) apply as in chargehand.

## Reopen if

Retrieval quality needs a library that has no maintained .NET equivalent.
