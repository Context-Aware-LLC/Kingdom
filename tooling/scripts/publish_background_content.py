#!/usr/bin/env python3
"""Publish only the exact hashed bundles in a Unity build's background-content.json.

Default: validate local output and print a plan. --publish uploads missing objects to R2,
then downloads and SHA-256 verifies every public URL before writing the build receipt.
--verify performs the same verification without uploading. Existing objects are immutable.
"""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import urllib.error
import urllib.request
from pathlib import Path

PLATFORMS = {"Android", "iOS", "StandaloneWindows64", "StandaloneOSX", "StandaloneLinux64"}
FILE = re.compile(r"backgrounds_desktoptablet_[a-z0-9_.-]+_[a-f0-9]{32}\.bundle")
ORIGIN = "https://kingdomofbadideas.com/game-assets/"
REPO = Path(__file__).resolve().parents[2]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load_manifest(path):
    manifest = json.loads(path.read_text(encoding="utf-8"))
    platform = manifest.get("platform")
    prefix = f"backgrounds/v1/{platform}"
    if (manifest.get("version") != 1 or platform not in PLATFORMS
            or manifest.get("bucket") != "kobi-game-assets"
            or manifest.get("keyPrefix") != prefix or manifest.get("baseUrl") != ORIGIN + prefix):
        raise ValueError("Manifest destination does not match the background delivery configuration")
    bundles = manifest.get("bundles", [])
    if len(bundles) != 13 or len({b["file"] for b in bundles}) != 13:
        raise ValueError("Expected exactly 13 distinct desktop/tablet background bundles")
    for bundle in bundles:
        if not FILE.fullmatch(bundle["file"]):
            raise ValueError(f"Invalid bundle filename: {bundle['file']}")
        source = path.parent / bundle["file"]
        if source.stat().st_size != bundle["bytes"] or digest(source) != bundle["sha256"]:
            raise ValueError(f"Bundle changed since the content build: {source}")
    return manifest


def remote_matches(url, bundle):
    # Cloudflare rejects urllib's generic signature; identify the publishing client.
    request = urllib.request.Request(url, headers={
        "User-Agent": "KingdomOfBadIdeas-ContentPublisher/1.0 (+https://kingdomofbadideas.com)",
    })
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            sha = hashlib.sha256()
            size = 0
            while chunk := response.read(1024 * 1024):
                size += len(chunk)
                sha.update(chunk)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        raise
    if size != bundle["bytes"] or sha.hexdigest() != bundle["sha256"]:
        raise ValueError(f"Public content mismatch; refusing to overwrite immutable URL: {url}")
    return True


def upload(manifest, bundle, path):
    npx = shutil.which("npx.cmd") or shutil.which("npx")
    if not npx:
        raise RuntimeError("npx is required to run Wrangler")
    # No token in arguments or logs: Wrangler reads CLOUDFLARE_API_TOKEN from the environment.
    subprocess.run([
        npx, "--yes", "wrangler@4", "r2", "object", "put",
        f"{manifest['bucket']}/{manifest['keyPrefix']}/{bundle['file']}",
        "--remote", "--file", str(path.parent / bundle["file"]),
        "--content-type", "application/octet-stream",
        "--cache-control", "public, max-age=31536000, immutable",
    ], cwd=REPO / "web/landing", check=True)


def publish(path, allow_upload=False):
    manifest = load_manifest(path)  # Validate ALL files before any network mutation.
    manifest_sha = digest(path)
    for bundle in manifest["bundles"]:
        url = f"{manifest['baseUrl']}/{bundle['file']}"
        if not remote_matches(url, bundle):
            if not allow_upload:
                raise ValueError(f"Not published: {url}")
            upload(manifest, bundle, path)
            if not remote_matches(url, bundle):
                raise ValueError(f"Upload is not publicly available: {url}")
        print(f"Verified {bundle['file']} ({bundle['bytes']:,} bytes)")
    if digest(path) != manifest_sha:
        raise ValueError("Manifest changed during publication; rebuild and retry")
    receipt = Path(str(path) + ".published.json")
    temporary = Path(str(receipt) + ".tmp")
    temporary.write_text(json.dumps({"manifestSha256": manifest_sha}, indent=2) + "\n", encoding="utf-8")
    temporary.replace(receipt)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--publish", action="store_true")
    mode.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    path = args.manifest.resolve()
    manifest = load_manifest(path)
    print(f"{manifest['platform']}: {len(manifest['bundles'])} remote bundles, "
          f"{sum(b['bytes'] for b in manifest['bundles']):,} bytes -> {manifest['baseUrl']}")
    if args.publish or args.verify:
        print(f"Publication receipt: {publish(path, allow_upload=args.publish)}")
    else:
        print("Local validation passed. Use --publish to upload and verify, or --verify to verify existing content.")


if __name__ == "__main__":
    main()
