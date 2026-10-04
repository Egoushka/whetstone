# Privacy and data

whetstone stores the prompts people write. Most prompts carry something private: a repository name, a customer's
problem, a pasted error with a token in it. This page is the rule for every goal that stores or sends data.

## Stored, per user

| what | from goal | kept |
|---|---|---|
| the prompt as sent, with secrets redacted | 0.2 | until deleted |
| the context the client sent (repository, commit, task kind, client) | 0.2 | until deleted |
| the outcome from `feedback` | 0.2 | until deleted |
| templates and their versions | 0.5 | until deleted |

Nothing else: no file contents, no model answers, no other users' data.

## Rules

- **Redact before storage.** Secret-shaped text (keys, tokens, private keys, connection strings) is replaced with a
  marker before anything is written. A redaction miss is a `broken` bug.
- **One store per user.** A file per user under the data directory (mode 0600, directory 0700); no query reads across
  stores.
- **Export and delete.** `whetstone export` writes everything stored for the user as JSON lines; `whetstone forget`
  deletes it, with an optional filter (a repository, a date range), and only counts matches until `--confirm` is given.
  They are commands, not MCP tools, so no agent can call them (ADR 0003). Deletion removes it from templates too: a
  template written from a forgotten prompt is rebuilt or dropped.
- **Outbound calls.** The only outbound call is to the rewriting model the user configures (goal 0.6), and only the
  redacted prompt and the chosen template are sent.
- **This repository holds no real prompt.** Tests and examples use invented ones.
