# Price levels and routing evidence (goal 0.10)

Status: built 2026-10-19 (decisions 1 to 3). A level's champion is a template stored under the kind `<kind>@<level>`, so its
trial and evidence are its own without a new table. Decision 4 is a follow-up outside whetstone.

A kind's best prompt for an expensive model is not always its best for a cheap one, and the evidence whetstone keeps per
model can tell the owner which model and effort a kind needs. This goal replaces the earlier "it suggests the task kind".

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Levels | A kind may have one champion per level: `cheap`, `middle`, `best`. The client sends the level in `context.level` (from a prompt prefix such as `*cheap` or `*max`, or from a budget or time limit it knows). With no level the client's default applies. Evidence is already grouped by model; levels group models the owner assigns to them. |
| 2 | Routing evidence | `whetstone report --routing` prints, per kind, the cheapest model whose completion and rework are non-inferior to the best one at the same looks as the promotion rule, with counts. It reads only. |
| 3 | Effort evidence | The same report per `effort` value. |
| 4 | Turning evidence into routing | A job outside whetstone (in the owner's gateway configuration repository) reads the report weekly and opens a pull request with routing rules. The owner merges or closes it. |

## Fit with the vision

The vision says whetstone does not pick a model; the client decides. This holds: the level comes from the client,
the report suggests, and routing changes only by the owner's merge in another repository.
