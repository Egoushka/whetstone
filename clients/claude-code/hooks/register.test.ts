import { test, expect, mock } from 'claude-code/testing'

const PROMPT = 'Implement retry with backoff in the upload client for the example service'
const usage = { input_tokens: 1, output_tokens: 1, cache_read_input_tokens: 0, cache_creation_input_tokens: 0 }

type Call = { name: string; arguments: any }

// A whetstone that answers `enhance` with the given reply and records every tool call it gets.
function fakeWhetstone(on: any, reply: object | 'down', calls: Call[]) {
  on('process.run', async (_$: any, e: any) => ({ value: { exitCode: 0, stdout: e.argv[0] === 'security' ? 'k\n' : 'https://github.com/Example/app.git\n', stderr: '' } }))
  on('session.cwd', async () => ({ value: '/home/dev/Projects/personal/app' }))
  on('http.fetch', async (_$: any, e: any) => {
    if (reply === 'down') throw new Error('refused')
    const body = JSON.parse(e.init?.body ?? e.body ?? '{}')
    const json = (result: object) => ({ value: { status: 200, ok: true, headers: { 'mcp-session-id': 's1' }, text: JSON.stringify({ jsonrpc: '2.0', id: body.id, result }) } })
    if (body.method === 'initialize') return json({})
    if (body.method === 'tools/call') {
      calls.push(body.params)
      return json({ structuredContent: body.params.name === 'enhance' ? reply : {} })
    }
    return { value: { status: 202, ok: true, headers: {}, text: '' } }
  })
}

const champion = { prompt: PROMPT + '\n\nBefore you report this done, run the build.', changed: true, template_id: 'typed-implement-check', template_version: '1', reason: 'template: typed-implement-check v1 (champion)', task_kind: 'typed/implement', request_id: 'r1', held_out: false }

test('a champion template is sent as the prompt, shown, and the turn is reported', async ($, on) => {
  mock.store(on)
  mock.clock(on)
  const calls: Call[] = []
  fakeWhetstone(on, champion, calls)
  const logged: string[] = []
  on('ui.log', async (_$, e) => (logged.push(JSON.stringify(e)), { value: undefined }))
  let seen = ''
  on('prompt.submit', async (_$, e) => ((seen = e.text), { text: e.text }))
  on('turn.start', async (_$, e) => ({ turnId: e.turnId }))
  on('turn.complete', async (_$, e) => ({ text: e.answer }))
  await $.prompt.submit({ text: PROMPT })
  expect(seen).toBe(champion.prompt)
  expect(logged.join('\n')).toContain('Start with * to send as typed')
  const enhance = calls.find(c => c.name === 'enhance')!
  expect(enhance.arguments.prompt).toBe(PROMPT)
  const { session_id, ...context } = enhance.arguments.context
  expect(context).toEqual({ client: 'claude-code', repository: 'Example/app' })
  expect(session_id).toMatch(/^cc-[a-z0-9]+-[a-z0-9]+$/)
  await $.turn.start({ text: seen, turnId: 't1' })
  await $.turn.complete({ answer: 'done', durationMs: 4200, isAborted: false, turnId: 't1', usage: { ...usage, input_tokens: 10, output_tokens: 20, cache_read_input_tokens: 300, cache_creation_input_tokens: 40, model: 'm' } })
  const fb = calls.find(c => c.name === 'feedback')!
  expect(fb.arguments).toEqual({ request_id: 'r1', outcome: { completed: true, duration_ms: 4200, tokens_in: 10, tokens_out: 20, cache_read_tokens: 300, cache_write_tokens: 40, model: 'm' } })
})

test('the next prompt says whether the last one was corrected', async ($, on) => {
  mock.store(on)
  mock.clock(on)
  const calls: Call[] = []
  fakeWhetstone(on, champion, calls)
  on('ui.log', async () => ({ value: undefined }))
  on('prompt.submit', async (_$, e) => ({ text: e.text }))
  await $.prompt.submit({ text: PROMPT })
  await $.prompt.submit({ text: 'No, I meant the download client' })
  await new Promise(r => setTimeout(r, 20))
  const fb = calls.filter(c => c.name === 'feedback')
  expect(fb.map(c => c.arguments)).toContainEqual({ request_id: 'r1', outcome: { asked_again: true } })
})

test('whetstone down, or a bypass, sends the prompt as typed', async ($, on) => {
  mock.store(on)
  mock.clock(on)
  const calls: Call[] = []
  fakeWhetstone(on, 'down', calls)
  on('ui.log', async () => ({ value: undefined }))
  const seen: string[] = []
  on('prompt.submit', async (_$, e) => (seen.push(e.text), { text: e.text }))
  await $.prompt.submit({ text: PROMPT })
  await $.prompt.submit({ text: '*' + PROMPT })
  expect(seen).toEqual([PROMPT, '*' + PROMPT])
})

test('the model rewrite challenger runs the rewriter and a valid edit is sent', async ($, on) => {
  mock.store(on)
  mock.clock(on)
  const calls: Call[] = []
  const ask = 'The retry loop in ingest.py sleeps too long, make it shorter and keep 3 attempts'
  const edited = 'The retry loop in ingest.py sleeps too long, make the sleep shorter and keep 3 attempts'
  fakeWhetstone(on, { prompt: ask, changed: false, template_id: 'typed-rewrite-haiku', template_version: 'r1', reason: 'template', task_kind: 'typed/implement', request_id: 'r2', held_out: false }, calls)
  on('model.complete', async () => ({ value: { isAnswered: true as const, text: JSON.stringify({ changed: true, text: edited, reason: '"it" was the sleep' }), usage } }))
  on('ui.log', async () => ({ value: undefined }))
  let seen = ''
  on('prompt.submit', async (_$, e) => ((seen = e.text), { text: e.text }))
  await $.prompt.submit({ text: ask })
  expect(seen).toBe(edited)
})

test('the expander keeps a terse prompt as typed and adds a brief under it, and advice is shown', async ($, on) => {
  mock.store(on)
  mock.clock(on)
  const calls: Call[] = []
  const ask = 'fix the flaky upload test in tests/upload.spec.ts'
  const brief = 'Goal: make tests/upload.spec.ts pass reliably.\nStop when it passes ten runs in a row and report what changed.'
  fakeWhetstone(on, { prompt: ask, changed: false, template_id: 'typed-expand', template_version: 'e1', reason: 'template', task_kind: 'typed/debug', request_id: 'r3', held_out: false, advice: 'typed/debug: 30% of the last 40 runs needed rework.' }, calls)
  on('model.complete', async () => ({ value: { isAnswered: true as const, text: JSON.stringify({ changed: true, text: brief, reason: 'spelled out the stop rule' }), usage } }))
  const logged: string[] = []
  on('ui.log', async (_$, e) => (logged.push(JSON.stringify(e)), { value: undefined }))
  let seen = ''
  on('prompt.submit', async (_$, e) => ((seen = e.text), { text: e.text }))
  await $.prompt.submit({ text: ask })
  expect(seen).toBe(`${ask}\n\n${brief}`)
  expect(logged.join('\n')).toContain('needed rework')
})

test('a prompt too long for the expander is sent as typed', async ($, on) => {
  mock.store(on)
  mock.clock(on)
  const calls: Call[] = []
  const ask = 'Refactor the upload client so retries back off exponentially, keep the public API the same, and update the three call sites in the worker'
  fakeWhetstone(on, { prompt: ask, changed: false, template_id: 'typed-expand', template_version: 'e1', reason: 'template', task_kind: 'typed/implement', request_id: 'r4', held_out: false }, calls)
  let asked = false
  on('model.complete', async () => ((asked = true), { value: { isAnswered: true as const, text: '{}', usage } }))
  on('ui.log', async () => ({ value: undefined }))
  let seen = ''
  on('prompt.submit', async (_$, e) => ((seen = e.text), { text: e.text }))
  await $.prompt.submit({ text: ask })
  expect(seen).toBe(ask)
  expect(asked).toBe(false)
})
