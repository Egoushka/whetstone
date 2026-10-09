# Roadmap

Each goal is written as what is true when it is done. Finishing a goal is a minor release; 1.0 is when the contract is
declared stable. What changed in each release: [CHANGELOG.md](CHANGELOG.md).

Legend: ✅ done · ⏳ in progress · ▶ next · · later

## Stage 0 — It answers, and never gets in the way (0.1–0.3)

- ✅ **0.1** The contract and a pass-through: `enhance` and `feedback` as MCP tools with `schemas/*/v1`, a server that
  returns the prompt unchanged, stores nothing, answers within its latency budget, and a client in chargehand's tests.
- ⏳ **0.2** It remembers: every prompt and its outcome is stored per user, with secrets redacted before storage, and can
  be exported and deleted (docs/privacy.md).
- ⏳ **0.3** It retrieves: `enhance` returns your most similar past prompt that went well, filled in for this request,
  with the reason. No model call yet.

## Stage 1 — It learns (0.4–0.6)

- · **0.4** It measures: a held-out share of requests gets no rewrite, so acceptance, score and cost can be compared
  (docs/evaluation.md). A report says whether rewrites help.
- · **0.5** It writes templates: similar prompts are grouped and a versioned template is written for each group; a new
  version replaces the old one only when its outcomes are at least as good.
- · **0.6** It rewrites with a model: an optional configured model polishes the retrieved template, under a cost cap
  per call, shown in the reason.

## Stage 2 — It knows you beyond one client (0.7–1.0)

- · **0.7** It imports history: past prompts from agent transcripts (Claude Code, OpenCode) and from a memory service,
  redacted on the way in.
- · **0.8** It suggests the kind of task, so a client's model picker can use it.
- · **1.0** The contract is declared stable.

## Not planned

Running agents, choosing models, sharing prompts between users, a hosted service.
