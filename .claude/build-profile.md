# Build profile — Kingdom of Bad Ideas (Android)

Read by the global `build` skill (`~/.claude/skills/build/SKILL.md`). Detail on modes, Sentry
jobs and the naming contract: [android-builds.md](../docs/dev-docs/operations/android-builds.md)
(lives in the docs repo; see CLAUDE.md on the junction).

Another platform or output requested → establish that target first; never substitute Android.

## Purposes

Every command runs from the build checkout: `pwsh tools/ci/build-android.ps1 <args>`.

| Purpose | Command args | Artifact (in build checkout) | Signing | Spends version | Install? |
| --- | --- | --- | --- | --- | --- |
| Quick phone check | `-Fast` | `unity/Builds/Android/Kingdom-fast.apk` | debug | no | ask |
| Release-like phone test | `-DeferSymbolUpload` | `unity/Builds/Android/Kingdom.apk` | debug | no | ask |
| Performance capture | `-PerfCapture -DeferSymbolUpload` | `unity/Builds/Android/Kingdom.apk` | debug | no | ask |
| Google Play release | `-PlayRelease -DeferSymbolUpload` | `unity/Builds/Android/KingdomOfBadIdeas.aab` | upload key `kobi-upload` | yes | only if the request mentions a phone |

Recommend the quick phone check for ordinary visual/gameplay iteration. Bare `release` is
ambiguous between release-like phone test and Play; ask. `-Fast` is unsuitable for performance
numbers. Perf capture needs a checkout with no uncommitted changes. Add `-Clean` only when asked
or cache corruption is evidenced. Timeout 60 min (`-TimeoutMinutes N`).

A Play bundle plus a phone install means both deliverables: the AAB, and an APK set or universal
APK generated from it with bundletool (`Phone/Play/`).

## Preconditions

- **Build in the shared headless Editor `C:\claude-editor`**, never Sam's open Editor. Read
  `C:\claude-editor\EDITOR-LOCK.md` first; OCCUPIED by someone else → do not touch the checkout,
  report and ask. Claim by editing its status block (holder, branch, since, purpose).
- Point it at the commit being built: `git -C C:\claude-editor fetch` then
  `checkout --detach <commit>`, and confirm it actually contains the requested changes.
- No `Unity.exe` running on that path and no `unity/Temp/UnityLockfile`.
- Play builds: `KINGDOM_UPLOAD_KEYSTORE_PASS` must be set (the harness checks it before Unity
  launches).

## Verification

- Harness exit 0 and its `SUCCESS` line; `[KingdomBuild] Player build Succeeded: 0 error(s)` and
  no `Shader error in` in the Unity log the harness prints (`build-android: log ...`).
- SHA-256 of the artifact equals `apkSha256` in the adjacent `<artifact>.build.json`, and
  `commit` there is the intended one with `dirty: false`.
- Tools under `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Data\PlaybackEngines\AndroidPlayer\`:
  `OpenJDK\bin\{java,keytool,jarsigner}.exe`, `NDK\toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-{readelf,objcopy}.exe`;
  bundletool at `C:\Users\User\.android\bin\bundletool.jar`; aapt from the Android SDK for APKs.
- AAB: `bundletool validate`; `dump manifest --module base` → package `com.contextaware.kobi`,
  `android:versionName`/`android:versionCode` (namespaced xpath), not debuggable.
- Signer: `keytool -printcert -jarfile <aab> -keystore C:\Users\User\.keystore\KingdomOfBadIdeas\kobi-upload.keystore -storepass:env KINGDOM_UPLOAD_KEYSTORE_PASS`.
  The `-keystore` is required: without it keytool falls back to `~/.keystore`, a directory here,
  and exits 1 after printing. Expected SHA-256
  `38:21:4E:72:51:30:F7:E1:D0:FD:26:8C:46:46:1B:C1:22:F8:71:07:D3:3A:44:BA:79:2C:02:73:B8:16:12:BA`.
- Native symbols: every `base/lib/arm64-v8a/*.so` in the AAB has a symbol file with the same
  ELF Build ID. `libsentry*.so` ship pre-stripped from the Sentry AAR; record their Build ID and
  AAR hash instead.
- Manifest features declared in `unity/Assets/Plugins/Android/AndroidManifest.xml` (e.g.
  `android.hardware.screen.landscape` required=false) must appear in the built manifest; that
  file is a base Unity overwrites, so check the build, not the file.

## Delivery

- Root: `C:\Users\User\Documents\tcg-hearthlike\unity\Builds\Android`, even when Unity ran in
  `C:\claude-editor` (whose output is intermediate, not delivery).
- `B = KingdomOfBadIdeas-{versionName}-{versionCode}`, read off the artifact.

| Purpose | Directory under root | App | Companion stem |
| --- | --- | --- | --- |
| Google Play | `.` | `{B}.aab` | `{B}` |
| Quick phone check | `Phone/Fast/` | `{B}-fast.apk` | `{B}-fast` |
| Release-like phone test | `Phone/Release/` | `{B}-release.apk` | `{B}-release` |
| Performance capture | `Phone/Perf/` | `{B}-perf.apk` | `{B}-perf` |
| APK from a Play bundle | `Phone/Play/` | `{B}-play-phone.apk` (split set: `.apks`) | `{B}-play-phone` |

- Companions: `{stem}-IL2CPP.symbols.zip` and, when minified, `{stem}-mapping.txt`. A Play set
  is all three files. `-PlayRelease` writes no symbols zip, and Gradle's
  `extractReleaseNativeSymbolTables` output can be days stale: build the zip from the Sentry
  job's `payload/unity-symbols/arm64-v8a/` (libil2cpp, libunity) plus `llvm-objcopy --strip-debug`
  of `Library/Bee/Android/Prj/IL2CPP/Gradle/launcher/build/intermediates/merged_native_libs/release/mergeReleaseNativeLibs/out/lib/arm64-v8a/`,
  entries `arm64-v8a/<lib>.so.sym`. Mapping: the AAB's
  `BUNDLE-METADATA/com.android.tools.build.obfuscation/proguard.map`, matched to the Sentry
  job's snapshot. Fast builds: mapping not applicable; record that.
- Evidence and receipt: `artifacts/builds/{UTC yyyyMMdd-HHmmss of build start}-{mode}-{shortCommit}/`
  in the operator checkout, receipt `{stem}.build.json` (harness metadata + purpose, version,
  signer, delivered hashes, debug-file association, cleanup). The last Play evidence folder's
  `prepare_play_delivery.py` (validate + stage) and `publish_play_delivery.ps1` (verified copy,
  publish, archive) are the template: copy and change the commit, build time, version floor and
  superseded receipt.
- Superseded set: moved by exact filename to `artifacts/builds/archive/{UTC}-{mode}-{version}-{code}/`
  with its receipt. One current set per purpose in the root.

## Version floor

`unity/ProjectSettings/ProjectSettings.asset`: `bundleVersion` and `AndroidBundleVersionCode`.
`StoreVersionBump` bumps them on every Play build, failed attempts included. Copy the two lines
(and only those; the shared seat's file also carries keystore/IL2CPP collateral) into the
operator checkout, commit on master by explicit path
(`Version floor X (N): spent by the Play AAB built from <commit>`), push. Never roll a spent
floor back.

## After the build

- Sentry: `-DeferSymbolUpload` leaves a job beside `unity/Builds/SymbolUploads/<id>/job.json` in
  the build checkout; report `status.json`'s state. Retry once from the job
  (`pwsh tools/ci/upload-android-symbols.ps1 -Job <job.json>`), never by rebuilding.
- Shared seat release: record then revert Unity's collateral (`git -C C:\claude-editor checkout -- unity`;
  `git status` shows ~15 tracked files, some line-ending only). Move generated untracked files
  (`unity/Assets/AddressableAssetsData/iOS.meta`,
  `unity/ProjectSettings/Packages/com.unity.dedicated-server/`) into the evidence folder.
  Confirm `productName: Bad Ideas`. Park detached at the new `origin/master`. Set the lock block
  to VACANT with a one-paragraph purpose line, previous block kept below as `PRIOR:`.
- Do not leave unversioned `Kingdom.apk` / `Kingdom-fast.apk` / `KingdomOfBadIdeas.aab` beside
  delivered sets in the operator checkout.
