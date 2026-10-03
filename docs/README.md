[![Kingdom CI — determinism gates](https://github.com/mrsonord2240/tcg-hearthlike/actions/workflows/ci.yml/badge.svg)](https://github.com/mrsonord2240/tcg-hearthlike/actions/workflows/ci.yml)

The maintained documentation is organized by audience and purpose:

| Root | Audience | Contract |
|---|---|---|
| [`pub-docs/`](pub-docs/) | Players and community | Concise, publishable rules and policies. No internal implementation, security findings, stale plans, or unannounced specifics. |
| [`dev-docs/`](dev-docs/) | Developers and project contributors | Concise current contracts, architecture, pipelines, operating guidance, and explicitly labeled open work. |
| [`hist-docs/`](hist-docs/) | Maintainers tracing project history | Loss-preserving snapshots, decisions, drafts, audits, milestone records, and superseded or repetitive context. Not a source of current truth. |

Information may belong to more than one audience. Reuse the fact, but write it at the audience's level: a player-facing rule may also appear as an implementation contract, while the historical tree preserves the dated source and rationale behind both.

## Maintenance rules

1. Give each fact one topic-level home within an audience tree; link to it instead of repeating it across that tree.
2. Keep `pub-docs/` and `dev-docs/` short. Move dated reasoning, session residue, replaced proposals, and exhaustive audit evidence to `hist-docs/`.
3. Label planned, open, unverified, and superseded material. Do not phrase it as current behavior.
4. Treat executable code, committed data, tests, and deployment configuration as the final check on implementation claims.
5. Do no preserve historical records in place. Move them to the hist-docs and replace with up to date information.

## Cutover status

 Current guidance now lives only in `dev-docs/` and `pub-docs/`; detailed legacy citations point directly to relevant entries in `hist-docs`.
