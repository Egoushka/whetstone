import { test, expect } from 'claude-code/testing'
import { isCorrection, repositoryOf, skipReason } from './policy'

const long = 'Implement retry with backoff in the upload client for the example service'

test('prompts that never reach whetstone', () => {
  expect(skipReason('go ahead', '/p', false, 'composer', false)).toBe('short')
  expect(skipReason('*' + long, '/p', false, 'composer', false)).toBe('bypass prefix')
  expect(skipReason(long, '/p', true, 'composer', false)).toBe('has attachments')
  expect(skipReason(long, '/p', false, 'peer', false)).toBe('not typed by the user')
  expect(skipReason(long, '/p', false, 'composer', true)).toBe('slash command')
  expect(skipReason(long, '/home/dev/Projects/work/app', false, 'composer', false)).toBe('employer work')
  expect(skipReason('use this token abc please for the thing ' + long, '/p', false, 'composer', false)).toBe('may hold a credential')
  expect(skipReason('<pasted_content id="a">' + long + '</pasted_content>', '/p', false, 'composer', false)).toBe('long or pasted')
  expect(skipReason(long, '/home/dev/Projects/personal/x', false, 'composer', false)).toBeUndefined()
  expect(skipReason(long, '/p', false, undefined, false)).toBeUndefined()
})

test('a correction is found by how the prompt opens', () => {
  for (const t of ['No, I meant the other file', 'That is wrong', "that's not what I asked", 'Still failing after your change', 'Не те, я просив інше', 'you forgot the tests'])
    expect(isCorrection(t)).toBe(true)
  for (const t of ['Now add the tests', 'Looks good, merge it', 'Notice the config also changes', 'Continue with the plan'])
    expect(isCorrection(t)).toBe(false)
})

test('the repository comes from the remote URL', () => {
  expect(repositoryOf('https://github.com/Example/app.git\n')).toBe('Example/app')
  expect(repositoryOf('git@github.com:Example/app.git')).toBe('Example/app')
  expect(repositoryOf('')).toBeUndefined()
})
