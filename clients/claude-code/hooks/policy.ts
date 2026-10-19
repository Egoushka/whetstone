// Pure parts of the whetstone mod: which prompts it leaves alone and whether a prompt corrects the one before.

export const MIN_CHARS = 40
export const MAX_CHARS = 2000

const SECRET = /BEGIN [A-Z ]*PRIVATE KEY|AKIA|eyJ|gh[pousr]_|glpat-|xox[abprs]-|sk-|pass|pwd|api[ _-]?key|secret|token|bearer/i
export const EMPLOYER_DIR = '/Projects/work'
const BYPASS = /^\s*[*\/!]/

/** Why this prompt does not go to whetstone, or undefined when it may. */
export function skipReason(text: string, cwd: string, hasAttachments: boolean, originKind: string | undefined, isCommand: boolean, isMeta = false): string | undefined {
  if (originKind !== undefined && originKind !== 'composer' && originKind !== 'bridge') return 'not typed by the user'
  if (isCommand) return 'slash command'
  if (isMeta || text.includes('<system-reminder>')) return 'not typed by the user'
  if (BYPASS.test(text)) return 'bypass prefix'
  if (hasAttachments) return 'has attachments'
  if (text.length < MIN_CHARS) return 'short'
  if (text.length > MAX_CHARS || text.includes('<pasted_content')) return 'long or pasted'
  if (cwd.includes(EMPLOYER_DIR)) return 'employer work'
  if (SECRET.test(text)) return 'may hold a credential'
  return undefined
}

const CORRECTION = new RegExp(
  String.raw`^\s*(no\b|nope\b|wrong\b|not (what|that|like|quite)\b|that('s| is) (not|wrong|incorrect)\b|i (said|meant|asked|wanted)\b|again\b|still\b|you (didn'?t|forgot|missed|ignored|broke)\b|undo\b|revert (that|it)\b|ні(?![а-яіїєґ])|не те(?![а-яіїєґ])|неправильно(?![а-яіїєґ])|я (казав|просив|мав на увазі)(?![а-яіїєґ])|знову(?![а-яіїєґ])|досі(?![а-яіїєґ]))`,
  'i',
)

/** True when the prompt reads as a correction of the answer before it. A guess by opening words, recorded as `asked_again`. */
export function isCorrection(text: string): boolean {
  return CORRECTION.test(text)
}

/** `owner/repo` from a git remote URL, or undefined. */
export function repositoryOf(remote: string): string | undefined {
  const m = remote.trim().match(/[:/]([\w.-]+\/[\w.-]+?)(?:\.git)?$/)
  return m?.[1]
}

const LEVEL = /^\s*\*(cheap|max)\b[ \t]*/i

/** A `*cheap` or `*max` prefix is the price level the person chose (`cheap`, `best`); the prompt comes back without it. */
export function levelOf(text: string): { level?: 'cheap' | 'best'; text: string } {
  const m = text.match(LEVEL)
  return m ? { level: m[1]!.toLowerCase() === 'cheap' ? 'cheap' : 'best', text: text.slice(m[0].length) } : { text }
}
