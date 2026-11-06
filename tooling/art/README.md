# Background delivery variants

The 13 active backgrounds have two exact 16:9 exports:

| Tier | Size | Unity folder | Addressables group |
| --- | --- | --- | --- |
| Desktop / tablet | 2560 × 1440 | `Assets/Art/BackgroundVariants/DesktopTablet` | `Backgrounds_DesktopTablet` |
| Phone | 2048 × 1152 | `Assets/Art/BackgroundVariants/Mobile` | `Backgrounds_Mobile` |

## Export again

From the repository root, with Python and Pillow installed:

```powershell
python tooling/scripts/export_background_variants.py --dry-run
python tooling/scripts/export_background_variants.py
```

The script also works from other working directories; paths resolve from the repository root.
`background-variants.json` maps each runtime key to its approved crop. The regenerated source
images remain under the local, gitignored `art-pipeline/`; a fresh checkout already has the
delivery exports but needs those source files to regenerate them. Other sources remain under
`Assets/Art/Backgrounds`. Original pixels and metadata are preserved.

The exporter uses Lanczos resampling. It standardizes resolution and does not invent additional
painted detail. Both sizes are generated directly from the source crop, never from one another.
Sources must be exact 16:9, or have an explicitly approved `[left, top, right, bottom]` crop in the
manifest. Login and Update Gate have Sam-approved centered trims. Nothing is stretched. All
inputs are validated before writing. Repeating the export leaves identical files untouched and
preserves existing `.meta` GUIDs. New Unity metadata is created with stable GUIDs and tier-specific
import caps; `CardArtPostprocessor` enforces the same settings on import.

`--only <key>` exports a subset; `--output <project-relative-folder>` changes the destination.
The generated `export-manifest.json` records source hashes, crops, dimensions and output hashes.

## Runtime and bundles

One app contains both local packed groups. Each group packs together into its own bundle for the
active platform; only the chosen tier is loaded. Addresses are unique, for example:

```text
backgrounds/desktop-tablet/MainMenu_Backdrop
backgrounds/mobile/MainMenu_Backdrop
```

`BackgroundArt` chooses once per app session. Desktops use DesktopTablet; Android handhelds use
DesktopTablet when `Configuration.smallestScreenWidthDp >= 600`; iPads use DesktopTablet. Phones
and unidentified handhelds use Mobile. Hardware with a maximum texture width below 2560 uses
Mobile. Framebuffer resolution is not used to classify a phone as a tablet.

UI screens, illustrated loaders, Versus and the Royal Council hall all use the shared loader.
Handles remain cached for the app session. Direct stylesheet/prefab references to the old art
have been removed, and the old `Backgrounds` group is empty and excluded from builds. The old
images remain authoring sources and are not copied into the player through these routes.
Versus portrait coordinates were updated for the new 16:9 crop; the composition fits inside
4:3 tablet viewports so the painted portrait frames remain visible.

DesktopTablet imports use a 4096 cap on Standalone, Android and iOS so Unity preserves the actual
2560 × 1440 pixels on tablets. Mobile uses a 2048 cap. Both tiers disable power-of-two resizing
and mipmaps; compression is BC7 on Standalone and ASTC 6×6 on Android/iOS.

The existing `KingdomBuild` Addressables build step synchronizes and validates both groups before
building. For a manual content build, first use **Kingdom → Regenerate → Background Variant
Groups**, then the Addressables Groups window's content-build command for the active platform.
**Kingdom → Preview → Background Tier** selects the next Editor Play session's tier.

These are local bundles, so sign-in/update screens work before any network download. Both sets
contribute to app installation size. Remote hosting and downloading only the chosen set require
a separate catalog/hosting rollout; no CDN or remote URL has been configured by this change.
Bundles remain platform-specific: an Android bundle is not a Windows or iOS bundle.

## Verification

```powershell
python -m unittest discover -s tests/python -p test_export_background_variants.py
dotnet test tests/Kingdom.Core.Tests/Kingdom.Core.Tests.csproj --filter "FullyQualifiedName~LoadingArtworkAssetTests|FullyQualifiedName~BackgroundVariantPolicyTests"
```

The checks cover all 26 PNG dimensions, GUID/address pairings, platform caps, device selection,
source preservation and repeat exports. A Unity content build and device/Play Mode visual check
are separate from these file and source checks and require the Unity Editor seat.

References: [Unity Addressables overview](https://docs.unity3d.com/Packages/com.unity.addressables@2.7/manual/AddressableAssetsOverview.html),
[Android smallestScreenWidthDp](https://developer.android.com/reference/android/content/res/Configuration#smallestScreenWidthDp).
