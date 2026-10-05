# whetstone 0.3: it retrieves

- Status: draft for the maintainer's review. Decisions 1 to 3 are the ones to veto.
- Date: 2026-10-05

## Goal

When a new prompt resembles an earlier one that went well, `enhance` adds that earlier prompt to the answer; otherwise it
returns the prompt unchanged. This is the first goal where whetstone changes what it answers, so it is the first one that
can make things worse.

Done when (bar in docs/evaluation.md): acceptance is at least 40% over 50 rewrites. Acceptance is the share of rewritten
answers whose `feedback` says the user sent the rewrite.

## Where it stands

1. **Store:** one SQLite file per user, one row per `enhance`, outcome fields filled by `feedback` (0.2). Prompts are redacted
   before storage.
2. **Answer:** `EnhanceResponse` carries `Prompt`, `Changed`, `Reason`, `TemplateId`, `TemplateVersion`, `HeldOut`. `Reason` says
   "this was retrieved"; the one addition is an optional `source_request_id` (left out of a pass-through), so the stored row can
   record which request the answer drew on (decision 6).
3. **Gap:** the week of use that closes 0.2 yields few rows, and only rows with a `score` can count as "went well". Whether
   there are enough of them is not known yet; task 1 measures it before anything else is built.
4. **No baseline yet:** the held-out share starts in 0.4. Until then acceptance is the only signal, and the evaluation page
   already calls it necessary, not sufficient.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | What "similar" means | Lexical: SQLite FTS5 with BM25 over the stored prompt, filtered to the same user. No embeddings: no model, no network, no extra dependency, and the answer is deterministic and fits the 1.5 s deadline. Reopen only if the replay in task 1 shows lexical ranking fails on prompts that are plainly alike. |
| 2 | What "went well" means | A row is eligible when `score` is present and at or above the user's median score for that task kind (or overall, below 10 scored rows of that kind), and `model_overridden` is not true. A row without a score never qualifies. |
| 3 | What the rewrite is | The original prompt, then a quoted block: the matched earlier prompt (already redacted), its date and repository. Nothing is removed from the original. A past prompt on its own is not a better prompt for this task; as an addition it is context the user can delete before sending. |
| 4 | When to stay silent | Below a BM25 threshold, or with no eligible match, the answer is the pass-through. The threshold is set on the replay in task 1 and stored in configuration, not in code. |
| 5 | Cross-repository matches | Allowed, because the store is one user's. The block names the repository so a reader can see it came from elsewhere. |
| 6 | Traceability | A nullable `source_request_id` column on `requests` records which row was retrieved. The `export/v1` schema gains an optional field (additive within the major). |
| 7 | Self-match | A request never retrieves itself, and a prompt that is identical to the new one is not retrieved: it adds nothing. |
| 8 | Never block | Retrieval runs inside the request's budget. Any failure, or running out of time, returns the pass-through and counts in `/health` as `retrieval_failures`. Off unless `WHETSTONE_RETRIEVAL=on`, which also needs `WHETSTONE_RETRIEVAL_MIN_SCORE` (BM25 grows with query length and falls with a small store, so it is read off `whetstone replay`, never defaulted). |
| 9 | Order of redaction | Retrieved text comes from storage, so it was redacted at write time. The new prompt is redacted again before it is matched, so a secret in it never reaches a query that is logged. |

## What can go wrong

- **Too few rows:** with fewer than about 20 scored rows the median and the threshold mean little. Then 0.3 ships as built
  but stays off (a `retrieval.enabled` setting, default off) until the store has enough; the bar cannot be met before that.
- **Acceptance without benefit:** a user may send the longer prompt out of habit. 0.4 exists to catch this; do not read a
  40% here as proof the feature helps.
- **Stale matches:** an old prompt may name files or APIs that no longer exist. The block carries the date; the held-out
  comparison in 0.4 is where this shows.

## Tasks (each one pull request)

1. **Replay tool:** a command that, for every scored row, ranks the others and prints the top match and its score, so the
   owner can read real pairs and pick the threshold. Reads only; no change to answers. Reports how many rows are eligible.
2. **Index:** FTS5 table kept in step with `requests`, rebuilt by a command; `source_request_id` column and the `export/v1`
   field with examples checked in CI.
3. **Retriever:** `IEnhancer` implementation that applies decisions 2 to 9, behind `retrieval.enabled`, with tests on
   invented prompts: match, no match, self, identical, unscored, overridden, failure isolation.
4. **Run it:** enable on the local agent, collect 50 rewrites, report acceptance from `export`, release 0.3.0.

## Out of scope for 0.3

Embeddings, templates, a model call, held-out routing (0.4), task-kind inference (0.8), importing history (0.7), retrieval
across users.
