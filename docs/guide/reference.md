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
| `serve [--listen ADDR] [--port N] [--allowed-hosts H1,H2]` | MCP over HTTP at `/v1/mcp`, and `GET /health` |
| `export [--repository R] [--before DATE]` | Everything stored for you, as JSON lines, oldest first |
| `forget (--all \| [--repository R] [--before DATE] [--text REGEX] [--imported]) [--confirm]` | Counts what matches; with `--confirm`, deletes it |
| `import claude-code DIR [--exclude DIR] [--exclude-text REGEX] [--confirm]` | Counts the prompts typed in past sessions under DIR; with `--confirm`, stores them with an implicit score |
| `replay [--min-chars N]` | For each scored request, the earlier one that best matches it, and how many rows are eligible; reads only |
| `report` | Per kind in a template trial: runs per arm, completion, median cost and duration, the split check and what the promotion rule says; reads only |
| `reindex` | Upgrades an older store file and rebuilds its search index |

### serve

| Option | Default | Notes |
|---|---|---|
| `--port` | `7340` | Not a number: exit 2 |
| `--listen` | `127.0.0.1` | An IP address. A non-loopback one needs allowed hosts |
| `--allowed-hosts` | `WHETSTONE_ALLOWED_HOSTS`, else none | Host names, separated by commas, that clients put in the URL, besides `localhost` and `127.0.0.1` |

> [!WARNING]
> A `--listen` address that is not loopback with no allowed hosts stops the process with an unhandled `InvalidOperationException`: beyond loopback the Host check is the only guard against DNS rebinding ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs), `Listening_beyond_loopback_needs_allowed_hosts` in [HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs)). A host entry without a port accepts any port, and the comparison ignores case (`A_host_is_accepted_only_when_allowed_with_or_without_its_port`); any other Host header gets 400.

### Container

The [Dockerfile](../../Dockerfile) builds an image that runs `serve --listen 0.0.0.0 --port 7340` as the non-root user `app` (uid 1654, gid 1654), with `WHETSTONE_DATA_DIR=/data` and `WHETSTONE_USER=owner`. Mount a volume at `/data` that uid 1654 can write, and set `WHETSTONE_API_KEY` and `WHETSTONE_ALLOWED_HOSTS` (the host names clients use, for example the name or address of the machine the container runs on).

```bash title="Run the image"
docker run -d --name whetstone -p 7340:7340 -v whetstone-data:/data \
  -e WHETSTONE_API_KEY=... -e WHETSTONE_ALLOWED_HOSTS=<host-clients-use> ghcr.io/egoushka/whetstone:<tag>
```

The other commands run in the same container with the image's entry point, `dotnet /app/Whetstone.Server.dll`:

```bash title="Commands inside the running container"
docker exec whetstone dotnet /app/Whetstone.Server.dll export > mine.jsonl
docker exec whetstone dotnet /app/Whetstone.Server.dll report
docker exec whetstone dotnet /app/Whetstone.Server.dll forget --all          # counts; add --confirm to delete
```

The image's health check opens `127.0.0.1:7340` with bash, because the runtime image has no `curl`, and expects `200` from `GET /health`. Images are published by [image.yml](../../.github/workflows/image.yml) as `ghcr.io/egoushka/whetstone:sha-<12 characters of the commit>` on every merge to `main`, and as `:<Version>` on a `v<Version>` tag; the run summary prints the digest to pin.

### report

Prints, for each kind that has a template or a request with an arm: the champion and challenger, runs by arm, the counted runs (those with a reported `completed`, since the model last changed) with completion, median cost, median duration, rework, median person time and the cache-read share (a share more than 10 points below the arm it is compared with is flagged), the sample-ratio check against the planned split, the regression monitor (champion against the held-out arm) and, for each look the challenger has reached (30, 60, 100, 150, 200 counted runs), what the promotion rule says. It decides nothing; applying a verdict is the owner's act ([Trials.cs](../../src/Whetstone/Templates/Trials.cs), [ReportTests.cs](../../tests/Whetstone.Tests/Templates/ReportTests.cs)). Cost is tokens weighted 1, 1.25, 0.1 and 5 for input, cache write, cache read and output, and is left out of a run missing any of the four counts.

### Templates and arms

With `WHETSTONE_TEMPLATES=on`, `serve` and `mcp` seed the shipped templates, the agent brief completer: for subagent kinds a report format, a stop rule with a 300-word cap and, for read-only kinds (`agent/explore`, `agent/plan`, `agent/claude-code-guide`, `agent/box-reader`, `agent/general-purpose/explore`, `agent/general-purpose/research`), a read-only line ([BuiltInTemplates.cs](../../src/Whetstone/Templates/BuiltInTemplates.cs)). A request that sends `context.task_kind` of such a kind gets an arm from a hash of the kind and the request id: champion 90% and held out 10%, or 60, 30 and 10 while a challenger is on trial. The champion's text is added after the prompt, which stays byte for byte; the held-out arm gets the prompt unchanged and `held_out: true`. A kind with no champion is held out entirely; a request with no kind or kind `other` is not an arm. The arm is stored with the request and appears as `answer.arm` in `export`. Templates live in the store's `templates` table, one champion and one challenger per kind at most; the owner changes them with `whetstone template list|add|promote|retire` (add stores a challenger, or a champion with `--champion` when the kind has none; promote swaps the challenger in and retires the old champion; retire takes a version out of service; retired rows stay). The command writes no decision log yet: read the counts with `whetstone report` first. A failing template lookup returns the prompt unchanged and counts as `template_failures` in `/health`.

### export and forget

`--before` takes a date or time such as `2026-10-01` or `2026-10-01T12:00:00Z`; a value without an offset is UTC. `--repository` matches the stored repository exactly. `--text` is a regular expression matched against the stored (redacted) prompt, ignoring case, in linear time. The filters combine for both commands. `forget` accepts any mix of filters, but `--all` cannot be combined with a filter, and a `forget` that names nothing is refused. Each option may appear once and a valued option needs a value ([Commands.cs](../../src/Whetstone.Server/Commands.cs)).

`forget` without `--confirm` prints `N requests match. Nothing was deleted.`. With `--confirm` it prints `Deleted N requests.`.

### import

Reads every `*.jsonl` file under DIR, at any depth, as Claude Code session transcripts, and keeps what a person typed. Each prompt is scored by the next one in its session and by whether its repository got a commit from the configured git user during the session; the rules, the duplicates and the ids are in [the design](../specs/2026-10-09-it-imports-design.md). `--exclude` drops sessions that started under a folder or whose tools worked in it; `--exclude-text` drops sessions with any prompt that matches. Without `--confirm` it prints `N sessions read, M excluded; P typed prompts: X new, Y already stored by a live client, Z imported before.` and stores nothing.

It also stores the prompts agents wrote, counted on a second line (`N prompts written by agents and orchestrators: ...`): the `prompt` of each `Agent` call (client `claude-code/agent`), of each `WebFetch` call (`claude-code/fetch`, kind `fetch/extract`), and each turn an orchestrator sent into a session (turn origin `sdk`, client `claude-code/sdk`, kind `orchestrator/turn`). They get no score; the run measures come from the call's result in the transcript: completed, duration, tool calls, tokens, model. A background launch has no result yet, so only its model is kept; token counts are those of the run's final request, as the client records them. The kind of an `Agent` prompt is `agent/<subagent type>`, and for the general-purpose agent `agent/general-purpose/<job>` from the first verb of the call's description (`explore`, `research`, `review`, `implement`, `ship`) or `agent/general-purpose` when no verb matches ([PromptKinds.cs](../../src/Whetstone/Kinds/PromptKinds.cs)). The same `--exclude` and `--exclude-text` rules apply to them: a session left out keeps its agent prompts out.

### outcomes

`whetstone outcomes collect --root DIR [--forge github] [--confirm]` fills delayed outcomes for stored requests that have a `session_id` and a `repository` ([the design](../specs/2026-10-19-reward-signals-design.md), decision 1). A request owns the commits its repository's configured user made, on any ref, between it and the next request of the same session; the last request of a session owns two hours, and a request whose window has not closed yet is left for a later run. The checkout is the folder named like the stored repository under DIR or one level below. It sets `commits`; `reverted` when a later commit says "This reverts commit" for one of them, or `false` once 7 days have passed without one; and with `--forge github`, through the `gh` client, `pr_merged` and `ci_first_push_green` (the checks on the pull request's first commit). It counts until `--confirm`, then stores through the store like `feedback`, so running it again only adds what is newly known. Two sessions in one repository at once both own the commits made while they overlap. `item_closed` is left to clients that know the tracker.

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
| `WHETSTONE_ALLOWED_HOSTS` | none | `serve`: the `--allowed-hosts` list when the option is absent |
| `WHETSTONE_USER` | `owner` | every command; 1 to 64 of `a-z`, `0-9`, `_`, `-` |
| `WHETSTONE_DATA_DIR` | `whetstone` under the local application data directory | every command |
| `WHETSTONE_TEMPLATES` | off | `serve` and `mcp`: `on` seeds the shipped templates and serves them (see "Templates and arms" below); anything else leaves every answer as it was |
| `WHETSTONE_COST_WEIGHTS` | `1,1.25,0.1,5` | `report`: weights of input, cache write, cache read and output tokens in a run's cost; four numbers of 0 or more, or `report` exits 2 |
| `WHETSTONE_PERSON_TIME_KINDS` | `typed/` | `report`: comma-separated kind prefixes whose cost-only decisions compare the person's time (`person_ms`) instead of run cost |
| `WHETSTONE_RETRIEVAL_MIN_CHARS` | `0` | `serve` and `mcp` with retrieval on: prompts shorter than this are neither answered with a retrieval nor retrieved; read it off `replay --min-chars` |

The store is `<WHETSTONE_DATA_DIR>/<WHETSTONE_USER>/whetstone.db`. `export` and `forget` for a user with no store print nothing or delete nothing, and create no file ([CommandTests.cs](../../tests/Whetstone.Tests/Storage/CommandTests.cs)).

## HTTP routes

| Route | Auth | Answer |
|---|---|---|
| `POST /v1/mcp` and the rest of the MCP transport | `Authorization: Bearer <key>` | The MCP session |
| `GET /health` | none | `status`, `version`, `store_failures`, `retrieval_failures`, `template_failures`, `uptime_seconds`, `cpu_seconds`, `threads`, `heap_mb`, `committed_mb`, `gen2_collections` ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs)) |

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
| `context.session_id` | string | Optional; 1 to 128 characters, opaque; requests that share it share a session |
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
| `outcome.completed` | boolean or null | The run the prompt started finished and did not fail |
| `outcome.tokens_in`, `tokens_out`, `cache_read_tokens`, `cache_write_tokens` | integer or null | 0 or more; input is apart from cache reads and writes, as billed |
| `outcome.duration_ms`, `outcome.tool_calls` | integer or null | 0 or more |
| `outcome.asked_again` | boolean or null | The author repeated or corrected the request in the same turn |
| `outcome.effort` | string or null | The effort the run used; redacted before storage |
| `outcome.commits` | integer or null | 0 or more; commits the request's work made |
| `outcome.pr_merged`, `ci_first_push_green`, `reverted`, `item_closed` | boolean or null | A pull request with that work merged; CI passed on its first push; a commit of it reverted within 7 days; the tracker item closed |
| `outcome.correction_reason` | one of `wrong_target`, `incomplete`, `wrong_approach`, `misread`, `too_much`, `style`, `new_info`, `stop`, `other`, or null | Why the next prompt corrected the run |
| `outcome.review_findings`, `review_findings_severe` | integer or null | 0 or more; problems a reviewer found in the produced change, and those marked blocking |
| `outcome.person_ms` | integer or null | 0 or more; the person's time on the request |

The run measures (`completed` to `effort`) came with schema version 3 of the store and are optional within `feedback/v1`. A measure left out is stored as missing, never zero, and a later report for the same request adds the measures it has without erasing one an earlier report gave ([StoreTests.cs](../../tests/Whetstone.Tests/Storage/StoreTests.cs)). `export` writes them inside `outcome`, and leaves out the ones nobody reported.

The reward measures (`commits` to `person_ms`, [the design](../specs/2026-10-19-reward-signals-design.md)) and `context.session_id` came with schema version 5. They often arrive later than the run measures, in a report of their own, so since version 5 no field of a report erases what an earlier report gave, the first five included: a later value replaces an earlier one, a missing value leaves it. `import claude-code` fills `session_id` with a hash of the transcript's session id, never the id itself.

When a request with a `session_id` reads as a correction of the session's previous request, whetstone sets that request's `correction_reason`, and `asked_again` when nobody reported it, by rules over the new prompt's opening in English and Ukrainian ([Corrections.cs](../../src/Whetstone/Kinds/Corrections.cs)). A reason the client reported wins. Words such as "only", "instead" or "missing" name a reason only in a prompt that already reads as a correction ("no, only the test"), so "just run the tests" stays a new request.

A score outside 0 to 1 is an error ([HttpServerTests.cs](../../tests/Whetstone.Tests/HttpServerTests.cs)).

## Schemas

| Schema | Describes | Examples |
|---|---|---|
| `enhance/v1` | The enhance request and response | [examples](../../schemas/enhance/v1/examples/valid-rewrite.json) |
| `feedback/v1` | The feedback request | [examples](../../schemas/feedback/v1/examples/valid-accepted.json) |
| `export/v1` | One line of `export` | [examples](../../schemas/export/v1/examples/valid-with-outcome.json) |

Within a major version, changes are additive ([CLAUDE.md](../../CLAUDE.md)). CI validates every `valid-*` example and checks that every `invalid-*` one fails ([check-schemas.py](../../scripts/check-schemas.py)).
