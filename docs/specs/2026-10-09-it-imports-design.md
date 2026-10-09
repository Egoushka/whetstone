# It imports: past sessions as scored requests

Goal 0.7, brought forward for one client. Retrieval (goal 0.3) reuses only a prompt with a `score`, and a client that sends
`enhance` without `feedback` leaves the store with nothing to retrieve. Past sessions hold both halves already: what the
person typed, and what they did next. `whetstone import` turns them into stored requests with a score, so retrieval can be
measured on real data on its first day instead of after weeks of live use.

## Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Which client | Claude Code session transcripts (one JSON object per line, one file per session). Other clients later, each its own reader. |
| 2 | What counts as a prompt | A user message that is not a tool result, not meta, not a side chain, not a command (`/`), not client-generated text (`<…>`, `[…]`), and, when the line says who started the turn, started by a person (`turnOrigin: human`). A line that does not parse is skipped. |
| 3 | What the score is | Implicit: the next prompt in the session sets the base score (a correction 0.2, praise or a go-ahead 0.9, anything else 0.6; the session's last prompt 0.5), and a session in which the repository's configured user committed on any ref, from its first prompt to ten minutes after its last activity, adds 0.2, capped at 1. English and Ukrainian word lists ([ImplicitOutcome.cs](../../src/Whetstone/Import/ImplicitOutcome.cs)). |
| 4 | Duplicates | A resumed or forked session repeats earlier messages under the same message id: each id is stored once, scored from a copy that has a following prompt when one exists. A prompt a live client already stored (same client, same redacted text, within two minutes) is scored, not stored again. Another imported row never counts as a match, so two past prompts that redact to the same text stay two. |
| 5 | Ids | `imp-` and 32 hex characters of the SHA-256 of the message id, so a second import changes nothing. The message id and the session id are not stored. |
| 6 | What stays out | `--exclude DIR`: sessions that started under that folder. `--exclude-text REGEX`: sessions with any prompt that matches, ignoring case. A message seen in an excluded session stays out wherever else it appears. |
| 7 | Redaction | Every row goes through the same constructor as a live one (`RequestRow.From`), so the redactor and the length cut apply unchanged. |
| 8 | Safety of the command | Like `forget`: it counts and stores nothing until `--confirm`. It is a command, not an MCP tool. |
| 9 | Removing by content | `export` and `forget` take `--text REGEX`, so rows that should not have been stored (a live client's prompt that belonged to an excluded context) can be found and removed. |

## Limits

- The word lists and weights are guesses. Eligibility compares a score with the user's median, so only the order matters,
  but a terse "no" that opens a new request counts as a correction.
- A commit made in another repository during the session earns nothing; a commit by the same person in a parallel session
  in the same repository earns the bonus for both.
- The transcript format belongs to the client and is undocumented; a change to it reads as fewer prompts, not as an error.
- The commit field is empty for imported rows: a transcript records the branch, not the commit.
