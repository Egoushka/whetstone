---
title: "Overview"
description: "What whetstone is, how a prompt moves through it, what it does not do yet, and where it stands at 0.1.0 plus the unreleased memory store."
order: 0
section: "Get started"
---

whetstone is a personal prompt enhancer. A client sends it a prompt before running it; whetstone is meant to return a better one, built from your own past prompts that went well, and to learn from what happened next. It is a .NET 10 program that speaks [MCP](https://modelcontextprotocol.io) through two tools, `enhance` and `feedback` ([ADR 0002](../adr/0002-stack.md), [Tools.cs](../../src/Whetstone.Server/Tools.cs)).

> [!IMPORTANT]
> Today whetstone does not improve anything. `enhance` returns your prompt unchanged and says so in its `reason` field. What exists is the contract, the server, and a store that remembers each call with secrets redacted. Learning starts at roadmap goal 0.3 ([ROADMAP.md](../../ROADMAP.md)). [Status](status.md) lists what is built and what is not.

## How a prompt moves through it

1. A client calls `enhance` with the prompt and the context you allow: repository, commit, task kind, client name.
2. whetstone answers inside the client's deadline. Today the answer is the same prompt with `changed: false`.
3. whetstone stores the call, with secrets replaced by markers, in a SQLite file that belongs to your user.
4. After the run the client calls `feedback` with the `request_id` and the outcome: rewrite accepted, model overridden, score, cost, model. whetstone fills the outcome into the stored row.

If whetstone is slow, fails, or cannot write its store, the client still gets the original prompt. Tests for each case are named in [Status](status.md#tests-and-ci).

## Who it is for

One person who runs a coding agent every day and wants an enhancer that is theirs: one store per user, local first, exportable and deletable ([VISION.md](../../VISION.md)). The first client is [chargehand](https://github.com/Egoushka/chargehand); any MCP client can call the two tools. There is no hosted service and none is planned ([ROADMAP.md](../../ROADMAP.md#not-planned)).

## What it does not do

- It does not rewrite prompts. Retrieval, off by default, appends an earlier prompt of yours and leaves yours as it is; no templates and no model call exist ([ROADMAP.md](../../ROADMAP.md) goals 0.3, 0.5 and 0.6).
- It does not run agents, pick a model, or share prompts between users ([VISION.md](../../VISION.md#what-it-will-not-do)).
- It has no encryption of its own. The store is a plain SQLite file; disk encryption is yours ([ADR 0003](../adr/0003-memory-store.md)).
- It has no tool to read or delete your data over MCP. `export` and `forget` are commands you run in a terminal, so no agent can call them ([ADR 0003](../adr/0003-memory-store.md)).
- It has no way to bind a non-loopback address from the command line. `serve --listen` with such an address stops with an error ([WhetstoneServer.cs](../../src/Whetstone.Server/WhetstoneServer.cs)).

## Where it stands

- Version 0.1.0 is released: the contract and the pass-through. Goal 0.2, the memory store with redaction, export and forget, is merged under `Unreleased` in [CHANGELOG.md](../../CHANGELOG.md) and not yet tagged.
- [ROADMAP.md](../../ROADMAP.md) marks 0.1 done and 0.2 next. It says a goal is done when its bar in [docs/evaluation.md](../evaluation.md) is met, not when the code merges. The bar for 0.2 includes a week of the owner's use, and no page here claims that is done.
- Only the latest commit on `main` is supported ([SECURITY.md](../../SECURITY.md)).
- The GitHub repository is private until the maintainer decides otherwise ([going-public.md](../going-public.md)).

## Get started

Follow the [Quickstart](quickstart.md): from a clone to a stored call and an export. The [Reference](reference.md) has every command, variable and field.
