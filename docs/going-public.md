# Going public

The repository is private on GitHub until the maintainer marks it public, and is developed as a public one from the
first commit: nothing in it, in its history, in a commit message or in a PR may need hiding later. This page is the
list to run through on the day it flips. Flipping is the maintainer's decision alone.

## Always, from now on

- No real prompt, name, hostname, IP address, key alias, employer or project name, home path or session id in a file
  or a commit message. The `.private-terms` denylist and gitleaks run on every commit and in CI.
- Tracker keys appear only as `(WHET-12)` at the end of a PR title; no tracker URLs.

## Before flipping

1. History scan: `gitleaks git --redact -v`, and a search of the full history with the `.private-terms` patterns
   (`git log -p --all`, not only the tip).
2. Files a public project needs and this one lacks: `CONTRIBUTING.md` (DCO or CLA decision, as chargehand's),
   `CODE_OF_CONDUCT.md`, issue and PR templates, a `TRADEMARK.md` only if the name is to be protected.
3. A README that says what works today, not what is planned; the status line matches ROADMAP.md.
4. Repository settings: branch protection on `main` (required `ci`), private vulnerability reporting on, Dependabot
   and secret scanning on, squash-merge only, delete branch on merge. The settings of `Egoushka/chargehand` are the
   model.
5. Name check: `whetstone` is free on this GitHub account; check the package registries (NuGet, npm, PyPI) and the
   MCP Registry before a first release, and search for an existing product of the same name.
6. A first release with notes (chargehand's release workflow is the model), so the first thing a visitor sees is a
   tag, not a bare `main`.
