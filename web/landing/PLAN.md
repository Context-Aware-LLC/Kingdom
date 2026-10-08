# kingdomofbadideas.com site plan (2026-09-30)

One Cloudflare Worker: static pages in `public/`, a script that owns `/api/*` only. The company
site is nordaware.com and is separate.

1. **Pre-registration. DONE 2026-09-30, live.** Double opt-in Early Access form, D1 `kobi-site`,
   Turnstile, Resend confirmation. CSV export is `scripts/export-csv.mjs`. Decisions: D1 plus CSV
   is the list, no external tool.
2. **Card gallery. Teaser only** (Sam, 2026-09-30). The generator publishes an allowlist of
   approved cards and nothing else, because card text still changes. Player-facing names only
   ("Pendingham", never "Emberfall"). The page shows art, name, class, rarity and text, and no cost or stats (Sam, 2026-09-30: they do not read to a newcomer without the card frame). Built by `scripts/build-gallery.py` from shipped card data and the art.
3. **Support and FAQ.** FAQ drafted from `docs/pub-docs/` (Early Access, connection required,
   accounts, deletion). Support form on the Worker with the same Turnstile check, mailing support@.
   Link the existing delete-account page.
4. **Stubs.** Shared nav and layout. Events, Leaderboards and How-to say "coming at launch" and are
   `noindex` until real.
5. **Deploy on push. DONE 2026-09-30.** `.github/workflows/deploy-site.yml`. It also promotes the
   version, because `wrangler deploy` here only uploads one (no workers.dev or route targets).

6. **Hero pages. DONE 2026-10-07.** `/heroes/<name>`, one per hero, linked from the home roster:
   playstyle, hero power, and that class's allowlisted cards. The allowlist grew to four per class
   (Sam, 2026-10-07: reveals may go public and change, there is no traffic yet).

Phases 2-4 are built (support is the support@ email link; the form is optional).
