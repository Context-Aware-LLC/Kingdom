"""Scale images down in place, overwriting each original.

Feed it files, folders, or both. Each image is resized (50% by default, Lanczos) and written
back over itself, so a Unity `.meta` beside it keeps its guid. Images are found by
extension: .png .jpg .jpeg .webp .bmp .tga .tif .tiff.

Usage:
    python scale_images.py <file-or-folder> [more ...] [--scale 0.5] [-r] [--dry-run]

    -r / --recursive   descend into subfolders (default: only the folder's own files)
    --dry-run          print what would change and write nothing

THERE IS NO UNDO HERE. The original is replaced; git is the way back. Running it twice
halves twice.
"""
import argparse
import os
import sys

from PIL import Image

EXTENSIONS = {".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tga", ".tif", ".tiff"}


def collect(paths, recursive):
    files = []
    for p in paths:
        if os.path.isfile(p):
            files.append(p)
        elif os.path.isdir(p):
            if recursive:
                for root, _, names in os.walk(p):
                    files.extend(os.path.join(root, n) for n in names)
            else:
                files.extend(os.path.join(p, n) for n in os.listdir(p))
        else:
            raise SystemExit(f"not found: {p}")
    return sorted(f for f in files if os.path.splitext(f)[1].lower() in EXTENSIONS and os.path.isfile(f))


def scale_file(path, scale, dry_run):
    """Returns (old size, new size, old bytes, new bytes)."""
    old_bytes = os.path.getsize(path)
    with Image.open(path) as src:
        fmt = src.format
        old = src.size
        new = (max(1, round(old[0] * scale)), max(1, round(old[1] * scale)))
        if dry_run:
            return old, new, old_bytes, None
        img = src
        # A palette cannot be resampled; go through RGBA and let the format re-encode.
        if img.mode == "P":
            img = img.convert("RGBA")
        # Pillow premultiplies RGBA/LA inside resize, so transparent edges do not fringe dark.
        out = img.resize(new, Image.LANCZOS)
        save_args = {}
        if "icc_profile" in src.info:
            save_args["icc_profile"] = src.info["icc_profile"]
        if fmt == "PNG":
            save_args["optimize"] = True
        elif fmt == "JPEG":
            save_args.update(quality=90, optimize=True)
            if out.mode in ("RGBA", "LA", "P"):
                out = out.convert("RGB")

    # Write beside the original and swap, so a failed encode never leaves a truncated file.
    tmp = path + ".scaling.tmp"
    try:
        out.save(tmp, format=fmt, **save_args)
        os.replace(tmp, path)
    finally:
        if os.path.exists(tmp):
            os.remove(tmp)
    return old, new, old_bytes, os.path.getsize(path)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+", help="image files and/or folders")
    ap.add_argument("--scale", type=float, default=0.5, help="factor to scale by (default 0.5)")
    ap.add_argument("-r", "--recursive", action="store_true", help="descend into subfolders")
    ap.add_argument("--dry-run", action="store_true", help="report only, write nothing")
    args = ap.parse_args()
    if not 0 < args.scale < 1:
        raise SystemExit("--scale must be between 0 and 1; this tool only scales down")

    files = collect(args.paths, args.recursive)
    if not files:
        raise SystemExit("no images found")

    before = after = failed = 0
    for f in files:
        try:
            old, new, old_bytes, new_bytes = scale_file(f, args.scale, args.dry_run)
        except Exception as e:  # one unreadable file should not stop a folder run
            failed += 1
            print(f"FAILED  {f}: {e}")
            continue
        before += old_bytes
        if new_bytes is None:
            print(f"{old[0]}x{old[1]} -> {new[0]}x{new[1]}  {old_bytes / 1048576:.2f} MB  {f}")
        else:
            after += new_bytes
            print(f"{old[0]}x{old[1]} -> {new[0]}x{new[1]}  {old_bytes / 1048576:.2f} -> {new_bytes / 1048576:.2f} MB  {f}")

    done = len(files) - failed
    if args.dry_run:
        print(f"dry run: {done} image(s), {before / 1048576:.2f} MB, nothing written")
    else:
        print(f"scaled {done} image(s): {before / 1048576:.2f} -> {after / 1048576:.2f} MB")
    if failed:
        print(f"{failed} failed")
        sys.exit(1)


if __name__ == "__main__":
    main()
