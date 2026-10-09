---
title: "Reference"
description: "Every command, option, environment variable, MCP tool field, HTTP route and exit code of whetstone, with defaults."
order: 4
section: "Reference"
---

## Commands

Run from a clone as `dotnet run --project src/Whetstone.Server -- <command>`. Any other first argument prints the usage text and exits with 2 ([Program.cs](../../src/Whetstone.Server/Program.cs)).

| Command | What it does |
|---|---|
| `mcp` | The MCP tools over stdio, for a client that starts whetstone itself |
| `serve [--listen ADDR] [--port N]` | MCP over HTTP at `/v1/mcp`, and `GET /health` |
| `export [--repository R] [--before DATE]` | Everything stored for you, as JSON lines, oldest first |
| `forget (--all \| [--repository R] [--before DATE] [--text REGEX] [--imported]) [--confirm]` | Counts what matches; with `--confirm`, deletes it |
| `import claude-code DIR [--exclude DIR] [--exclude-text REGEX] [--confirm]` | Counts the prompts typed in past sessions under DIR; with `--confirm`, stores them with an implicit score |
| `replay [--min-chars N]` | For each scored request, the earlier one that best matches it, and how many rows are eligible; reads only |
| `reindex` | Upgrades an older store file and rebuilds its search index |

### serve

| Option | Default | Notes |
|---|---|---|
| `--port` | `7340` | Not a number: exit 2 |
| `--listen` | `127.0.0.1` | Only loopback starts |

> [!WARNING]
> A `--listen` address that is not loopback stops the process with an unhandled `InvalidOperationException`, because the command line has no way to set the allowed host names that the server then requires ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs), `Listening_beyond_loopback_needs_allowed_hosts` in [HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs)). Reach it from another host through a tunnel to the loopback port.

### export and forget

`--before` takes a date or time such as `2026-10-01` or `2026-10-01T12:00:00Z`; a value without an offset is UTC. `--repository` matches the stored repository exactly. `--text` is a regular expression matched against the stored (redacted) prompt, ignoring case, in linear time. The filters combine for both commands. `forget` accepts any mix of filters, but `--all` cannot be combined with a filter, and a `forget` that names nothing is refused. Each option may appear once and a valued option needs a value ([Commands.cs](../../src/Whetstone.Server/Commands.cs)).

`forget` without `--confirm` prints `N requests match. Nothing was deleted.`. With `--confirm` it prints `Deleted N requests.`.

### import

Reads every `*.jsonl` file under DIR, at any depth, as Claude Code session transcripts, and keeps what a person typed. Each prompt is scored by the next one in its session and by whether its repository got a commit from the configured git user during the session; the rules, the duplicates and the ids are in [the design](../specs/2026-10-09-it-imports-design.md). `--exclude` drops sessions that started under a folder or whose tools worked in it; `--exclude-text` drops sessions with any prompt that matches. Without `--confirm` it prints `N sessions read, M excluded; P typed prompts: X new, Y already stored by a live client, Z imported before.` and stores nothing.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Done |
| 1 | `export`, `forget` or `import` failed on the store (a newer store version, a SQLite or I/O error) |
| 2 | Usage error, a bad `WHETSTONE_USER`, a missing `WHETSTONE_API_KEY`, or a bad `--port` |

## Environment variables

| Variable | Default | Used by |
|---|---|---|
| `WHETSTONE_API_KEY` | none | `serve`; required |
| `WHETSTONE_USER` | `owner` | every command; 1 to 64 of `a-z`, `0-9`, `_`, `-` |
| `WHETSTONE_DATA_DIR` | `whetstone` under the local application data directory | every command |
| `WHETSTONE_RETRIEVAL_MIN_CHARS` | `0` | `serve` and `mcp` with retrieval on: prompts shorter than this are neither answered with a retrieval nor retrieved; read it off `replay --min-chars` |

The store is `<WHETSTONE_DATA_DIR>/<WHETSTONE_USER>/whetstone.db`. `export` and `forget` for a user with no store print nothing or delete nothing, and create no file ([CommandTests.cs](../../tests/Whetstone.Tests/Storage/CommandTests.cs)).

## HTTP routes

| Route | Auth | Answer |
|---|---|---|
| `POST /v1/mcp` and the rest of the MCP transport | `Authorization: Bearer <key>` | The MCP session |
| `GET /health` | none | `status`, `version`, `store_failures`, `retrieval_failures`, `uptime_seconds`, `cpu_seconds`, `threads`, `heap_mb`, `committed_mb`, `gen2_collections` ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs)) |

A missing or wrong key gets 401 ([HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs)). Request bodies over 1,000,000 bytes are refused.

## The enhance tool

The arguments are the `request` part of [enhance.schema.json](../../schemas/enhance/v1/enhance.schema.json). Unknown members are rejected.

| Field | Type | Notes |
|---|---|---|
| `prompt` | string | Required, not empty |
| `context.repository` | string | Optional |
| `context.commit` | string | Optional; 7 to 40 lowercase hex |
| `context.task_kind` | string | Optional |
| `context.client` | string | Optional |
| `deadline_ms` | integer | 50 to 30000; default 1500 |

The result has structured content and the prompt as text:

| Field | Type | Today |
|---|---|---|
| `prompt` | string | The prompt you sent |
| `changed` | boolean | Always `false` |
| `template_id`, `template_version` | string or null | Always null |
| `reason` | string | See below |
| `task_kind` | string or null | Always null |
| `request_id` | string | `req-` and 32 hex digits, new per call |
| `held_out` | boolean | Always `false` |

The reasons that exist are `pass-through: nothing learned yet`, `pass-through: deadline reached` and `pass-through: enhancer failed` ([PassThrough.cs](../../src/Whetstone/PassThrough.cs), [Tools.cs](../../src/Whetstone.Server/Tools.cs)). `held_out` is part of the contract for goal 0.4; nothing sets it yet.

An invalid request returns an MCP error result starting `invalid enhance/v1:` and names the fault.

## The feedback tool

The arguments are [feedback.schema.json](../../schemas/feedback/v1/feedback.schema.json). The result is an empty object.

| Field | Type | Notes |
|---|---|---|
| `request_id` | string | Required; from `enhance` |
| `outcome.rewrite_accepted` | boolean or null | Optional |
| `outcome.model_overridden` | boolean or null | Optional |
| `outcome.score` | number or null | 0 to 1 |
| `outcome.cost_usd` | number or null | 0 or more |
| `outcome.model` | string or null | Redacted before storage |

A score outside 0 to 1 is an error ([HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs)).

## Schemas

| Schema | Describes | Examples |
|---|---|---|
| `enhance/v1` | The enhance request and response | [examples](../../schemas/enhance/v1/examples/valid-rewrite.json) |
| `feedback/v1` | The feedback request | [examples](../../schemas/feedback/v1/examples/valid-accepted.json) |
| `export/v1` | One line of `export` | [examples](../../schemas/export/v1/examples/valid-with-outcome.json) |

Within a major version, changes are additive ([CLAUDE.md](../../CLAUDE.md)). CI validates every `valid-*` example and checks that every `invalid-*` one fails ([check-schemas.py](../../scripts/check-schemas.py)).
