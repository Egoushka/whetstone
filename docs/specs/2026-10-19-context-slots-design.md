# Context: slots filled from memory and recent failures, and trials of instructions (goal 0.9)

Status: decision 1 waits for a research note on whether a memory service's recall helps a prompt; this page is revised
when it lands.

Templates already have slots (`{{repository}}`, `{{commit}}`, `{{task_kind}}`). This goal adds slots whose value comes
from outside the request, and runs the same trials on the instructions a client loads (instruction files and skills)
as on prompts.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Memory slot | `{{memory}}` is filled at `enhance` time from a configured memory service: decisions and recent failures recalled for the repository and the prompt, within the deadline (an answer that comes late leaves the slot empty). A template with the slot is a challenger to the same template without it. When a run that had the slot ends without rework, whetstone sends the memory service the ids of the memories used and the verdict, never the prompt. |
| 2 | Hint slots | `{{last_ci_failure}}` and similar slots are filled from context the client sends (the failing test path from the last CI run of the branch). whetstone does not fetch them. |
| 3 | Instruction ablation | A client that loads instruction files may ask for an arm per rule: for 10% of sessions one rule is held out, chosen from the session id. Outcomes are compared per rule; a rule whose absence changes nothing after 100 sessions is listed as a removal candidate. The owner decides. |
| 4 | Skill variants | Versions of one skill's text run as champion and challenger, assigned per session like rules, scored by the outcomes of the sessions that loaded them. |

## Fit with the vision

Decision 1 sends data to a third party only when that party is the owner's own memory service, and sends ids and a
verdict, not prompt text. Decisions 3 and 4 bend "never change a prompt without showing the change": a held-out rule is
not shown per prompt. They are off until the owner turns them on, and the report lists, per session, what was held out.
The client applies the arm; whetstone only assigns it.
