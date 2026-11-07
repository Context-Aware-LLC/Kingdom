# Background delivery

One app contains the 13 **2048 x 1152 Mobile** backgrounds. Phones only load those.
Android tablets (smallest width at least 600 dp), iPads and desktops load the bundled
fallback, then download the matching **2560 x 1440 DesktopTablet** background. Each larger
image has its own remote bundle, so visiting sign-in does not download every loading scene.
The normal Unity bundle cache retains downloads across app launches. A failed request keeps
the fallback; another screen visit can retry after 60 seconds.

The catalog is embedded and versioned with the player. It contains immutable hashed remote
URLs; startup does not contact a catalog server. Keep older objects in R2 while old app
versions remain supported. Never replace a published object with different bytes.

## Hosting (one-time setup)

The existing kingdomofbadideas.com Worker serves read-only `/game-assets/backgrounds/v1/`
URLs from its `GAME_ASSETS` binding, bucket `kobi-game-assets`. The bucket itself stays private.
No account credentials are included in the app. Cloudflare setup/publication requires an
account token with **Workers R2 Storage: Edit** in addition to the existing worker deployment
permission. Do not paste tokens into commands or commit them; Wrangler reads its environment.

1. `npx wrangler r2 bucket create kobi-game-assets` (once; preserve it on later releases).
2. Deploy `web/landing/wrangler.jsonc` and promote the returned version, as in
   `.github/workflows/deploy-site.yml`. The new binding and route must be live before publishing.
3. Run `node --test web/landing/tests/game-assets.test.mjs` for the serving contract.

## Content and player builds

Use the authorized Unity seat. Select the target platform first; Android, iOS and desktop
bundles contain different platform texture formats and cannot be shared across platforms.

1. Run **Kingdom > Build > Background Content (active platform)**, or the batch entry point
   `Kingdom.Editor.KingdomBuild.BuildBackgroundContent` with the desired `-buildTarget`.
   This builds Addressables without an APK/AAB and writes the exact content manifest to
   `unity/ServerData/Backgrounds/v1/<BuildTarget>/background-content.json`.
2. From the repo root, run:

   ```powershell
   python tooling/scripts/publish_background_content.py --manifest unity/ServerData/Backgrounds/v1/Android/background-content.json --publish
   ```

   Without `--publish` it only validates local files and prints a plan. `--verify` checks
   previously published content. The publisher verifies every public file's SHA-256 and
   writes a receipt only after all 13 pass. It refuses to overwrite mismatching content.
3. Build the player normally. The build checks the receipt against the exact current manifest
   and files, and stops if changed art has not been published. An unchanged content rebuild
   reuses the receipt. Content and receipts are generated output and are not committed.

`BackgroundAddressables.Synchronize` preserves this split when groups are regenerated.
It also checks both sets' export dimensions and importer caps. Retired local background bundle
outputs are pruned after a successful content build so they cannot ride into the player.

## Runtime validation

Use a built player: Editor preview loads source files and does not exercise remote delivery.
Check a phone makes zero `/game-assets/` requests; a tablet upgrades after a cold-cache download;
the same tablet retains larger art after an offline restart; and a tablet with an empty cache
still shows mobile fallback art offline. Navigate away during a download and confirm destroyed
overlays are not touched. Inspect the APK/AAB to confirm no `backgrounds_desktoptablet_*.bundle`
is packaged. Headless loader tests cover request sharing, offline fallback, retries and tier
selection, but do not replace this device pass.
