---
name: build
description: Produce a Kingdom of Bad Ideas Android game build, asking its purpose and build type when unspecified and delivering consistently named artifacts. Use for /build, a phone test APK, performance build, or Google Play bundle in tcg-hearthlike. Not for ordinary compilation, tests, server deployment, or creating a build skill.
---

# Kingdom build

This is the project build workflow for Sam. Read
[Android build modes and delivery](../../../docs/dev-docs/operations/android-builds.md)
before choosing flags or publishing files. Resolve repository paths from the repo root.

## Choose the purpose first

A bare `/build`, `$build`, or an ambiguous request to make a build must ask and **wait for
the answer before launching Unity, changing versions, or replacing artifacts**. Use the
available question tool; otherwise ask in the reply:

> What is this build for: a quick phone check, a release-like phone test, performance
> capture, or a Google Play bundle?

Offer these choices, recommending fast phone testing for ordinary visual/gameplay iteration:

| Purpose | Harness arguments | Result |
| --- | --- | --- |
| Quick phone check | `-Fast` | Fast debug-signed APK; unsuitable for performance measurements |
| Release-like phone test | `-DeferSymbolUpload` | Optimized debug-signed APK |
| Performance capture | `-PerfCapture -DeferSymbolUpload` | Optimized, instrumented APK from a checkout with no uncommitted changes (cache handling still follows step 2 unless requested `-Clean`) |
| Google Play release | `-PlayRelease -DeferSymbolUpload` | Upload-key-signed AAB and matching debugging files |

An explicit purpose in the request is already the answer: `/build fast`, `/build perf`,
`/build play`, and `/build release-like phone` need no repeated confirmation. Bare
`/build release` is ambiguous between an optimized APK and a Play bundle; ask which.
If Sam requests another platform or output, establish that target before proceeding;
do not silently substitute Android.

For every purpose except Google Play release, ask whether to also install on the connected phone;
for Google Play release, ask only if the request mentions a phone install. Honor any install
instruction already supplied. Without one, build and deliver the files; do not assume a
store upload, track rollout, or phone-data reset is included. A request for both a Play bundle
and a phone install means both deliverables, not two differently labelled copies of an APK.

## Build and deliver

1. Identify the source checkout/commit and Sam's intended changes. Preserve unrelated work.
   Follow `AGENTS.md` and the shared Editor protocol in `CLAUDE.md`; claim the shared seat
   before touching its checkout. When using it, ensure it actually contains the requested
   source changes. A skill invocation does not authorize using Sam's open Editor.
2. State the selected mode, install intent, and exact final delivery directory. Use
   `pwsh tools/ci/build-android.ps1` with the selected arguments from the build checkout.
   Preserve caches by default. Use `-Clean` only when requested or evidence calls for cache
   recovery; do not add another build or a broad test suite to an artifact-only request.
3. Require the harness to succeed and its artifact hash to match the adjacent `.build.json`.
   Inspect the built APK/AAB for `com.contextaware.kobi`, its actual version name/code, and
   signing identity. Read APK metadata with the installed Android SDK tools and AAB metadata
   with the available bundletool. Do not name a Play build from pre-build settings: store
   builds bump numbers, including failed attempts. If the harness exits non-zero or the hash
   does not match, do not stage or publish any artifacts. Report the failing step, the log path,
   and the expected and actual hashes, then stop and ask Sam whether to retry or use -Clean.
   Preserve the spent version floor in the operator checkout before shared-seat cleanup;
   do not silently roll it back.
4. Apply the delivery contract in the linked guide exactly. Stage the complete current-build
   set, verify copies by hash, and only then publish it to the operator's delivery directory.
   A build in `C:\claude-editor` is intermediate output, not delivery to Sam. Preserve each
   build's native symbols and, when minified, its R8 mapping; never pair a fresh app with
   debugging files left by a previous build.
5. If an install was requested, start it from the verified artifact. 
   a. If no install was requested, skip to step 6.
   b. If the artifact is an AAB, generate an APK set with bundletool or use a separately
      identified phone APK.
   c. Compare the installed app's signing certificate with the artifact's.
   d. On a match, install and poll to completion.
   e. On a mismatch, stop and ask Sam before any uninstall or data clear.
   Report an actual device/install blocker separately from successful artifact delivery.
6. Keep any deferred symbol job intact and report its actual state. Retry a transient upload
   failure once from the saved job rather than rebuilding; report authentication or repeated
   failures with the job path. Follow the delivery guide's retention rules. If the shared Editor
   seat is already claimed by another session, do not take it over; wait for the lock to clear
   or report the blocker and ask Sam how to proceed. Release the shared Editor lock and revert
   the temporary files, config edits, and other changes listed in the Editor protocol section
   of `CLAUDE.md`, listing each reverted item in the report.

Report the build type, version/code, exact app and debugging-file paths, build duration,
and install/upload status. Distinguish built, delivered, installed, and runtime-verified;
only claim the states supported by evidence.
