---
title: "Quickstart"
description: "Clone the repository, start the server, see a prompt stored with its secrets redacted, then export and delete it."
order: 1
section: "Get started"
---

You need the .NET SDK that [global.json](../../global.json) pins: 10.0.100, with roll-forward to a later feature band. whetstone has no package or release binary yet, so you run it from a clone.

## Run the server

```bash title="Over stdio, for a client that starts whetstone itself"
dotnet run --project src/Whetstone.Server -- mcp
```

```bash title="Over HTTP, for a client that connects"
WHETSTONE_API_KEY=choose-a-long-random-key dotnet run --project src/Whetstone.Server -- serve
```

`serve` listens on `127.0.0.1:7340`. MCP is at `/v1/mcp` and needs the header `Authorization: Bearer <WHETSTONE_API_KEY>`. `GET /health` needs no key. Without `WHETSTONE_API_KEY`, `serve` exits with code 2 ([Program.cs](../../src/Whetstone.Server/Program.cs)).

```bash title="Check that it answers"
curl http://127.0.0.1:7340/health
```

```json title="Response"
{"status":"ok","version":"0.1.0","store_failures":0}
```

`version` is the assembly version, set once in [Directory.Build.props](../../Directory.Build.props). `store_failures` counts requests whose storage failed since the process started ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs)).

> [!NOTE]
> How you register the server in your MCP client depends on the client. whetstone's side is the command above for stdio, or the URL and bearer header for HTTP.

## Call the tools

Your client sees two tools, `enhance` and `feedback`. An `enhance` call needs only a prompt:

```json title="enhance arguments"
{
  "prompt": "Explain why the retry test is flaky",
  "context": { "repository": "example/app", "task_kind": "explain" },
  "deadline_ms": 1500
}
```

You get the same prompt back with `changed: false`, a fresh `request_id`, and the reason `pass-through: nothing learned yet`. After the run, report what happened:

```json title="feedback arguments"
{
  "request_id": "req-...",
  "outcome": { "rewrite_accepted": false, "score": 0.8, "cost_usd": 0.12 }
}
```

The result is an empty object. Feedback for a `request_id` whose row is not in your store is accepted and stores nothing ([StoreServerTests.cs](../../tests/Whetstone.Tests/Storage/StoreServerTests.cs)). Every field is described in the [Reference](reference.md#the-enhance-tool).

## See what was stored

```bash title="Everything stored for you, as JSON lines"
dotnet run --project src/Whetstone.Server -- export
```

Each line is one call in the `export/v1` shape ([export.schema.json](../../schemas/export/v1/export.schema.json)). A secret in the prompt appears as `[REDACTED:kind]`; the answer your client received is not changed by redaction ([StoreServerTests.cs](../../tests/Whetstone.Tests/Storage/StoreServerTests.cs)).

## Delete it

```bash title="Count first, then delete"
dotnet run --project src/Whetstone.Server -- forget --repository example/app
dotnet run --project src/Whetstone.Server -- forget --repository example/app --confirm
```

Without `--confirm`, `forget` prints how many requests match and deletes nothing. Run `export` first if you want a copy.

> [!CAUTION]
> `forget --confirm` deletes the rows and overwrites their text in the file. There is no undo ([CommandTests.cs](../../tests/Whetstone.Tests/Storage/CommandTests.cs)).

## Where the data lives

A file per user: `<WHETSTONE_DATA_DIR>/<WHETSTONE_USER>/whetstone.db`. The defaults are your platform's local application data directory under `whetstone`, and the user `owner`. The directory is created with mode 0700 and the file with 0600 ([SqliteStores.cs](../../src/Whetstone/Storage/SqliteStores.cs), [StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs)). Both `mcp` and `serve` print the path to stderr when they start.
