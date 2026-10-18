import type { Register } from 'claude-code'
import { RULES, TEMPLATE_ID, VERSION, accept } from './rewrite'
import { whetstone, type Enhanced } from './client'
import { isCorrection, repositoryOf, skipReason } from './policy'

const CLIENT = 'claude-code'
const DEADLINE_MS = 1200
const KEEP_RECORDS = 200
const typed = (origin: string | undefined) => origin === undefined || origin === 'composer' || origin === 'bridge'

type Sent = { enhanced: Enhanced; text: string; turnId?: string }
type Shown = { id: string; ts: number; kind: string; arm: string; template: string; original: string; sent: string }

// Typed prompts go through whetstone: it names a kind, picks an arm (a template, the model rewrite, or nothing held out) and
// stores the request; when the turn ends the mod reports how it went. Every failure sends the prompt as typed.
export const register: Register = on => {
  let ws: ReturnType<typeof whetstone> | undefined
  let last: Sent | undefined
  const byTurn = new Map<string, Sent>()

  on('session.start', async ($, e, next) => {
    ws = whetstone($)
    await $.command.register({ name: 'whetstone', description: 'Show the last prompts whetstone changed, with the originals' })
    return next(e)
  })

  const quietly = async (work: () => Promise<unknown>) => {
    try {
      await work()
    } catch {}
  }

  on('prompt.submit', async ($, e, next) => {
    ws ??= whetstone($)
    const client = ws
    // The previous typed prompt's answer is judged by how this one opens, whatever this one is.
    if (last && typed(e.origin?.kind) && e.command === undefined) {
      const before = last
      last = undefined
      void quietly(() => client.feedback(before.enhanced.request_id, { asked_again: isCorrection(e.text) }))
    }

    const cwd = await $.session.cwd().catch(() => '')
    const why = skipReason(e.text, cwd, (e.attachments?.length ?? 0) > 0, e.origin?.kind, e.command !== undefined)
    if (why) return next(e)
    try {
      const remote = await $.process.run(['git', 'remote', 'get-url', 'origin'], { cwd, timeoutMs: 1500 }).catch(() => undefined)
      const repository = remote?.exitCode === 0 ? repositoryOf(remote.stdout) : undefined
      const enhanced = await Promise.race([
        client.enhance(e.text, { client: CLIENT, ...(repository ? { repository } : {}) }, DEADLINE_MS),
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
      }
      if (enhanced.task_kind) last = { enhanced, text }
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
    if (!e.agentId && last && !last.turnId && e.text.trim() === last.text.trim()) {
      last.turnId = e.turnId
      byTurn.set(e.turnId, last)
    }
    return next(e)
  })

  on('turn.complete', async ($, e, next) => {
    const sent = !e.agentId ? byTurn.get(e.turnId) : undefined
    const result = await next(e)
    if (sent && ws) {
      byTurn.delete(e.turnId)
      const u = e.usage
      const outcome = {
        completed: !e.isAborted,
        duration_ms: Math.round(e.durationMs),
        ...(u ? { tokens_in: u.input_tokens, tokens_out: u.output_tokens, cache_read_tokens: u.cache_read_input_tokens, cache_write_tokens: u.cache_creation_input_tokens, model: u.model } : {}),
      }
      const client = ws
      await quietly(() => Promise.race([client.feedback(sent.enhanced.request_id, outcome), $.clock.sleep(2000)]))
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
