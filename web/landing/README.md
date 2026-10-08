# kingdomofbadideas.com landing page

Static assets in `public/`, plus a small Worker (`src/worker.js`) that owns `/api/*` only
(`run_worker_first`). `wrangler.jsonc` does not bind the domains: the apex and `www` are attached
in the dashboard, and a zone redirect rule sends `www` to the apex. Pushes to master that touch `web/landing/` deploy automatically (`.github/workflows/deploy-site.yml`).

To deploy by hand from this folder. `wrangler deploy` only UPLOADS a version here (no workers.dev or route
targets in the config), so promote it with the version id it prints:

    npx wrangler deploy
    npx wrangler versions deploy <version-id>@100 -y

Plan and status: `PLAN.md`.

## Early Access pre-registration

Double opt-in. `POST /api/preregister` checks Turnstile, stores the address unconfirmed in D1
(`kobi-site`, table `prereg`), and mails a confirm link; `GET /api/confirm?t=` confirms,
`GET /api/unsubscribe?t=` opts out. The form answers identically for new, pending and
already-confirmed addresses.

Secrets on the Worker: `TURNSTILE_SECRET` (set), `RESEND_API_KEY` (set; without it
sign-ups are stored but no confirmation email goes out). `MAIL_FROM` in `wrangler.jsonc` must be
an address on a domain verified in Resend.

    npx wrangler secret put RESEND_API_KEY
    node scripts/export-csv.mjs > early-access.csv    # confirmed + still subscribed

`public/assets/` holds web encodes of files under `unity/Assets/Art/` (the backdrops,
hero portraits, splash orb, and the Alfa Slab One and Google Sans fonts subset to Latin). When
the art changes, re-encode from those sources rather than editing the encodes.

## Card teaser and hero pages

`python web/landing/scripts/build-gallery.py` writes `public/cards.html`, `public/heroes/*.html`,
the card encodes, and the roster inside `public/index.html` (between the `roster:` markers; edit
`heroes.json`, not that block). `gallery-allowlist.json` is the only gate on which cards are
public: `/cards` shows all of them and each hero page shows its own class's. Hero copy lives in
`heroes.json`; the hero power text is read from the card data, so rebuild after it changes.
A new hero page also needs a line in `public/sitemap.xml`.
