# Agent structure: orchestrators, kinds, graduation, outputs, a brief tool (goal 0.12)

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Orchestrator prompts | `enhance/v1` gains optional `context.parent_request_id`. An orchestrator's prompt is scored by its children: the share of child runs that completed without rework, and their summed cost. |
| 2 | Kind discovery | `whetstone kinds suggest` clusters prompts of kind `other` and of wide kinds (BM25 term vectors, local) and prints proposed kinds with examples and counts. The owner adds a rule; nothing changes by itself. |
| 3 | Graduation and removal | `whetstone graduate` prints, for each gold champion and each rule the ablation trial found useless, a proposed change to the owner's shared skills or global instructions, with the evidence. Opening a pull request in that repository is a client's or the owner's act. |
| 4 | Output templates | Kinds for what agents write for a person (handoffs, pull request bodies). Their outcome is the reader's: `read_ms` and `edit_chars` reported by the client. |
| 5 | A brief tool | A third MCP tool, `brief` (`schemas/brief/v1`): an agent about to start a subagent asks for the champion template of a kind and gets its text, so agents can use what whetstone learned without a hook. It records the request like `enhance`. |

## Fit with the vision

Decision 3 is the one that reaches beyond whetstone's store: it proposes text for files the owner keeps elsewhere, and
stops at a proposal. Decision 5 adds a tool without changing `enhance` or `feedback`.
