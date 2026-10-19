import type { Register } from 'claude-code'
import { EXPAND_ID, EXPAND_MAX_CHARS, EXPAND_RULES, EXPAND_VERSION, RULES, TEMPLATE_ID, VERSION, accept, acceptExpansion } from './rewrite'
import { whetstone, type Enhanced } from './client'
import { isCorrection, levelOf, repositoryOf, skipReason } from './policy'
import { refreshHint, type Hint } from './ci'

const CLIENT = 'claude-code'
const DEADLINE_MS = 1200
const KEEP_RECORDS = 200
// A pause longer than this is the person being away, not reading or typing.
const MAX_PAUSE_MS = 10 * 60_000
const HINT_TTL_MS = 5 * 60_000
const OUTPUT_KIND = 'output/'
const typed = (origin: string | undefined) => origin === undefined || origin === 'composer' || origin === 'bridge'

type Sent = { enhanced: Enhanced; text: string; turnId?: string; personMs?: number }
type Shown = { id: string; ts: number; kind: string; arm: string; template: string; original: string; sent: string }

// Typed prompts go through whetstone: it names a kind, picks an arm (a template, the model rewrite, or nothing held out) and
// stores the request; when the turn ends the mod reports how it went. Every failure sends the prompt as typed.
export const register: Register = on => {
  let ws: ReturnType<typeof whetstone> | undefined
  let last: Sent | undefined
  // An opaque id per session, so whetstone can link a correction to the request before it and own commits per session.
  const sessionId = `cc-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
  const byTurn = new Map<string, Sent>()
  // Requests made for subagent prompts, by the agent that runs them; the main turn in progress is the parent of its subagents.
  const byAgent = new Map<string, Sent>()
  let running: Sent | undefined
  let doneAt: number | undefined
  let hint: Hint | undefined

  on('session.start', async ($, e, next) => {
    ws = whetstone($)
    void quietly(async () => { hint = await refreshHint($, hint) })
    await $.command.register({ name: 'whetstone', description: 'Show the last prompts whetstone changed, with the originals' })
    return next(e)
  })

  const hintsFor = () => (hint?.value ? { last_ci_failure: hint.value } : {})

  const quietly = async (work: () => Promise<unknown>) => {
    try {
      await work()
    } catch {}
  }

  on('prompt.submit', async ($, e, next) => {
    ws ??= whetstone($)
    const client = ws
    const now = await $.clock.now()
    // The time since the last answer ended: reading it and typing this prompt.
    const pause = doneAt === undefined ? undefined : Math.min(Math.max(0, now - doneAt), MAX_PAUSE_MS)
    let correction = false
    // The previous typed prompt's answer is judged by how this one opens, whatever this one is.
    if (last && typed(e.origin?.kind) && e.command === undefined && !e.isMeta && !e.text.includes('<system-reminder>')) {
      const before = last
      last = undefined
      correction = isCorrection(e.text)
      const kind = before.enhanced.task_kind ?? ''
      const outcome = {
        asked_again: correction,
        // A correction's typing is rework on the request before it; the pause before an output is the reader's time.
        ...(correction && pause !== undefined ? { person_ms: (before.personMs ?? 0) + pause } : {}),
        ...(kind.startsWith(OUTPUT_KIND) && pause !== undefined ? { read_ms: pause } : {}),
      }
      before.personMs = outcome.person_ms ?? before.personMs
      void quietly(() => client.feedback(before.enhanced.request_id, outcome))
    }

    const cwd = await $.session.cwd().catch(() => '')
    // `*cheap` and `*max` choose a price level and are not part of the prompt, whether or not whetstone answers.
    const { level, text: typedText } = typed(e.origin?.kind) && e.command === undefined ? levelOf(e.text) : { level: undefined, text: e.text }
    if (typedText !== e.text) e = { ...e, text: typedText }
    const why = skipReason(e.text, cwd, (e.attachments?.length ?? 0) > 0, e.origin?.kind, e.command !== undefined, e.isMeta === true)
    if (why) return next(e)
    try {
      const remote = await $.process.run(['git', 'remote', 'get-url', 'origin'], { cwd, timeoutMs: 1500 }).catch(() => undefined)
      const repository = remote?.exitCode === 0 ? repositoryOf(remote.stdout) : undefined
      const enhanced = await Promise.race([
        client.enhance(e.text, { client: CLIENT, session_id: sessionId, ...(repository ? { repository } : {}), ...(level ? { level } : {}), ...(Object.keys(hintsFor()).length ? { hints: hintsFor() } : {}) }, DEADLINE_MS),
        $.clock.sleep(DEADLINE_MS + 300).then(() => undefined),
      ])
      if (!enhanced?.request_id) return next(e)
      let text = enhanced.changed ? enhanced.prompt : e.text
      let note = enhanced.changed ? enhanced.reason : ''
      if (enhanced.template_id === TEMPLATE_ID) {
        const r = await $.model.complete({ model: 'haiku', system: RULES, prompt: e.text, maxTokens: 700, effort: 'low', timeoutMs: 8000 })
        const edit = r.isAnswered ? accept(e.text, r.text) : undefined
        if (edit) {
          text = edit.text
          note = `rewrite ${VERSION}: ${edit.reason}`
        }
      } else if (enhanced.template_id === EXPAND_ID) {
        text = e.text
        if (e.text.trim().length <= EXPAND_MAX_CHARS) {
          const r = await $.model.complete({ model: 'haiku', system: EXPAND_RULES, prompt: e.text, maxTokens: 400, effort: 'low', timeoutMs: 8000 })
          const edit = r.isAnswered ? acceptExpansion(e.text, r.text) : undefined
          if (edit) {
            text = edit.text
            note = `expand ${EXPAND_VERSION}: ${edit.reason}`
          }
        }
      }
      if (enhanced.advice) $.ui.log(`whetstone: ${enhanced.advice}`)
      if (enhanced.task_kind) last = { enhanced, text, ...(pause !== undefined && !correction ? { personMs: pause } : {}) }
      if (text !== e.text) {
        const ts = Date.now()
        const row: Shown = { id: `${ts}`, ts, kind: enhanced.task_kind ?? '', arm: enhanced.held_out ? 'held out' : (enhanced.template_id ?? ''), template: `${enhanced.template_id}@${enhanced.template_version}`, original: e.text, sent: text }
        await $.store.set(`sent:${row.id}`, row)
        const keys = (await $.store.keys()).filter(k => k.startsWith('sent:')).sort()
        for (const k of keys.slice(0, Math.max(0, keys.length - KEEP_RECORDS))) await $.store.delete(k)
        $.ui.log(`whetstone (${enhanced.task_kind}, ${enhanced.template_id}): ${note} Original kept (/whetstone). Start with * to send as typed.`)
      }
      return next({ ...e, text })
    } catch {
      last = undefined
      return next(e)
    }
  }).catch(($, e, next) => next(e)) // fail open: a broken hook sends the prompt as typed

  on('turn.start', async ($, e, next) => {
    // The first main-loop turn after the submit is the prompt's own; its text can differ from what was sent (context added around it).
    if (!e.agentId && last && !last.turnId) {
      last.turnId = e.turnId
      byTurn.set(e.turnId, last)
    }
    if (!e.agentId) running = byTurn.get(e.turnId)
    return next(e)
  })

  // A subagent's prompt is a request of its own, linked to the prompt that started it so the parent is scored by its children.
  // Only a server template for the kind is applied; the model rewrite and the expander are for what a person types.
  on('agent.spawn', async ($, e, next) => {
    let enhanced: Enhanced | undefined
    let text = e.prompt
    try {
      ws ??= whetstone($)
      const client = ws
      const cwd = await $.session.cwd().catch(() => '')
      const why = e.isTeammate || e.fork ? 'inherits context' : skipReason(e.prompt, cwd, false, undefined, false, false)
      if (!why) {
        const parent = e.parentAgentId ? byAgent.get(e.parentAgentId) : running
        const remote = await $.process.run(['git', 'remote', 'get-url', 'origin'], { cwd, timeoutMs: 1500 }).catch(() => undefined)
        const repository = remote?.exitCode === 0 ? repositoryOf(remote.stdout) : undefined
        enhanced = await Promise.race([
          client.enhance(e.prompt, { client: CLIENT, session_id: sessionId, ...(repository ? { repository } : {}), ...(parent ? { parent_request_id: parent.enhanced.request_id } : {}) }, DEADLINE_MS),
          $.clock.sleep(DEADLINE_MS + 300).then(() => undefined),
        ])
        if (enhanced?.request_id && enhanced.changed && enhanced.template_id !== TEMPLATE_ID && enhanced.template_id !== EXPAND_ID) {
          text = enhanced.prompt
          $.ui.log(`whetstone (${enhanced.task_kind}, subagent): ${enhanced.reason}`)
        }
      }
    } catch {
      enhanced = undefined
      text = e.prompt
    }
    const result = await next(text === e.prompt ? e : { ...e, prompt: text })
    if (enhanced?.request_id && 'agentId' in result && result.agentId) byAgent.set(result.agentId, { enhanced, text })
    return result
  }).catch(($, e, next) => next(e))

  on('turn.complete', async ($, e, next) => {
    const sent = e.agentId ? byAgent.get(e.agentId) : byTurn.get(e.turnId)
    const result = await next(e)
    if (!e.agentId) {
      running = undefined
      doneAt = await $.clock.now()
      void quietly(async () => { hint = await refreshHint($, hint) })
    }
    if (sent && ws) {
      if (e.agentId) byAgent.delete(e.agentId)
      else byTurn.delete(e.turnId)
      const u = e.usage
      const outcome = {
        completed: !e.isAborted,
        duration_ms: Math.round(e.durationMs),
        ...(sent.personMs !== undefined ? { person_ms: Math.round(sent.personMs) } : {}),
        ...(u ? { tokens_in: u.input_tokens, tokens_out: u.output_tokens, cache_read_tokens: u.cache_read_input_tokens, cache_write_tokens: u.cache_creation_input_tokens, model: u.model } : {}),
      }
      const client = ws
      try {
        await Promise.race([client.feedback(sent.enhanced.request_id, outcome), $.clock.sleep(2000)])
      } catch (err) {
        $.ui.log(`whetstone: feedback failed (${String(err)})`, { to: 'debug' })
      }
    }
    return result
  })

  on('command.run', { command: 'whetstone' }, async $ => {
    const keys = (await $.store.keys()).filter(k => k.startsWith('sent:')).sort().slice(-10).reverse()
    if (!keys.length) return { text: 'whetstone has not changed a prompt yet.' }
    const rows = await Promise.all(keys.map(k => $.store.get(k) as Promise<Shown>))
    return {
      text: rows
        .map(x => `${new Date(x.ts).toISOString().slice(0, 16)} ${x.kind} ${x.template}\n  was: ${x.original}\n  sent: ${x.sent}`)
        .join('\n\n'),
    }
  })
}
