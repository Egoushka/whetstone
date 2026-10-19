import { EMPLOYER_DIR } from './policy'
import type { Host } from './client'

// The `last_ci_failure` hint: which workflow, job and step failed in the newest CI run of the branch. Read with `gh`, off the
// hot path; a hint is never worth a slow prompt.

const MAX = 500

type Step = { name?: string; conclusion?: string }
type Job = { name?: string; conclusion?: string; steps?: Step[] }

/** The newest run of `gh run list --json databaseId,conclusion,workflowName`, when it failed. */
export function failedRun(listJson: string): { id: number; workflow: string } | undefined {
  try {
    const run = JSON.parse(listJson)?.[0]
    return run?.conclusion === 'failure' && Number.isInteger(run.databaseId) ? { id: run.databaseId, workflow: String(run.workflowName ?? '') } : undefined
  } catch {
    return undefined
  }
}

/** `workflow / job / step` of the first failed job in `gh run view --json jobs`, or just the workflow when no job says. */
export function ciFailure(workflow: string, viewJson: string): string | undefined {
  let jobs: Job[] = []
  try {
    jobs = JSON.parse(viewJson)?.jobs ?? []
  } catch {}
  const job = jobs.find(j => j.conclusion === 'failure')
  const step = job?.steps?.find(s => s.conclusion === 'failure')
  const text = [workflow, job?.name, step?.name].filter(Boolean).join(' / ')
  return text ? text.slice(0, MAX) : undefined
}

export type Hint = { key: string; at: number; value?: string }

const TTL_MS = 5 * 60_000

/** The failing CI step of the branch's newest run, or `prev` while it is fresh. Called off the hot path, never awaited by a prompt. */
export async function refreshHint($: Host & { session: { cwd: () => Promise<string> }; clock: { now: () => Promise<number> } }, prev: Hint | undefined): Promise<Hint | undefined> {
  const cwd = await $.session.cwd().catch(() => '')
  if (!cwd || cwd.includes(EMPLOYER_DIR)) return prev
  const branch = (await $.process.run(['git', 'rev-parse', '--abbrev-ref', 'HEAD'], { cwd, timeoutMs: 1500 })).stdout.trim()
  const now = await $.clock.now()
  const key = `${cwd}@${branch}`
  if (!branch || branch === 'HEAD' || (prev?.key === key && now - prev.at < TTL_MS)) return prev
  const list = await $.process.run(['gh', 'run', 'list', '--branch', branch, '--limit', '1', '--json', 'databaseId,conclusion,workflowName'], { cwd, timeoutMs: 8000 })
  const run = list.exitCode === 0 ? failedRun(list.stdout) : undefined
  if (!run) return { key, at: now }
  const view = await $.process.run(['gh', 'run', 'view', String(run.id), '--json', 'jobs'], { cwd, timeoutMs: 8000 })
  return { key, at: now, value: view.exitCode === 0 ? ciFailure(run.workflow, view.stdout) : undefined }
}
