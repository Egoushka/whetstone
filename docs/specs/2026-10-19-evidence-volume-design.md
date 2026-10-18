# Evidence volume: more learned per run (goal 0.7)

At one owner's volume most kinds will never reach the promotion rule's first look (30 counted runs per arm). This goal
gets more evidence out of each run and more runs into the store, so trials finish. It replaces the earlier goals "it
writes challengers" and "it imports history", which both feed it.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Allocation | A kind with two or more challengers allocates its non-held-out traffic by Thompson sampling on completion (rework counts as not completed), with a floor of 15% per arm. The held-out share stays a fixed 10%, assigned by the request-id hash before the bandit draws, so the regression monitor keeps an unbiased baseline. The sample-ratio check then tests only the held-out share. Promotion still happens only at the fixed looks of the rule; the bandit changes who gets traffic, not when a decision is taken. |
| 2 | Trait trials | A template may be composed of **traits**: named paragraphs that do one thing (name the file to touch, a stop rule, a report format). For kinds that opt in, each request draws each trait on or off independently from the request-id hash (a factorial design). A trait's effect is estimated pooled across every kind that carries it, which is how pooling in the promotion rule already works, with per-kind counts shown beside it. A trait that wins enters the champions of the kinds it was tried on. |
| 3 | Challengers written by a model | A configured model reads a kind's champion and the runs that ended in rework, with their correction reasons, and writes the next challenger (reflective evolution, after GEPA). It sees redacted text only, runs under a cost cap per call and per week, and each version records its source (`model`) and the runs it read. Off unless configured. |
| 4 | Filler stripper | One challenger class removes phrases that only ask for effort ("be thorough", "double-check everything") from the original prompt. It amends design decision 3 (the original sits unchanged between `before` and `after`) for this class alone: the change is a deletion of listed phrases, shown to the person, and the response's `reason` lists what was removed. |
| 5 | Negative library | Retired challengers and their records stay in the store and are exported. The model writer receives the nearest retired texts (BM25, the retrieval code) and a proposal too close to one of them is dropped before it is stored. |
| 6 | Rewrite pairs | Every model rewrite is kept with the original, whether the person sent or reverted it, and the run's outcome; `export` carries the pair. No local model is trained in this goal; at about 1,000 pairs the choice is reopened. |
| 7 | More imports | `whetstone import opencode` and an import from a memory service, with the same redaction and exclusions as `import claude-code`. |

## Fit with the vision

Unchanged: templates still frame the prompt, the person still sees every change, and a model is used only when the
owner configures one. Decision 4 is the one bend, limited to deleting listed phrases.

## Not in this design

Training a local rewriter (reopened at 1,000 pairs) and bandits over models (models are the client's choice).
