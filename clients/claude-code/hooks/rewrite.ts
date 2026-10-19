// The model rewrite, one challenger among whetstone's templates (it was the prompt-edit mod): the rewriter's instructions and the
// checks an edit must pass. The id and version match the template whetstone seeds (`typed-rewrite-haiku`).

export const RULES = `You make small edits to a developer's message to a coding agent.
Change the message ONLY to remove an ambiguity that would make the agent guess:
- a pronoun or "that/this/it" whose referent is stated earlier in the SAME message: name the referent;
- two instructions that contradict each other inside the message: keep the later one and say so in "reason";
- a missing object of a verb that the message itself makes obvious.
Never add a requirement, a file, a name or a step that is not in the message. Never remove a clause. Keep the language
(Ukrainian stays Ukrainian), the tone, every code span, path, URL, number and identifier exactly as written.
If nothing is ambiguous, return the message unchanged with "changed": false.
Reply with one JSON object and nothing else: {"changed": boolean, "text": string, "reason": string}.
"reason" is one short sentence saying what was ambiguous.`

export const TEMPLATE_ID = 'typed-rewrite-haiku'

// Bumped by hand with the text above, and with the template whetstone seeds.
export const VERSION = 'r1'

const KEEP = /`[^`]+`|https?:\/\/\S+|[\w.~-]*\/[\w./~-]+|\b[\w-]+\.[a-z]{1,5}\b|\b\d[\w.-]*/g

/** Tokens that must survive an edit untouched: code spans, URLs, paths, file names, numbers. */
export function keepTokens(text: string): string[] {
  return [...new Set(text.match(KEEP) ?? [])]
}

const cyrillic = (s: string) => (s.match(/[а-яіїєґё]/gi) ?? []).length / Math.max(1, (s.match(/[a-zа-яіїєґё]/gi) ?? []).length)

export type Edit = { text: string; reason: string }

/** The rewriter's reply as an Edit, or undefined when it changed nothing or broke a rule. */
export function accept(original: string, reply: string): Edit | undefined {
  const json = reply.slice(reply.indexOf('{'), reply.lastIndexOf('}') + 1)
  let r: { changed?: unknown; text?: unknown; reason?: unknown }
  try {
    r = JSON.parse(json)
  } catch {
    return undefined
  }
  if (r.changed !== true || typeof r.text !== 'string' || typeof r.reason !== 'string') return undefined
  const text = r.text.trim()
  if (!text || text === original.trim()) return undefined
  const ratio = text.length / original.trim().length
  if (ratio < 0.8 || ratio > 1.4) return undefined                                  // an edit, not a rewrite
  if (keepTokens(original).some(t => !text.includes(t))) return undefined           // identifiers, paths and numbers intact
  if (Math.abs(cyrillic(original) - cyrillic(text)) > 0.25) return undefined        // same language
  return { text, reason: r.reason.trim().slice(0, 160) }
}

// The shorthand expander (goal 0.11), a second model challenger: a terse prompt keeps its own words and gains a short brief
// under them. The id and version match the template the owner adds with `whetstone template add ... --source model`.
export const EXPAND_ID = 'typed-expand'

export const EXPAND_VERSION = 'e1'

/** Prompts longer than this are sent as typed: they are not terse. */
export const EXPAND_MAX_CHARS = 100

export const EXPAND_RULES = `A developer typed a terse message to a coding agent. Write a short brief that spells out what the message already says:
the goal in one sentence, the files or areas it names, and when the agent should stop and report.
Use only what the message states or makes obvious. Never add a requirement, a file, a name or a step that is not in it.
Write in the message's language (Ukrainian stays Ukrainian). At most four short lines.
Reply with one JSON object and nothing else: {"changed": boolean, "text": string, "reason": string}.
"changed" is false when the message is already clear; "text" is the brief only, not the message.`

/** The message with the brief under it, or undefined when the expander declined or broke a rule. The message itself is never changed. */
export function acceptExpansion(original: string, reply: string): Edit | undefined {
  const json = reply.slice(reply.indexOf('{'), reply.lastIndexOf('}') + 1)
  let r: { changed?: unknown; text?: unknown; reason?: unknown }
  try {
    r = JSON.parse(json)
  } catch {
    return undefined
  }
  if (r.changed !== true || typeof r.text !== 'string' || typeof r.reason !== 'string') return undefined
  const brief = r.text.trim()
  if (!brief || brief.length > 600 || brief.split('\n').length > 6) return undefined
  if (Math.abs(cyrillic(original) - cyrillic(brief)) > 0.35) return undefined
  return { text: `${original}\n\n${brief}`, reason: r.reason.trim().slice(0, 160) }
}
