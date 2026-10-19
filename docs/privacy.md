# Privacy and data

whetstone stores the prompts people write. Most prompts carry something private: a repository name, a customer's
problem, a pasted error with a token in it. This page is the rule for every goal that stores or sends data.

## Stored, per user

| what | from goal | kept |
|---|---|---|
| the prompt as sent, with secrets redacted | 0.2 | until deleted |
| the context the client sent (repository, commit, task kind, client, session id) | 0.2 (session id 0.6) | until deleted |
| the outcome from `feedback`, including delayed outcomes, correction reasons, review finding counts and the person's time | 0.2 (rewards 0.6) | until deleted |
| the text a client's model wrote in place of the prompt (a rewrite pair), redacted and cut like the prompt | 0.7 | until deleted, with the request |
| the traits the owner defines (named paragraphs) and which were drawn on or off for each request | 0.7 | until deleted; the draws go with the request |
| what each challenger-writer model call cost (time, kind, model, dollars; no text) | 0.7 | until deleted |
| a search index over the stored prompt (words only, no other text) | 0.3 | until deleted; rebuilt on every `forget` |
| which stored request a retrieval drew on | 0.3 | until deleted |
| templates and their versions | 0.5 | until deleted |
| prompts typed in past sessions, imported by the owner, each with an implicit score | 0.7 | until deleted |
| which memories a request was shown (memory id, source document, a hash of the text; never the text) | 0.9 | until deleted, with the request |
| replay cases, runs and judge verdicts (ids, prompt hashes, measures; no answers) | 0.8 | until deleted, with the request |
| prompts agents and orchestrators wrote in past sessions (subagent tasks, page-extraction questions, orchestrator turns), imported by the owner, each with the run measures the transcript recorded | 0.4 | until deleted |

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
  stores nothing until `--confirm`. Prompts agents wrote for subagents, page extractions and orchestrators are stored the same
  way, under the same exclusions; they often quote file contents, which is why a session left out by folder, by the paths
  its tools touched or by text keeps these out as well. `--exclude` and `--exclude-text` keep whole sessions out by folder or by content, and
  `forget --text` removes stored rows by content. Message and session ids from the transcript are not stored.
- **Outbound calls.** The only outbound call is to the model the owner configures for `whetstone template write` (goal 0.7;
  off unless all six `WHETSTONE_WRITER_*` settings are given, and sent only with `--confirm`). It carries the champion's text,
  the newest few stored prompts of that kind that ended in rework with the reason they were corrected, and the nearest retired
  templates, all redacted once more before they leave; the key is read from the environment and never stored or printed. With `WHETSTONE_MEMORY_URL` set (goal 0.9), a recall query (the redacted
  prompt, cut at 2,000 characters, and the repository name) goes to the owner's own memory service; nothing is written back
  (docs/specs/2026-10-19-context-slots-design.md).
- **Replays (goal 0.8, ADR 0006).** `whetstone gym run` may send a stored, redacted prompt and a template's text to the gym
  client the owner configures (a coding-agent orchestrator pinned to the request's commit) and to the judge model the
  owner configures. Only cases from repositories the gym client already holds are sent. The answers are not stored; the
  store keeps the case id, a hash of the prompt, the run's measures and the judge's verdict, and `forget` removes them
  with the request they came from.
- **This repository holds no real prompt.** Tests and examples use invented ones.
