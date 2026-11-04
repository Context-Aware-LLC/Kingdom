---
name: note
description: Record a design or balance note Sam dictates into the right design doc, dated and in his words. Use when he says "note that", "record this", or dictates a card/class/feature idea.
---

# Note

Sam dictates (often through Wispr Flow), so fix transcription slips — misheard card and class
names, run-ons — and **nothing else**. His wording is the note; do not improve, expand or soften it.

## Where it goes

| The note is about | Append to |
|---|---|
| a card or class idea, an identity gap | `docs/dev-docs/design/card-design-ideas.md`, under that class's `## CLASS` heading (`## NEUTRAL` for neutrals) |
| a balance change, a number, a card edit to try | the newest `docs/dev-docs/design/class-balance-proposals-*.md`, in its `## Sam's notes, <date>` section — add one for today if the newest is another day |
| whimsy: voice lines, banter, jokes, flavour | the newest `docs/dev-docs/design/whimsy-pass-*.md` |
| anything else for the game | `docs/sam/sams-scratchpad.md` |

Resolve card names against the card data (the JSON is minified: `git grep -n '"name":"<name>"' -- unity/Assets/CardData`)
before writing, so a misheard name is caught here rather than in the doc. If a note splits across
two rows, split it. If the destination is genuinely unclear, write it to your best guess and say
which in the reply — do not ask first.

## Format

```
- **Sam, YYYY-MM-DD:** <the note>
```

A note that **decides** something (a ruling, a cut, a direction) also goes to the brain as a
timeline entry on `projects/kingdom/<subject>` — see the brain rules in the global CLAUDE.md.

## Then

Commit the doc by explicit path (`git commit --only <path>`) and push, per the standing git grant.
Every target above is in the private docs repo, reached through a junction: commit with
`git -C "C:\Users\User\Documents\Project Docs\Kingdom\docs"` and the path relative to it
(`sam/sams-scratchpad.md`), never in the game repo, which ignores these files.
Reply in one line: where it went. No restating the note.
