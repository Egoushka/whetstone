import { test, expect } from 'claude-code/testing'
import { ciFailure, failedRun } from './ci'

test('the newest run counts only when it failed', () => {
  expect(failedRun('[{"databaseId":7,"conclusion":"failure","workflowName":"ci"}]')).toEqual({ id: 7, workflow: 'ci' })
  expect(failedRun('[{"databaseId":7,"conclusion":"success","workflowName":"ci"}]')).toBeUndefined()
  expect(failedRun('[]')).toBeUndefined()
  expect(failedRun('not json')).toBeUndefined()
})

test('the hint names the workflow, the failed job and its failed step', () => {
  const view = JSON.stringify({ jobs: [{ name: 'lint', conclusion: 'success' }, { name: 'test', conclusion: 'failure', steps: [{ name: 'setup', conclusion: 'success' }, { name: 'dotnet test', conclusion: 'failure' }] }] })
  expect(ciFailure('ci', view)).toBe('ci / test / dotnet test')
  expect(ciFailure('ci', 'garbage')).toBe('ci')
})
