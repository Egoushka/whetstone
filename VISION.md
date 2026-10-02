# Vision

## The problem

People who use coding agents every day write the same kinds of prompt again and again: fix this test, review this
diff, explain this module, plan this feature. Each time they rewrite it from memory, forget the part that made last
week's version work, and cannot tell which model would have done it as well for less. Vendor clients keep a history,
but they do not learn from it, and the history is locked in each client.

## What whetstone is

A personal service that owns your prompts and gets better at them from what you do:

- **It learns from outcomes, not opinions.** A template earns its place by the runs that used it: accepted, scored
  well, cost little. No hand-kept prompt library, no "best prompts" list from the internet.
- **It is yours.** One store per user, local first, exportable and deletable. Nobody else's prompts shape yours unless
  you import them.
- **It is client-neutral.** It speaks MCP, so chargehand, an editor plugin or a script can use it. chargehand is the
  first client because it already measures the outcomes whetstone learns from.
- **It is optional and never blocks.** Any failure means the original prompt goes out unchanged.

## What it will not do

- Run agents, call tools or pick a model on its own. It may suggest a task kind; the client decides the model.
- Send your prompts to a third party except the model you configure for rewriting, if any.
- Become a prompt marketplace or a shared library.
- Change a prompt without showing the change.

## What success looks like

After a month of daily use: at least half of the rewrites are accepted, runs that used a rewrite score no worse and
cost no more than a held-out set that did not (docs/evaluation.md), and the owner has stopped keeping prompts in notes.
