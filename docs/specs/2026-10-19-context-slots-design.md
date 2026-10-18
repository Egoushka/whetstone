# Context: slots filled from memory and recent failures, and trials of instructions (goal 0.9)

Status: outline, decision 1 revised 2026-10-19 after a research pass over the memory service the owner runs.

Templates already have slots (`{{repository}}`, `{{commit}}`, `{{task_kind}}`). This goal adds slots whose value comes
from outside the request, and runs the same trials on the instructions a client loads (instruction files and skills)
as on prompts.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Memory slot | `{{memory}}` is filled at `enhance` time from a configured memory service: decisions and recent failures recalled for the repository and the prompt, at most once per request, cached for a few minutes, with a hard timeout (an answer that comes late leaves the slot empty). A template with the slot is a challenger to the same template without it. The service has no way to store "this memory helped" that recall would use, so **whetstone keeps that signal in its own store**: per memory shown (keyed by the source document, not the memory id, which changes when the service rewrites a summary), the outcomes of the runs it was shown in. It over-fetches from recall and re-ranks with what it learned, dropping memories that never helped. Nothing is written back to the memory service. |
| 2 | Hint slots | `{{last_ci_failure}}` and similar slots are filled from context the client sends (the failing test path from the last CI run of the branch). whetstone does not fetch them. |
| 3 | Instruction ablation | A client that loads instruction files may ask for an arm per rule: for 10% of sessions one rule is held out, chosen from the session id. Outcomes are compared per rule; a rule whose absence changes nothing after 100 sessions is listed as a removal candidate. The owner decides. |
| 4 | Skill variants | Versions of one skill's text run as champion and challenger, assigned per session like rules, scored by the outcomes of the sessions that loaded them. |

## Fit with the vision

Decision 1 reads from the owner's own memory service (a recall query, built from the prompt, goes to it) and writes
nothing back. Decisions 3 and 4 bend "never change a prompt without showing the change": a held-out rule is
not shown per prompt. They are off until the owner turns them on, and the report lists, per session, what was held out.
The client applies the arm; whetstone only assigns it.
