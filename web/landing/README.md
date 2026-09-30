# kingdomofbadideas.com landing page

A static site served by the Cloudflare Worker `kingdomofbadideas`, which serves static assets
only and has no script. `wrangler.jsonc` binds it to `kingdomofbadideas.com` and `www`, and a
zone redirect rule sends `www` to the apex. Nothing deploys it on push.

Deploy from this folder:

    npx wrangler deploy

Only `public/` is published.

`public/assets/` holds web encodes of files under `unity/Assets/Art/` (the backdrops,
hero portraits, splash orb, and the Alfa Slab One and Google Sans fonts subset to Latin). When
the art changes, re-encode from those sources rather than editing the encodes.
