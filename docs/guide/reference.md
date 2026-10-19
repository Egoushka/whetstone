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
| `import (claude-code DIR \| opencode DIR-OR-FILE) [--exclude DIR] [--exclude-text REGEX] [--confirm]` | Counts the prompts typed in past sessions under DIR; with `--confirm`, stores them with an implicit score |
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

With `WHETSTONE_TEMPLATES=on`, `serve` and `mcp` seed the shipped templates, the agent brief completer: for subagent kinds a report format, a stop rule with a 300-word cap and, for read-only kinds (`agent/explore`, `agent/plan`, `agent/claude-code-guide`, `agent/box-reader`, `agent/general-purpose/explore`, `agent/general-purpose/research`), a read-only line ([BuiltInTemplates.cs](../../src/Whetstone/Templates/BuiltInTemplates.cs)). A request that sends `context.task_kind` of such a kind gets an arm from a hash of the kind and the request id: champion 90% and held out 10%, or 60, 30 and 10 while a challenger is on trial. A kind may hold up to five challengers (`template add` stores each); with two or more, held out stays a fixed 10% and the rest is split by Thompson sampling on completion (rework counts as not completed) with a 15% floor per arm, re-read once a minute, and `report` judges each challenger alone at the same fixed looks. The sample-ratio check then tests the held-out share only. Store schema version 7 allows several challengers. The champion's text is added after the prompt, which stays byte for byte; the held-out arm gets the prompt unchanged and `held_out: true`. A kind with no champion is held out entirely; a request with no kind or kind `other` is not an arm. The arm is stored with the request and appears as `answer.arm` in `export`. Templates live in the store's `templates` table, one champion and up to five challengers per kind; the owner changes them with `whetstone template list|export|add|promote|retire` (add stores a challenger, or a champion with `--champion` when the kind has none; promote swaps the challenger in and retires the old champion; retire takes a version out of service; retired rows stay). A template with `--source filler-strip` (it may have no text of its own, or the champion's framing to isolate the effect) also deletes the effort-only phrases of [FillerStripper.cs](../../src/Whetstone/Templates/FillerStripper.cs) ("be thorough", "double-check everything", ...) from the prompt; code spans stay, a phrase that goes on into more words stays, and the answer's `reason` lists what was removed. `whetstone trait list|add|retire` gives a kind up to four traits: named paragraphs (`--text`) that each request of the kind draws on or off alone, by hash, with the paragraph added after the template when on (a factorial trial; held-out requests get none). The draw is stored (store schema version 9) and exported as `answer.traits`, and `report` prints each trait's completion on and off pooled over the kinds that carry it, with counts per kind; a trait that wins enters a champion by a new template version. `template write --kind K [--confirm]` asks the configured model for the kind's next challenger: it reads the champion's text, the newest eight runs of the kind that ended in rework with their correction reasons (stored, redacted text, cut at 400 characters) and the nearest retired texts, and everything sent is redacted once more. Without `--confirm` it prints what it would send and the most the call could cost, and sends nothing; it refuses a call that could pass the per-call limit or, with the last 7 days' spend (store schema version 10, table `writer_spend`, no text), the weekly limit. The model's text is stored as a challenger with source `model-written` only if it is non-empty, at most 1,200 characters, uses only the known slots and is not close to the champion or to a retired text; the runs it read and the model are in the decision log. The call is paid for whatever the text turns out to be. `template export` prints every version, retired ones too, with its decisions, as `library/v1` lines: the negative library. `add` refuses a text that BM25 over the kind's retired texts finds at least 0.8 as close as a retired one (both directions), unless `--allow-similar` is given. Each change writes a row to `template_decisions` (action, the report's counts as JSON, and `--note`) in the same transaction. A failing template lookup returns the prompt unchanged and counts as `template_failures` in `/health`.

### export and forget

`--before` takes a date or time such as `2026-10-01` or `2026-10-01T12:00:00Z`; a value without an offset is UTC. `--repository` matches the stored repository exactly. `--text` is a regular expression matched against the stored (redacted) prompt, ignoring case, in linear time. The filters combine for both commands. `forget` accepts any mix of filters, but `--all` cannot be combined with a filter, and a `forget` that names nothing is refused. Each option may appear once and a valued option needs a value ([Commands.cs](../../src/Whetstone.Server/Commands.cs)).

`forget` without `--confirm` prints `N requests match. Nothing was deleted.`. With `--confirm` it prints `Deleted N requests.`.

### import

Reads every `*.jsonl` file under DIR, at any depth, as Claude Code session transcripts, and keeps what a person typed. Each prompt is scored by the next one in its session and by whether its repository got a commit from the configured git user during the session; the rules, the duplicates and the ids are in [the design](../specs/2026-10-09-it-imports-design.md). `--exclude` drops sessions that started under a folder or whose tools worked in it; `--exclude-text` drops sessions with any prompt that matches. Without `--confirm` it prints `N sessions read, M excluded; P typed prompts: X new, Y already stored by a live client, Z imported before.` and stores nothing.

`import opencode` reads OpenCode's session database (`opencode.db`, given as the file or the folder that holds it, usually `~/.local/share/opencode`) the same way: the text parts of each top-level session's user messages, minus synthetic and ignored parts, commands and text the client generated; sessions a session started (subagents) are not read. Stored under client `opencode` with a hashed session id and the same redaction, scoring and exclusions (`--exclude` also matches the paths the session's tools were given). An import from a memory service is not built.

It also stores the prompts agents wrote, counted on a second line (`N prompts written by agents and orchestrators: ...`): the `prompt` of each `Agent` call (client `claude-code/agent`), of each `WebFetch` call (`claude-code/fetch`, kind `fetch/extract`), and each turn an orchestrator sent into a session (turn origin `sdk`, client `claude-code/sdk`, kind `orchestrator/turn`). They get no score; the run measures come from the call's result in the transcript: completed, duration, tool calls, tokens, model. A background launch has no result yet, so only its model is kept; token counts are those of the run's final request, as the client records them. The kind of an `Agent` prompt is `agent/<subagent type>`, and for the general-purpose agent `agent/general-purpose/<job>` from the first verb of the call's description (`explore`, `research`, `review`, `implement`, `ship`) or `agent/general-purpose` when no verb matches ([PromptKinds.cs](../../src/Whetstone/Kinds/PromptKinds.cs)). The same `--exclude` and `--exclude-text` rules apply to them: a session left out keeps its agent prompts out.

### Context slots (goal 0.9)

A template may use `{{hint_<name>}}`, filled from `context.hints` (for example the failing test path from the last CI run, which the client knows and whetstone does not fetch), and `{{memory}}`, filled from the configured memory service: up to ten memories recalled for the prompt and the repository within 0.9 s, one per source document, weighted by how runs that were shown that document went, and the top five kept, inside a `<whetstone_memory>` marker a session-retain job can strip. A document's weight moves only after 5 exposures, and one that helped in fewer than 1 in 5 of 10 or more runs is dropped. The record is whetstone's own (`memory_exposures`: request, document, memory id, a hash of the text); nothing is written to the memory service. A slot with no value drops its line, so a late or failed recall serves the template without it. Whether the slot helps is a trial like any other: the same template with and without it.

With `context.ablation` set, `report` ends with an ablations section: per held-out rule or skill version, sessions, runs, completion and rework beside sessions that held nothing out, and "a removal candidate" when after 100 sessions completion and rework differ by less than 5 points. The client applies the ablation; whetstone only compares.

### Levels and routing (goal 0.10)

A request with `context.level` is served by the champion stored for `<kind>@<level>` when there is one (add it with `whetstone template add --kind agent/explore@cheap ...`), and is stored under that kind, so each level runs its own trial; with no such champion the kind's own templates serve it. `whetstone report --routing` prints, per kind (levels folded together), each model's counted runs, completion, rework and median cost, marks the best completion, and names the cheapest model whose completion is shown no worse than the best's by 15 points (z 2.2, 30 runs each) as a routing candidate. `report --effort` does the same per effort. They suggest; routing changes by the owner's hand in the gateway's configuration, outside whetstone.

### For the person (goal 0.11)

Every `enhance` answer for a kind with at least 30 measured runs may carry `advice`: how many of the kind's last 200 runs needed rework and the most common reason, for the client to show before the prompt goes out (the Claude Code mod logs it). It never blocks or delays the answer; it is read at most every ten minutes per kind.

The shorthand expander is a model challenger, `typed-expand` version `e1`: the mod sends a typed prompt of at most 100 characters to a small model that writes a brief of at most four lines from what the prompt already says, and sends the prompt unchanged with the brief under it. It is not seeded; to try it on a typed kind, `whetstone template add --kind typed/debug --id typed-expand --version e1 --source model` (a model template needs no text).

`whetstone report --person` prints the last four ISO weeks of typed requests: your time in minutes (from `person_ms`), the three kinds that took most of it, and rework by reason.

State-aware templates (the spec's decision 4) are not built: they wait for the owner's privacy decision.

### Agent structure (goal 0.12)

The third MCP tool, `brief` ([brief.schema.json](../../schemas/brief/v1/brief.schema.json)), lets an agent about to start a subagent ask how to brief it: it takes a `task_kind` and the brief, and answers as `enhance` would for that kind (the champion's framing, a challenger or unchanged), recorded the same way, with client `brief` unless one is given.

`whetstone report --agents` scores each orchestrator kind by the runs its prompts started (requests that name it in `context.parent_request_id`): the share of measured child runs that completed without rework, and the median cost of one prompt's children together; and each `output/` kind (handoffs, pull request bodies) and template version by its reader's median `read_ms` and `edit_chars`.

`whetstone kinds suggest [--limit N]` proposes kinds for prompts of kind `other` and `agent/general-purpose`: each prompt goes to its most distinctive word shared with at least four others, and words with five or more prompts are printed with three examples. `whetstone graduate` prints, as Markdown, each champion the owner promoted over a challenger with the counts and note of that decision and its text, and each ablation the report calls a removal candidate: proposals for the owner's shared skills and instructions. Both read only; adding a rule or opening a pull request elsewhere is the owner's act.

### gym

`whetstone gym select --kind K [--limit N] [--confirm]` picks stored requests the replay gym may run again ([ADR 0006](../adr/0006-replay-gym-screens-challengers.md)): of a read-only kind (`agent/explore`, `agent/plan`, `agent/claude-code-guide`, `agent/box-reader`, `agent/general-purpose/explore`, `agent/general-purpose/research`), with a repository and a commit, not about live state (CI, pull requests, deploys, logs, "today"), and with no redaction marker. Newest first, 30 by default. It prints why the others were left out and counts until `--confirm`; then it stores each as a case: the request id, repository, commit, kind and a hash of the prompt, never a copy. A request already a case is not picked again.

`whetstone gym run --challenger ID@VERSION [--cases N] [--budget-usd B]` replays a stored challenger template against the cases of its kind through a gym client: per case the stored prompt as written (control, reused when one ran in the last 7 days) and the prompt framed by the challenger, in alternating order, one run at a time, stopping before the budget ($1 by default) would be passed. A case the version already ran on is skipped. With a judge, each fresh pair gets one verdict, with the order the judge sees flipped on alternate cases. Answers go to the judge and are not stored. No gym client is built yet, so `run` refuses for now; the chargehand client needs chargehand to skip its own whetstone seam for replays first.

`whetstone gym report --challenger ID@VERSION` pairs each challenger run with the nearest control on the same model and prints the screen: **screen pass** when, over at least 8 pairs, the challenger completes at least as often, its median paired cost ratio is at most 1.10, and the judge prefers it in at least 55% of the pairs it decided; otherwise **screen fail** or **not enough pairs**, with the reason. A pass only lets the challenger enter a live trial (`whetstone template add`); gym rows never reach `report` or the promotion rule.

`whetstone gym pin --template ID@VERSION` marks up to five cases as the template's unit cases: the newest where its run and the control on the same model both completed. `whetstone gym test --template ID@VERSION` replays the template on its unit cases and compares with its own latest earlier run on each: it fails (exit 1) when fewer complete, or when the median cost is more than 15% higher. `whetstone gym canary` lists champions whose kind's newest live run reports a model their unit cases never ran on, with the `gym test` to run. `whetstone gym harnesses` compares gym clients on control runs per kind (runs, completion, median cost and duration); it chooses nothing.

### label

`whetstone label list [--kind K] [--limit N]` prints runs with a reported completion and no hand label, newest first, with what the proxy says (completed, rework). `whetstone label set REQUEST_ID yes|no|clear` records your verdict: the run completed without needing a re-ask. Labels are stored in the store (schema 6), exported as `hand_label`, and `report` ends with how often the proxy "completed, and no rework" agrees with them; the promotion rule's decisions are only to be trusted after at least 30 labels with 75% agreement (design, "Validity first").

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
| `WHETSTONE_MEMORY_URL` | unset | `serve` and `mcp` with templates on: the base address of a Hindsight server; a template with a `{{memory}}` slot is filled from its recall (read only, nothing written back). Unset leaves the slot empty |
| `WHETSTONE_MEMORY_BANK` | `projects` | the bank recalled from |
| `WHETSTONE_MEMORY_KEY` | unset | a bearer key for the memory server, if it needs one |
| `WHETSTONE_COST_WEIGHTS` | `1,1.25,0.1,5` | `report`: weights of input, cache write, cache read and output tokens in a run's cost; four numbers of 0 or more, or `report` exits 2 |
| `WHETSTONE_PERSON_TIME_KINDS` | `typed/` | `report`: comma-separated kind prefixes whose cost-only decisions compare the person's time (`person_ms`) instead of run cost |
| `WHETSTONE_WRITER_URL`, `_MODEL`, `_KEY`, `_MAX_USD_CALL`, `_MAX_USD_WEEK`, `_PRICE` | none (off) | `template write`: the OpenAI-compatible chat endpoint (base address, `/chat/completions` is added), the model, its key, the most one call may cost, the most a week may cost, and the prices in dollars per million tokens (`3,15`: input, output). All six or none; no default is assumed |
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
| `context.hints` | object | Optional; up to 10 names (`a-z`, `0-9`, `_`) to strings of at most 500 characters; fill `{{hint_<name>}}` slots; not stored |
| `context.parent_request_id` | string | Optional; the request of the orchestrator prompt that started this one; the parent kind is scored by its children |
| `context.level` | `cheap`, `middle` or `best` | Optional; the price level the client chose; a kind's champion stored under `<kind>@<level>` serves it |
| `context.ablation` | string | Optional; what the client held out for the session (`rule:<id>`, `skill:<name>@<version>`); stored and compared in `report` |
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
| `outcome.read_ms`, `outcome.edit_chars` | integer or null | 0 or more; for an agent's output (a handoff, a pull request body): the reader's time and the characters they changed |
| `outcome.rewritten_prompt` | string or null | What the client's model wrote in place of the prompt, sent or reverted (`rewrite_accepted` says which); redacted and cut like a prompt. `export` carries it beside the original `prompt` as a rewrite pair (store schema version 8). Capture only: nothing is trained on it |

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
