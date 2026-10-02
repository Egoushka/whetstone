# whetstone

A prompt enhancer that learns from you. You write a prompt; whetstone returns a better one, built from your own past
prompts that went well, and learns from what happened next.

**Status: not started.** This repository holds the vision, the roadmap, the first spec and the contract. There is no
code yet.

## How it works

1. A client (first: [chargehand](https://github.com/Egoushka/chargehand)) calls `enhance` with your prompt and the
   context you allow: repository, commit, kind of task.
2. whetstone finds your past prompts most like it that went well, and returns a rewrite, the template it used and why.
3. The client shows the rewrite as a diff. You send the rewrite or your original.
4. After the run the client calls `feedback` with the outcome: rewrite accepted or not, model overridden or not, the
   run's score and cost. That outcome is what whetstone learns from.

Your prompts stay in your whetstone. Nothing is shared between users, and no prompt is ever committed to this
repository.

## Contract

Two MCP tools, versioned under [`schemas/`](schemas/):

| tool | in | out |
|---|---|---|
| `enhance` | prompt, context | prompt, template id and version, reason, task kind |
| `feedback` | template id and version, outcome | nothing |

If whetstone is slow, down or has nothing to offer, the client sends the original prompt. whetstone is never in the
path of a run succeeding.

## Read next

- [VISION.md](VISION.md): why this exists and what it will not do.
- [ROADMAP.md](ROADMAP.md): goals in order.
- [docs/specs/2026-10-02-first-version-design.md](docs/specs/2026-10-02-first-version-design.md): the first version.
- [docs/privacy.md](docs/privacy.md): what is stored and how to delete it.
- [docs/evaluation.md](docs/evaluation.md): how we know it helps.
- [docs/adr/](docs/adr/): decisions.
- [docs/going-public.md](docs/going-public.md): the repository is developed as a public one and made public when the maintainer decides.

## License

Apache-2.0.
