<!-- readme:header:start -->
<p>
  <img alt="status: Archived" src="https://img.shields.io/badge/status-Archived-6e7781">
  <a href="https://github.com/Egoushka/whetstone/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Egoushka/whetstone/actions/workflows/ci.yml/badge.svg"></a>
</p>
<!-- readme:header:end -->

# whetstone

> A personal prompt enhancer for coding agents meant to learn from my own runs which prompts work. Archived.

<!-- readme:status:start -->
**Archived**. Stopped. The README says what it tried and what survived.
<!-- readme:status:end -->

> [!NOTE]
> **Archived 2026-11-01.** It ran for four months and never showed that a learned template beats a fixed one; the two fixed briefs it served live on as static text.

## What it tried

I write the same kinds of prompt every day: fix this test, review this diff, research this question. whetstone was a
.NET 10 server that spoke [MCP](https://modelcontextprotocol.io) (Model Context Protocol, the standard way agents call
tools). Before a prompt went to an agent, a client asked it to `enhance` the prompt; afterwards the client sent
`feedback` with the outcome. Each kind of prompt had a champion template and challengers, and a challenger would replace
the champion only after it won a trial on real outcomes: accepted work, score, cost. The full design is in
[docs/history.md](docs/history.md).

## What happened

The plumbing worked and the learning never started. By the end it had 579 tests, three clients and no trial win.

| Measure | Result |
|---|---|
| Calls over its life | 5,523 from a Claude Code prompt hook, 1,838 from a Claude Code plugin, 110 from [chargehand](https://github.com/Egoushka/chargehand) |
| Prompts changed, last 7 days | 108 subagent prompts; 9 of 388 typed prompts |
| Trial arms since 2026-10-18 | 15 champion, 7 challenger, 46 held out |
| Hand labels for judging a winner | 0 of the 30 to 50 planned |
| Stored prompts read back by anything | none (2,680 rows written) |

Two things decided it. Without labels, no challenger could ever be promoted, so the store, trials and weekly
graduation job ran for nothing. And the only output that changed live prompts was two fixed texts, which need no
server.

## What survived

The two built-in briefs, appended after the prompt a coding agent writes for a subagent. They now live as static text
in a Claude Code `PreToolUse` hook in my private workspace repository, with no network call:

```text
For a subagent that may edit (general-purpose):
Report format: what you found or changed, how you checked it (the command you ran and its result), and anything left undone.
When the task is done and the checks pass, stop and report. Keep the report under 300 words.
Stay within the task above; if something else needs doing, say so in the report instead of doing it.

For a read-only subagent (Explore, Plan, claude-code-guide, box-reader):
Report format: what you found, the evidence (file paths and line numbers, commands run, results), and anything you could not confirm.
When the task is done, stop and report; do not keep searching once you can answer. Keep the report under 300 words.
This task is read-only: do not edit, create or delete files, and do not commit.
```

## Where it went

chargehand keeps the seam: [ADR 0040](https://github.com/Egoushka/chargehand/blob/main/docs/adr/0040-prompt-enhancer-extension.md)
defines a prompt enhancer you connect over MCP, so a tool like this one can plug in again without chargehand owning
your prompts. The retirement and its evidence are in [#71](https://github.com/Egoushka/whetstone/issues/71).
