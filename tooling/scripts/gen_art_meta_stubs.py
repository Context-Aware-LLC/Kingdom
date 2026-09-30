"""Generate stub Unity .meta files for card art PNGs that don't have them.

Unity generates .meta files on import, but agents cannot open the Unity Editor.
This script generates compatible stubs using the same import settings as the
CardArtPostprocessor.ApplyIllustration path (Sprite, sRGB, mips ON, BC7@1024
desktop, ASTC_6x6@512 mobile).  Unity will accept these and will NOT
re-import unless the file hash changes.

The GUID is random and unique per file — this is the same mechanism Unity uses.
A committed stub GUID becomes the Addressables pointer for that asset.

Usage:
  python tooling/scripts/gen_art_meta_stubs.py
  python tooling/scripts/gen_art_meta_stubs.py --set REKINDLING
  python tooling/scripts/gen_art_meta_stubs.py --dry-run
"""

import argparse
import pathlib
import uuid
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
CARDS_ROOT = REPO / "unity" / "Assets" / "Art" / "Cards"

# textureFormat values (Unity internal enum):
#   25 = BC7   (Standalone / DXT)
#   50 = ASTC_6x6  (Android / iOS)

META_TEMPLATE = """\
fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 1
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 50
  spriteMode: 1
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 1
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationDetail: -1
  textureType: 8
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 0
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Standalone
    maxTextureSize: 1024
    resizeAlgorithm: 0
    textureFormat: 25
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: Android
    maxTextureSize: 512
    resizeAlgorithm: 0
    textureFormat: 50
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: WebGL
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  - serializedVersion: 4
    buildTarget: iOS
    maxTextureSize: 512
    resizeAlgorithm: 0
    textureFormat: 50
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 1
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    customData:
    physicsShape: []
    bones: []
    spriteID: 5e97eb03825dee720800000000000000
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    spriteCustomMetadata:
      entries: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def make_guid() -> str:
    return uuid.uuid4().hex  # 32-char hex, no dashes


def find_missing_meta(root: pathlib.Path, set_filter: str | None) -> list[pathlib.Path]:
    missing = []
    for png in sorted(root.rglob("*.png")):
        if set_filter and png.parent.name != set_filter:
            continue
        meta = pathlib.Path(str(png) + ".meta")
        if not meta.exists():
            missing.append(png)
    return missing


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--set", dest="set_id", metavar="SETID",
                    help="restrict to one set folder (e.g. REKINDLING)")
    ap.add_argument("--dry-run", action="store_true",
                    help="print what would be created without writing")
    args = ap.parse_args()

    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(errors="replace")
        except (AttributeError, ValueError):
            pass

    missing = find_missing_meta(CARDS_ROOT, args.set_id)
    if not missing:
        print("No PNGs without .meta found.")
        return 0

    print(f"{'DRY RUN: ' if args.dry_run else ''}Writing {len(missing)} .meta stub(s):")
    for png in missing:
        meta_path = pathlib.Path(str(png) + ".meta")
        g = make_guid()
        print(f"  {meta_path.relative_to(REPO)}  [{g}]")
        if not args.dry_run:
            meta_path.write_text(META_TEMPLATE.format(guid=g), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
