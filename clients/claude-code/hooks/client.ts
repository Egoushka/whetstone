// whetstone's MCP tools over HTTP (streamable transport), by hand: initialize once, then tools/call. The key stays in memory.

export const URL = 'http://127.0.0.1:7340/v1/mcp'
export const KEYCHAIN_ITEM = 'whetstone-api-key'

export type Enhanced = {
  prompt: string
  changed: boolean
  template_id?: string | null
  template_version?: string | null
  reason: string
  task_kind?: string | null
  request_id: string
  held_out: boolean
}

export type Outcome = {
  completed?: boolean
  tokens_in?: number
  tokens_out?: number
  cache_read_tokens?: number
  cache_write_tokens?: number
  duration_ms?: number
  asked_again?: boolean
  model?: string
}

type Fetch = (url: string, init?: { method?: string; headers?: Record<string, string>; body?: string }) => Promise<{ status: number; ok: boolean; headers: Record<string, string>; text: string }>

/** The JSON-RPC message with this id from a JSON body or a server-sent-events body. */
export function parseReply(text: string, id: number): any {
  const bodies = text.trimStart().startsWith('{') ? [text] : text.split('\n').filter(l => l.startsWith('data:')).map(l => l.slice(5))
  for (const b of bodies) {
    try {
      const m = JSON.parse(b)
      if (m.id === id) return m
    } catch {}
  }
  return undefined
}

/** What the client needs of the host: `$.http` and `$.process`. */
export type Host = {
  http: { fetch: Fetch }
  process: { run: (argv: readonly string[], init?: { cwd?: string; timeoutMs?: number }) => Promise<{ exitCode: number; stdout: string }> }
}

export function whetstone(host: Host, url = URL) {
  const fetch = (u: string, i?: Parameters<Fetch>[1]) => host.http.fetch(u, i)
  let cached: Promise<string | undefined> | undefined
  // Read once from the login keychain and kept in memory only.
  const getKey = () =>
    (cached ??= host.process.run(['security', 'find-generic-password', '-s', KEYCHAIN_ITEM, '-w'], { timeoutMs: 3000 }).then(
      r => (r.exitCode === 0 ? r.stdout.trim() || undefined : undefined),
      () => undefined,
    ))
  let session: string | undefined
  let next = 1

  const post = async (key: string, body: object) => {
    const headers: Record<string, string> = {
      authorization: `Bearer ${key}`,
      'content-type': 'application/json',
      accept: 'application/json, text/event-stream',
    }
    if (session) headers['mcp-session-id'] = session
    return fetch(url, { method: 'POST', headers, body: JSON.stringify(body) })
  }

  const open = async (key: string) => {
    session = undefined
    const id = next++
    const r = await post(key, { jsonrpc: '2.0', id, method: 'initialize', params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'whetstone-mod', version: '0.1.0' } } })
    if (!r.ok) throw new Error(`initialize ${r.status}`)
    session = r.headers['mcp-session-id']
    await post(key, { jsonrpc: '2.0', method: 'notifications/initialized' })
  }

  const call = async (name: string, args: object): Promise<any> => {
    const key = await getKey()
    if (!key) throw new Error('no key')
    for (let attempt = 0; attempt < 2; attempt++) {
      if (!session) await open(key)
      const id = next++
      const r = await post(key, { jsonrpc: '2.0', id, method: 'tools/call', params: { name, arguments: args } })
      if (r.status === 404 || r.status === 400) {
        session = undefined // the server forgot the session (restart): open another once
        continue
      }
      if (!r.ok) throw new Error(`${name} ${r.status}`)
      const m = parseReply(r.text, id)
      if (!m || m.error || m.result?.isError) throw new Error(`${name} refused`)
      return m.result.structuredContent ?? {}
    }
    throw new Error(`${name} no session`)
  }

  return {
    enhance: (prompt: string, context: { repository?: string; client: string }, deadlineMs: number) =>
      call('enhance', { prompt, context, deadline_ms: deadlineMs }) as Promise<Enhanced>,
    feedback: (requestId: string, outcome: Outcome) => call('feedback', { request_id: requestId, outcome }),
  }
}
