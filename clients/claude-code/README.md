# whetstone mod for Claude Code

Sends the prompts you type through whetstone (ADR 0005), shows what changed, and reports how each turn went.

- `prompt.submit`: calls `enhance` with `client: claude-code` and the repository (from `git remote`); whetstone names the kind,
  picks the arm and stores the request. The champion template is added after your prompt; the `typed-rewrite-haiku`
  challenger runs a small-model rewrite under strict checks (`hooks/rewrite.ts`); the held-out arm sends the prompt as typed.
  A change is logged with the reason; `/whetstone` lists the last ten with the originals.
- `turn.complete`: calls `feedback` with `completed`, tokens (input, output, cache read, cache write), `duration_ms` and the model.
  The next typed prompt sets `asked_again` on the previous request when it opens like a correction.
- Left alone: slash commands, prompts under 40 or over 2,000 characters, pasted text, attachments, anything that looks like a
  credential, folders under `Projects/work`, and a prompt that starts with `*`, `/` or `!`.
- Fails open: no key, no server, a slow answer (1.2 s), a bad reply or any error sends the prompt as typed.

The key is read from the login keychain item `whetstone-api-key` once per session and kept in memory. The server is
`http://127.0.0.1:7340/v1/mcp` (`hooks/client.ts`).

Load it: `claude --plugin-dir clients/claude-code`, or copy the folder under the mods folder (`~/.claude/dev-mods/<id>/whetstone`).
Check it: `claude plugin validate clients/claude-code`; run the tests from a copy outside any dot-folder
(`claude plugin test` finds no tests under `.claude/worktrees`): `cp -R clients/claude-code "$TMPDIR/whetstone" && claude plugin test "$TMPDIR/whetstone"`.
