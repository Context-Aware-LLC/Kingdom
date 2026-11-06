"""Export approved 16:9 crops at desktop/tablet and phone delivery sizes.

Run from any directory:
    python tooling/scripts/export_background_variants.py --dry-run
    python tooling/scripts/export_background_variants.py

Requires Pillow. Sources and existing Unity metadata are never overwritten.
Cropping is only permitted when a source has an explicit crop rectangle in the
manifest. Lanczos resampling standardizes delivery size; it adds no new detail.
"""

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import tempfile
import uuid

from PIL import Image, ImageOps


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MANIFEST = Path("tooling/art/background-variants.json")
DEFAULT_OUTPUT = Path("unity/Assets/Art/BackgroundVariants")
TIERS = {"DesktopTablet": (2560, 1440), "Mobile": (2048, 1152)}


def resolve_inside(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError(f"Path must remain inside the project: {relative}")
    return path


def prepare(root, manifest, only=()):
    """Validate the entire selection before writing any output."""
    entries = manifest["backgrounds"]
    known = set()
    prepared = []
    for entry in entries:
        key = entry["key"]
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", key) or key.casefold() in known:
            raise ValueError(f"Invalid or duplicate background key: {key}")
        known.add(key.casefold())
        if only and key not in only:
            continue
        path = resolve_inside(root, entry["source"])
        source_bytes = path.read_bytes()
        with Image.open(io.BytesIO(source_bytes)) as raw:
            image = ImageOps.exif_transpose(raw)
            image.load()
            source_size = image.size
            expected_size = entry.get("expected_source_size")
            if expected_size and list(source_size) != expected_size:
                raise ValueError(f"{key}: source changed from {expected_size} to {source_size}; review its explicit crop")
            crop = entry.get("crop", [0, 0, *source_size])
            if (len(crop) != 4 or any(type(x) is not int for x in crop)
                    or not 0 <= crop[0] < crop[2] <= image.width
                    or not 0 <= crop[1] < crop[3] <= image.height):
                raise ValueError(f"{key}: invalid crop rectangle {crop} for {source_size}")
            width, height = crop[2] - crop[0], crop[3] - crop[1]
            if width * 9 != height * 16:
                unit = min(width // 16, height // 9)
                raise ValueError(
                    f"{key}: crop is {width}x{height}, not exact 16:9. "
                    f"Export an approved {unit * 16}x{unit * 9} crop or add an explicit "
                    "[left, top, right, bottom] crop to the manifest; no automatic stretching."
                )
            profile = image.info.get("icc_profile")
            image = image.crop(crop).convert("RGBA" if "A" in image.getbands()
                                             or "transparency" in image.info else "RGB")
        prepared.append({
            "key": key, "source": entry["source"], "source_size": list(source_size),
            "source_sha256": hashlib.sha256(source_bytes).hexdigest(),
            "crop": crop, "image": image, "icc_profile": profile,
        })
    unknown = set(only) - {entry["key"] for entry in entries}
    if unknown:
        raise ValueError(f"Unknown background keys: {', '.join(sorted(unknown))}")
    if not prepared:
        raise ValueError("No backgrounds selected")
    return prepared


def write_if_changed(path, data):
    if path.exists() and path.read_bytes() == data:
        return False
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)
    return True


def create_unity_metadata(root, path, size=None):
    """Create metadata only for new Unity assets; reruns retain their GUIDs/settings."""
    if not path.is_relative_to(root / "unity/Assets") or Path(str(path) + ".meta").exists():
        return
    identity = path.relative_to(root / "unity").as_posix()
    guid = uuid.uuid5(uuid.NAMESPACE_URL, "https://kingdomofbadideas.com/assets/" + identity).hex
    header = f"fileFormatVersion: 2\nguid: {guid}\n"
    if path.is_dir():
        body = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
    elif size is None:
        body = "TextScriptImporter:\n  externalObjects: {}\n"
    else:
        cap = 4096 if size[0] > 2048 else 2048
        body = f"""TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    enableMipMap: 0
    sRGBTexture: 1
  isReadable: 0
  maxTextureSize: {cap}
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  compressionQuality: 50
  spriteMode: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  alphaUsage: 1
  alphaIsTransparency: 1
  textureType: 8
  textureShape: 1
  platformSettings:
"""
        for platform, texture_format in (("Standalone", 25), ("Android", 50), ("iOS", 50)):
            body += f"""  - serializedVersion: 4
    buildTarget: {platform}
    maxTextureSize: {cap}
    resizeAlgorithm: 0
    textureFormat: {texture_format}
    textureCompression: 1
    compressionQuality: 50
    crunchedCompression: 0
    overridden: 1
"""
        body += "  spriteSheet:\n    serializedVersion: 2\n    sprites: []\n    secondaryTextures: []\n    nameFileIdTable: {}\n"
    write_if_changed(Path(str(path) + ".meta"), (header + body).encode("utf-8"))


def export(root, manifest, output, *, dry_run=False, only=()):
    prepared = prepare(root, manifest, only)
    sources = {resolve_inside(root, entry["source"]) for entry in manifest["backgrounds"]}
    destinations = [output / tier / (item["key"] + ".png")
                    for item in prepared for tier in TIERS]
    if any(path.resolve() in sources for path in destinations):
        raise ValueError("An output would overwrite a source image")

    report = {"resampler": "Pillow Lanczos", "tiers": TIERS, "backgrounds": []}
    for item in prepared:
        image = item["image"]
        record = {k: v for k, v in item.items() if k not in ("image", "icc_profile")}
        record["outputs"] = []
        for tier, size in TIERS.items():
            path = output / tier / (item["key"] + ".png")
            result = {"tier": tier, "size": size, "path": path.relative_to(root).as_posix()}
            if not dry_run:
                resized = image.resize(size, Image.Resampling.LANCZOS)
                encoded = io.BytesIO()
                options = {"icc_profile": item["icc_profile"]} if item["icc_profile"] else {}
                resized.save(encoded, format="PNG", **options)
                data = encoded.getvalue()
                result["sha256"] = hashlib.sha256(data).hexdigest()
                result["bytes"] = len(data)
                write_if_changed(path, data)
                create_unity_metadata(root, path, size)
                create_unity_metadata(root, path.parent)
            record["outputs"].append(result)
        report["backgrounds"].append(record)
        print(f"{item['key']}: {image.width}x{image.height} -> 2560x1440 + 2048x1152")
    if not dry_run:
        # A subset report must not overwrite the manifest for a complete export.
        report_name = "export-selected.json" if only else "export-manifest.json"
        write_if_changed(output / report_name, (json.dumps(report, indent=2) + "\n").encode("utf-8"))
        create_unity_metadata(root, output / report_name)
        create_unity_metadata(root, output)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST, help="Project-relative source manifest")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="Project-relative output folder")
    parser.add_argument("--only", action="append", default=[], help="Export only this key (repeatable)")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        manifest = json.loads(resolve_inside(REPO_ROOT, args.manifest).read_text(encoding="utf-8"))
        report = export(REPO_ROOT, manifest, resolve_inside(REPO_ROOT, args.output),
                        dry_run=args.dry_run, only=args.only)
    except (ValueError, OSError, KeyError) as error:
        parser.exit(1, f"Background export failed: {error}\n")
    count = len(report["backgrounds"]) * len(TIERS)
    print(f"{'Dry run: would export' if args.dry_run else 'Exported'} {count} variants; sources and .meta files preserved.")


if __name__ == "__main__":
    main()
