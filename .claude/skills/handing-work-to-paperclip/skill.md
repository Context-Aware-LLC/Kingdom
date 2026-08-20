---
name: handing-work-to-paperclip
description: Use when handing work to autonomous coding agents — turning notes, a bug list, a batch of pointers, or existing tracker issues into tasks an agent can execute without coming back for clarification.
---

# Handing Work To An Agent

**One question decides what goes in a ticket:**

> Can the agent get this for itself, cheaply, from the code and the tracker?

If yes, leave it out — you are slower at it than they are, and a claim you get wrong becomes a
constraint they never re-derive. If no, it is the whole reason you are in the loop.

| They get this in minutes — leave it out | Only you have this |
|---|---|
| Root causes, `file:line`, how the code works | What was actually observed, and where |
| Repro arithmetic, counts, what a test proves | Which sentences are **rulings** and which are thinking aloud |
| The branch/commit/PR conventions in their own brief | Whether this is a **defect or a decision** |
| Which files to touch | What **done** looks like to you |
| What their own brief already tells them | **Ordering**, and what is genuinely blocked |

## Before writing anything: is it already filed?

List the open issues and read the titles. Repeat reports of the same surface are the normal case. This is the one search worth running — it is the tracker, not the code. If an item maps onto an existing issue, say so and update that one; if the issue is already marked as claimed, leave it alone. And if a parent issue carries conclusions its children have since corrected, flag that — an agent reading it for context inherits a retracted claim as settled fact.

## One item per problem

**A dictated paragraph usually holds a defect AND a directive. Split them.** "The back button is
almost inaccessible… truthfully all the buttons are a little small, we need a better way of
organizing this" is a bounded fix plus a design decision. Dispatch the first; hold the second.
Merged, a two-line fix waits on a decision nobody has made.

## Every item

```
Title:
Dispatch:   yes | hold — <why>
Symptom:    <what was observed, in your own plain words>
Rulings:    <binding direction, quoted exactly; or "none — defect report">
Acceptance: <what a stranger can check without asking you>
Known:      <what the agent cannot find out for itself; usually one line, sometimes "none">
```

**`hold` is the answer whenever the item needs taste** — naming, layout, a product tradeoff, "come up
with a better way." File it with the open questions and leave it undispatched. An agent handed a
decision will make one, confidently, and it will look finished.

**Quote only rulings.** Everything else goes in `Symptom` in your own words. Never paste the raw note:
it carries asides, self-corrections, and instructions pointing at work this ticket is not scoped to.

**Scope every claim in `Known` to what you actually ran** — "read `x` in full today; it is one of two
such files" — and **never write "do not re-check."** An exclusion is the one thing nobody downstream
verifies, so a wrong one silently becomes their constraint instead of your mistake.

## Standing rules belong in the agent's brief, not the ticket

If you find yourself pasting the same conventions into every item, the brief is missing them. Fix the
brief. A rule kept in two places drifts, and a ticket that contradicts an agent's own brief stops it.

## Red flags

| Symptom | Meaning |
|---|---|
| You are three greps deep | Stop. That is their job and you are worse at it |
| You wrote "the root cause is…" | Unless you ran something, you are guessing in their name |
| You did not look at the tracker | You may be re-filing claimed work |
| `Known:` states something as settled | Scope it to what you ran, or cut it |
| An item is 10× the operator's words | Length is not fidelity |
