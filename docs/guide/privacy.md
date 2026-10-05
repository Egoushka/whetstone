---
title: "Privacy and data"
description: "What whetstone stores, which secret shapes it redacts and which it keeps, the limits of that, and how to export or delete your data."
order: 3
section: "Concepts"
---

whetstone stores the prompts you write, and prompts carry private things: a repository name, a pasted error with a token in it. The rules are in [docs/privacy.md](../privacy.md); this page says which of them are in the code today.

## What is stored

Per user, one row per `enhance` call, in the SQLite file described in the [Quickstart](quickstart.md#where-the-data-lives):

| Field | Notes |
|---|---|
| `request_id`, `created_at` | UTC, when `enhance` answered |
| `prompt` | as sent, redacted, cut at 32,000 characters |
| `truncated` | true when the prompt was cut |
| `context` | repository, commit, task kind, client; redacted |
| `answer` | changed, template id and version, held out |
| `outcome` | null until `feedback`; then what it reported and when |

That is the `export/v1` record ([export.schema.json](../../schemas/export/v1/export.schema.json)). No file contents, no model answers and no rewrite are stored. The only outbound call planned is to a rewriting model you configure, goal 0.6, which does not exist yet ([docs/privacy.md](../privacy.md)).

## What the redactor replaces

Secret-shaped text becomes `[REDACTED:kind]` before anything is written. The kinds in [Redactor.cs](../../src/Whetstone/Redaction/Redactor.cs) are `private-key`, `jwt`, `header`, `password`, `token` and `high-entropy`. It covers:

- private-key blocks, and JWTs;
- vendor tokens such as AWS access key ids, GitHub, GitLab, Slack, Stripe, Anthropic, OpenAI, Google, npm, SendGrid, Hugging Face and DigitalOcean shapes;
- `Authorization`, `Cookie` and similar header values, and `Bearer` tokens;
- passwords in URLs and connection strings;
- `name = value` where the name says credential (`token`, `secret`, `api_key`, `password` and similar);
- long mixed-character strings above an entropy threshold.

It keeps on purpose: git shas, UUIDs, paths, identifiers, placeholders such as `$TOKEN`, names, emails and repository names. [Corpus.cs](../../tests/Whetstone.Tests/Redaction/Corpus.cs) is the list of shapes tested, and the changelog counts 31 secret shapes and 20 look-alikes that must be kept ([CHANGELOG.md](../../CHANGELOG.md)). Every pattern runs in linear time, so no input can hold a request.

> [!WARNING]
> Redaction matches patterns. A secret written in words is not a pattern, and an unlabelled 40- or 64-character hex secret reads as a git sha and is kept ([docs/privacy.md](../privacy.md)). A miss stays in your file until you run `forget`.

## Check your own store

```bash title="A second opinion with gitleaks"
scripts/audit-export.sh
```

The script runs gitleaks over `whetstone export`; a finding is a redaction miss ([audit-export.sh](../../scripts/audit-export.sh)). The audit has been checked by hand against a store holding an unredacted token ([CHANGELOG.md](../../CHANGELOG.md)). The goal 0.2 bar also asks for a week of the owner's real use with a clean audit, and that has not been recorded ([docs/evaluation.md](../evaluation.md)).

## Export and delete

`export` and `forget` are commands, not MCP tools ([ADR 0003](../adr/0003-memory-store.md)). The options are strict: an unknown or repeated option, or `forget` with nothing named, is a usage error that deletes nothing ([CommandTests.cs](../../tests/Whetstone.Tests/Storage/CommandTests.cs)). `forget` also overwrites deleted text and rebuilds the file, and it leaves another user's store alone. Both are in the [Reference](reference.md#commands).

Deleting from templates is promised in [docs/privacy.md](../privacy.md), but templates do not exist yet.
