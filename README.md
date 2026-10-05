# whetstone

A prompt enhancer that learns from you. You write a prompt; whetstone returns a better one, built from your own past
prompts that went well, and learns from what happened next.

**Status: pass-through that remembers.** The MCP server answers `enhance` with your prompt unchanged and stores each call,
redacted, with its outcome; nothing is learned from it yet (goal 0.2 of [ROADMAP.md](ROADMAP.md)).

## Run it

```bash
dotnet run --project src/Whetstone.Server -- mcp                              # MCP over stdio
WHETSTONE_API_KEY=... dotnet run --project src/Whetstone.Server -- serve      # MCP over HTTP at 127.0.0.1:7340/v1/mcp
```

Every `enhance` is stored, with secrets redacted first, in `<WHETSTONE_DATA_DIR>/<WHETSTONE_USER>/whetstone.db` (your user data directory and the user `owner` by default); `feedback` adds the outcome. `GET /health` reports `store_failures`, `retrieval_failures` and the process's uptime, CPU seconds, threads and memory. Take it out or delete it from a terminal ([docs/privacy.md](docs/privacy.md)):

```bash
dotnet run --project src/Whetstone.Server -- export > mine.jsonl                       # everything, as JSON lines
dotnet run --project src/Whetstone.Server -- export --repository example/app --before 2026-10-01
dotnet run --project src/Whetstone.Server -- forget --repository example/app           # counts, deletes nothing
dotnet run --project src/Whetstone.Server -- forget --repository example/app --confirm # deletes
dotnet run --project src/Whetstone.Server -- reindex                                    # upgrade an older store file, rebuild its search index
```

Retrieval is off. `WHETSTONE_RETRIEVAL=on` with `WHETSTONE_RETRIEVAL_MIN_SCORE=<n>` (read it off `whetstone replay`) makes `enhance` append the closest earlier prompt of yours that was scored well, quoted after your prompt; no match, no scored prompt or any failure gives your prompt back unchanged.

Over HTTP every MCP request needs `Authorization: Bearer <WHETSTONE_API_KEY>`; `GET /health` needs none.

## How it works

1. A client (first: [chargehand](https://github.com/Egoushka/chargehand)) calls `enhance` with your prompt and the
   context you allow: repository, commit, kind of task.
2. whetstone finds your past prompts most like it that went well, and returns a rewrite, the template it used and why.
3. The client shows the rewrite as a diff. You send the rewrite or your original.
4. After the run the client calls `feedback` with the outcome: rewrite accepted or not, model overridden or not, the
   run's score and cost. That outcome is what whetstone learns from.

Your prompts stay in your whetstone. Nothing is shared between users, and no prompt is ever committed to this
repository.

## Contract

Two MCP tools, versioned under [`schemas/`](schemas/):

| tool | in | out |
|---|---|---|
| `enhance` | prompt, context | prompt, template id and version, reason, task kind |
| `feedback` | request id, outcome | nothing |

If whetstone is slow, down or has nothing to offer, the client sends the original prompt. whetstone is never in the
path of a run succeeding.

## Read next

- [VISION.md](VISION.md): why this exists and what it will not do.
- [ROADMAP.md](ROADMAP.md): goals in order.
- [docs/specs/2026-10-02-first-version-design.md](docs/specs/2026-10-02-first-version-design.md): the first version.
- [docs/privacy.md](docs/privacy.md): what is stored and how to delete it.
- [docs/evaluation.md](docs/evaluation.md): how we know it helps.
- [docs/adr/](docs/adr/): decisions.
- [docs/going-public.md](docs/going-public.md): the repository is developed as a public one and made public when the maintainer decides.

## License

Apache-2.0.
