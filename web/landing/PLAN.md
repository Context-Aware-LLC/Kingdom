# kingdomofbadideas.com site plan (2026-09-30)

One Cloudflare Worker: static pages in `public/`, a script that owns `/api/*` only. The company
site is nordaware.com and is separate.

1. **Pre-registration. DONE 2026-09-30, live.** Double opt-in Early Access form, D1 `kobi-site`,
   Turnstile, Resend confirmation. CSV export is `scripts/export-csv.mjs`. Decisions: D1 plus CSV
   is the list, no external tool.
2. **Card gallery. Teaser only** (Sam, 2026-09-30). The generator publishes an allowlist of
   approved cards and nothing else, because card text still changes. Player-facing names only
   ("Pendingham", never "Emberfall"). A `cards.json` built from shipped card data and the art webps.
3. **Support and FAQ.** FAQ drafted from `docs/pub-docs/` (Early Access, connection required,
   accounts, deletion). Support form on the Worker with the same Turnstile check, mailing support@.
   Link the existing delete-account page.
4. **Stubs.** Shared nav and layout. Events, Leaderboards and How-to say "coming at launch" and are
   `noindex` until real.
5. **Deploy on push.** GitHub Action. A deploy must also run `wrangler versions deploy <id>@100`:
   `wrangler deploy` here uploads a version but does not activate it (no workers.dev or route
   targets in the config).
