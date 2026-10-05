---
title: "Architecture"
description: "The projects in the solution, how an enhance call is answered and stored within its deadline, and why failures never reach the client."
order: 2
section: "Concepts"
---

## The parts

| Project | Holds |
|---|---|
| `src/Whetstone` | Contract types, embedded schemas, `IEnhancer`, `PassThrough`, the redactor, the SQLite store |
| `src/Whetstone.Server` | The executable: `mcp`, `serve`, `export`, `forget` and the two MCP tools |
| `tests/Whetstone.Tests` | Tests for both |

This is the layout in [CLAUDE.md](../../CLAUDE.md) and [Whetstone.slnx](../../Whetstone.slnx). The one implementation of `IEnhancer` is `PassThrough` ([PassThrough.cs](../../src/Whetstone/PassThrough.cs)); a rewriting enhancer is the work of goals 0.3 to 0.6.

## One enhance call

1. The arguments are validated against the embedded `enhance/v1` schema. An invalid request is the only error `enhance` returns, and it stores nothing ([StoreServerTests.cs](../../tests/Whetstone.Tests/Storage/StoreServerTests.cs)).
2. The enhancer runs with 80% of `deadline_ms` (`Tools.AnswerShare`). If it is late, blocks, ignores cancellation or throws, the answer is the original prompt with the reason `pass-through: deadline reached` or `pass-through: enhancer failed` ([DeadlineTests.cs](../../tests/Whetstone.Tests/DeadlineTests.cs)).
3. The call is stored with up to 15% of `deadline_ms` (`Tools.StoreShare`). That leaves 5% for the way back to the client ([Tools.cs](../../src/Whetstone.Server/Tools.cs)).
4. The client gets the answer, structured and as text.

Storage runs after the answer exists. A failing store, a store that blocks, and a data directory that cannot be written change no answer; each failure is counted in `/health` as `store_failures` and logged by exception type only, because a message could quote a prompt ([StoreServerTests.cs](../../tests/Whetstone.Tests/Storage/StoreServerTests.cs), [GuardedStoreTests.cs](../../tests/Whetstone.Tests/Storage/GuardedStoreTests.cs)).

## One feedback call

`feedback` validates against `feedback/v1`, then fills the outcome of the row with that `request_id`. It has a fixed one-second budget instead of a deadline. A second report for the same request replaces the first: the last one wins ([StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs)).

## Redact, then store

The prompt, every context field and a reported model name pass through `Redactor` before the write. The text is cut at 32,000 characters after redaction, so a secret on the cut cannot leak a piece of itself. The rewrite is not stored, only the prompt that was sent ([Redactor.cs](../../src/Whetstone/Redaction/Redactor.cs), [SqliteStores.cs](../../src/Whetstone/Storage/SqliteStores.cs), [StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs)). [Privacy and data](privacy.md) lists what the redactor catches and keeps.

## One store per user

The code reaches a store only through `IStores.ForUser(user)`, and the path carries the user id. One key means one user: `WHETSTONE_USER`, default `owner`. A user id is 1 to 64 characters of `a-z`, `0-9`, `_` and `-`. There is no key map; a second user on one server is not a goal ([ADR 0003](../adr/0003-memory-store.md)). `Two_users_never_share_a_file` in [StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs) covers the isolation.

A store written by a newer whetstone (SQLite `user_version` above the one this build knows) is refused, not changed ([StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs)).

## Transports

`mcp` serves one session over stdio and has no key, since it has no listener. `serve` uses streamable HTTP at `/v1/mcp` with a bearer key compared in fixed time, a host filter against DNS rebinding, and a request body limit of 1,000,000 bytes ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs), [HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs)). Both transports send the same server instructions, which tell a client to show the returned prompt as a diff and to fall back to the original ([StdioServerTests.cs](../../tests/Whetstone.Tests/StdioServerTests.cs)).
