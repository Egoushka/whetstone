# Privacy and data

whetstone stores the prompts people write. Most prompts carry something private: a repository name, a customer's
problem, a pasted error with a token in it. This page is the rule for every goal that stores or sends data.

## Stored, per user

| what | from goal | kept |
|---|---|---|
| the prompt as sent, with secrets redacted | 0.2 | until deleted |
| the context the client sent (repository, commit, task kind, client) | 0.2 | until deleted |
| the outcome from `feedback` | 0.2 | until deleted |
| a search index over the stored prompt (words only, no other text) | 0.3 | until deleted; rebuilt on every `forget` |
| which stored request a retrieval drew on | 0.3 | until deleted |
| templates and their versions | 0.5 | until deleted |
| prompts typed in past sessions, imported by the owner, each with an implicit score | 0.7 | until deleted |

Nothing else: no file contents, no model answers, no other users' data.

## Rules

- **Redact before storage.** Secret-shaped text (keys, tokens, private keys, connection strings) is replaced with a
  marker before anything is written. A redaction miss is a `broken` bug.
  What is redacted: private-key blocks, vendor and cloud tokens, JWTs, authorization and cookie headers, passwords in URLs
  and connection strings, `name = value` where the name says credential, and long mixed high-entropy strings. Kept on
  purpose: git shas (40 and 64 hex), UUIDs, paths, identifiers, placeholders such as `$TOKEN` or `{{password}}`, and
  names, emails and repository names. Known limits: an unlabelled 40- or 64-character hex secret reads as a git sha and is
  kept; a secret written in words is not a pattern. The corpus (`tests/Whetstone.Tests/Redaction/Corpus.cs`) is the list
  of shapes covered; add a shape there before fixing a miss.
  `scripts/audit-export.sh` runs gitleaks over an export as a second opinion; a finding is a redaction miss.
- **Retrieval stays inside one store.** When retrieval is on, `enhance` may quote an earlier prompt of the same user back to
  them; it is read from that user's file only and was redacted when stored.
- **One store per user.** A file per user under the data directory (mode 0600, directory 0700); no query reads across
  stores.
- **Export and delete.** `whetstone export` writes everything stored for the user as JSON lines; `whetstone forget`
  deletes it, with an optional filter (a repository, a date range, a text pattern), and only counts matches until `--confirm` is given.
  They are commands, not MCP tools, so no agent can call them (ADR 0003). Deletion removes it from templates too: a
  template written from a forgotten prompt is rebuilt or dropped.
- **Import is the owner's act.** `whetstone import` reads past session transcripts from a folder the owner names and stores
  only the prompts a person typed, redacted like live ones, with a score derived from what came next (no model call). It
  stores nothing until `--confirm`. `--exclude` and `--exclude-text` keep whole sessions out by folder or by content, and
  `forget --text` removes stored rows by content. Message and session ids from the transcript are not stored.
- **Outbound calls.** The only outbound call is to the rewriting model the user configures (goal 0.6), and only the
  redacted prompt and the chosen template are sent.
- **This repository holds no real prompt.** Tests and examples use invented ones.
