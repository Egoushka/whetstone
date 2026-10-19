# whetstone mod for Claude Code

Sends the prompts you type through whetstone (ADR 0005), shows what changed, and reports how each turn went.

- `prompt.submit`: calls `enhance` with `client: claude-code` and the repository (from `git remote`); whetstone names the kind,
  picks the arm and stores the request. The champion template is added after your prompt; the `typed-rewrite-haiku`
  challenger runs a small-model rewrite under strict checks (`hooks/rewrite.ts`); the held-out arm sends the prompt as typed.
  A change is logged with the reason; `/whetstone` lists the last ten with the originals.
- `turn.complete`: calls `feedback` with `completed`, tokens (input, output, cache read, cache write), `duration_ms` and the model.
  The next typed prompt sets `asked_again` on the previous request when it opens like a correction.
- Price level: a prompt that starts with `*cheap` or `*max` sends `level` `cheap` or `best` and loses the prefix, whether or not whetstone answers.
- `person_ms`: the pause between the end of the last answer and a prompt (reading it and typing), capped at 10 minutes. A correction's
  pause is added to the request it corrects; the pause before a prompt that follows an `output/` kind is its `read_ms`. Approval waits
  and `edit_chars` are not measured: the mod API shows neither.
- Context `hints`: `last_ci_failure` (workflow / job / step of the branch's newest failed run, read with `gh` in the background,
  at most every 5 minutes, never for `Projects/work`).
- Subagents (`agent.spawn`): the subagent's prompt is a request of its own with `parent_request_id` set to the prompt that started it,
  and its turn is reported. Only a server template for the kind is applied; teammates and forks are left alone.
- Not sent: `ablation`. The server assigns nothing: the client holds a rule out and names it. The mod API cannot do that for instruction-file rules (`prompt.compose` edits the system prompt's sections; CLAUDE.md reaches the model as context the mod does not edit), and a label without a real hold-out would corrupt the comparison.
- Left alone: slash commands, prompts under 40 or over 2,000 characters, pasted text, attachments, anything that looks like a
  credential, folders under `Projects/work`, and a prompt that starts with `*`, `/` or `!`.
- Fails open: no key, no server, a slow answer (1.2 s), a bad reply or any error sends the prompt as typed.

The key is read from the login keychain item `whetstone-api-key` once per session and kept in memory. The server is
`http://127.0.0.1:7340/v1/mcp` (`hooks/client.ts`).

Install it (typed in a session; needs the whetstone agent running and its key in the keychain):

```
/plugin install whetstone --marketplace Egoushka/whetstone
```

Or load it for one session: `claude --plugin-dir clients/claude-code`, or copy the folder under the mods folder (`~/.claude/dev-mods/<id>/whetstone`).
Check it: `claude plugin validate clients/claude-code`; run the tests from a copy outside any dot-folder
(`claude plugin test` finds no tests under `.claude/worktrees`): `cp -R clients/claude-code "$TMPDIR/whetstone" && claude plugin test "$TMPDIR/whetstone"`.
