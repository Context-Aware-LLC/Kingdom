---
name: delete-account
description: Handle a "delete my account" request from the Kingdom of Bad Ideas support mailbox - verify the sender, produce a dry-run deletion plan for Sam, and only after Sam approves that exact plan run the deletion tool. Use when a support mail asks for account or data deletion, or Sam asks to process one.
---

Read and follow the canonical project skill at
`.agents/skills/delete-account/SKILL.md`, resolved from the repository root. Pass through the
user's request. That skill owns the dry run, the approval gate and the data rules; keep this
entry point as a thin adapter.
