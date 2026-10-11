"""Derive a servable thumbnail set from the source card art.

WHY THIS EXISTS. The source art is not servable to a browser: measured 2026-08-27, 293 PNGs
totalling 518 MB, individual files 1.2-2.4 MB at 1024x1024 or 1254x1254. A card grid that
loaded those would move half a gigabyte to render one screen.

This writes a parallel tree of small WebP thumbnails and NEVER touches the source. It is a
cache: deleting the output costs a re-run and nothing else.

    python tooling/cardweb/build_thumbs.py            # incremental
    python tooling/cardweb/build_thumbs.py --force    # rebuild everything
    python tooling/cardweb/build_thumbs.py --check    # report staleness, write nothing

DO NOT point this at the source tree as an output. Overwriting a card .png in place is the
supported way to replace art (the .meta beside it carries the guid an Addressables entry points
at), and a downscale written over the original would be an unrecoverable quality loss that
looks exactly like a successful run.
"""

from __future__ import annotations

import argparse
import pathlib
import sys

from PIL import Image

REPO = pathlib.Path(__file__).resolve().parents[2]
ART_DIR = REPO / "unity" / "Assets" / "Art" / "Cards"
THUMB_DIR = pathlib.Path(__file__).resolve().parent / ".thumbs"

EDGE = 256          # longest edge, px
QUALITY = 82        # WebP quality
SOURCE_EXTS = (".png", ".jpg", ".jpeg")


def sources() -> list[pathlib.Path]:
    out: list[pathlib.Path] = []
    for ext in SOURCE_EXTS:
        out.extend(ART_DIR.rglob(f"*{ext}"))
    return sorted(out)


def thumb_for(src: pathlib.Path) -> pathlib.Path:
    """Mirror the source's <setId>/<stem> layout, so the set folder survives into the cache."""
    rel = src.relative_to(ART_DIR)
    return THUMB_DIR / rel.parent / (rel.stem + ".webp")


def is_stale(src: pathlib.Path, dst: pathlib.Path) -> bool:
    return not dst.exists() or dst.stat().st_mtime < src.stat().st_mtime


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--force", action="store_true", help="rebuild every thumbnail")
    mode.add_argument("--check", action="store_true", help="report staleness, write nothing")
    args = ap.parse_args()

    if not ART_DIR.exists():
        print(f"art directory not found: {ART_DIR}", file=sys.stderr)
        return 2

    srcs = sources()
    if not srcs:
        print(f"no source art under {ART_DIR} -- nothing to do.", file=sys.stderr)
        return 2

    todo = [s for s in srcs if args.force or is_stale(s, thumb_for(s))]

    if args.check:
        print(f"sources: {len(srcs)}    stale: {len(todo)}")
        for s in todo[:20]:
            print(f"  stale  {s.relative_to(ART_DIR)}")
        if len(todo) > 20:
            print(f"  ... and {len(todo) - 20} more")
        return 1 if todo else 0

    written = 0
    src_bytes = 0
    dst_bytes = 0
    for src in todo:
        dst = thumb_for(src)
        dst.parent.mkdir(parents=True, exist_ok=True)
        with Image.open(src) as im:
            im = im.convert("RGBA") if im.mode in ("RGBA", "LA", "P") else im.convert("RGB")
            im.thumbnail((EDGE, EDGE), Image.LANCZOS)
            im.save(dst, "WEBP", quality=QUALITY, method=6)
        written += 1
        src_bytes += src.stat().st_size
        dst_bytes += dst.stat().st_size

    total = sum(thumb_for(s).stat().st_size for s in srcs if thumb_for(s).exists())
    print(f"sources        {len(srcs)}")
    print(f"written        {written}")
    if written:
        print(f"in             {src_bytes / 1048576:.1f} MB")
        print(f"out            {dst_bytes / 1048576:.1f} MB")
    print(f"cache total    {total / 1048576:.1f} MB  ({THUMB_DIR})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
