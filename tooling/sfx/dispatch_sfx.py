#!/usr/bin/env python3
"""Fire a backlog of card SFX jobs at ElevenLabs and collect the clips.

Calls ElevenLabs DIRECTLY, not through ComfyUI. ComfyUI's
ElevenLabsTextToSoundEffects is an `api_node`: it bills Comfy credits, which
resell the same generation you are already paying ElevenLabs for. Routing
through it means paying twice for one clip. A paid ElevenLabs plan (Starter and
up) includes API access and spends the SAME monthly credit pool as the web app,
so an API call here costs exactly what clicking in the browser costs -- with the
backlog running unattended.

There is no state file: the output directory IS the record of what has been
generated, because every clip is named after its job key. "Which cards still
lack SFX" is a directory listing.

Usage:
    python tooling/sfx/dispatch_sfx.py --jobs tooling/sfx/jobs.json
    python tooling/sfx/dispatch_sfx.py --jobs ... --takes 3 --influence-spread 0.2
    python tooling/sfx/dispatch_sfx.py --jobs ... --only 1069_the_great_mechanism__attack
    python tooling/sfx/dispatch_sfx.py --jobs ... --dry-run
    python tooling/sfx/dispatch_sfx.py --list

Job file is a JSON list of objects:
    {"key": "1069_the_great_mechanism__attack", "prompt": "...",
     "duration": 2.0, "loop": false, "prompt_influence": 0.4}
Only "key" and "prompt" are required.

A job whose key already has a clip on disk is SKIPPED unless --force.
"""

import argparse
import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

API_URL = "https://api.elevenlabs.io/v1/sound-generation"

# The only model the sound-generation endpoint accepts. NOTE: ComfyUI's node calls
# the same model "eleven_sfx_v2" -- that name is Comfy's, and the direct API rejects it.
MODEL_ID = "eleven_text_to_sound_v2"

DEFAULT_OUT = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "sfx"

# ElevenLabs bills 40 credits per second when duration_seconds is specified. We always
# specify it, so the estimate is exact rather than indicative -- see --dry-run.
CREDITS_PER_SECOND = 40


def resolve_api_key(explicit):
    """ElevenLabs API key.

    Draws on the subscription's own credit pool, so this is the cheap path.
    Never put the key in jobs.json -- that file is committed.
    """
    if explicit:
        return explicit
    for var in ("ELEVENLABS_API_KEY", "ELEVEN_API_KEY", "XI_API_KEY"):
        env = os.environ.get(var)
        if env:
            return env.strip()
    keyfile = pathlib.Path(__file__).resolve().parent / ".elevenlabs_key"
    if keyfile.exists():
        return keyfile.read_text(encoding="utf-8").strip()
    return None


def generate(api_key, prompt, duration, loop, prompt_influence, output_format):
    """One clip. Returns raw audio bytes -- the endpoint responds with the file itself."""
    body = json.dumps(
        {
            "text": prompt,
            "model_id": MODEL_ID,
            "duration_seconds": float(duration),
            "loop": bool(loop),
            "prompt_influence": float(prompt_influence),
            "output_format": output_format,
        }
    ).encode("utf-8")
    req = urllib.request.Request(
        API_URL,
        data=body,
        headers={
            "xi-api-key": api_key,
            "Content-Type": "application/json",
            "Accept": "audio/mpeg",
        },
    )
    with urllib.request.urlopen(req, timeout=180) as resp:
        return resp.read()


def subscription_line(api_key):
    """One line of remaining quota, so the run is bounded by something real."""
    req = urllib.request.Request(
        "https://api.elevenlabs.io/v1/user/subscription",
        headers={"xi-api-key": api_key},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            d = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        # ElevenLabs keys are scoped per operation. A key that generates fine can still
        # be missing `user_read`, and the raw 401 reads like a dead key -- say which it is.
        body = e.read().decode("utf-8", "replace")
        if "user_read" in body:
            return "subscription: not readable (key lacks the user_read scope; generation is unaffected)"
        return "subscription: unavailable (HTTP %s)" % e.code
    except Exception as e:  # noqa: BLE001 - informational only, never fatal
        return "subscription: unavailable (%s)" % e
    used = d.get("character_count")
    limit = d.get("character_limit")
    tier = d.get("tier", "?")
    if used is None or limit is None:
        return "subscription: tier %s" % tier
    left = limit - used
    return "subscription: tier %s, %s of %s credits left (~%.0fs of SFX)" % (
        tier,
        format(left, ","),
        format(limit, ","),
        left / CREDITS_PER_SECOND,
    )


def existing_keys(out_dir):
    """Keys that already have at least one clip. Takes are '<key>__takeN.mp3'."""
    keys = set()
    for p in out_dir.glob("*.mp3"):
        keys.add(p.stem.split("__take")[0])
    return keys


def http_error_text(e):
    body = e.read().decode("utf-8", "replace")
    try:
        d = json.loads(body)
        detail = d.get("detail", d)
        if isinstance(detail, dict):
            return "%s: %s" % (
                detail.get("status", e.code),
                detail.get("message", json.dumps(detail)),
            )
        return "%s: %s" % (e.code, detail)
    except ValueError:
        return "%s: %s" % (e.code, body[:300])


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--jobs", type=pathlib.Path, help="JSON list of jobs")
    ap.add_argument("--out", type=pathlib.Path, default=DEFAULT_OUT, help="clip output directory")
    ap.add_argument(
        "--api-key",
        help="ElevenLabs key; defaults to $ELEVENLABS_API_KEY, then tooling/sfx/.elevenlabs_key",
    )
    ap.add_argument(
        "--output-format",
        default="mp3_44100_128",
        help="mp3_44100_128 works on every paid tier; 192 needs Creator or above",
    )
    # RULING (Sam, 2026-08-28): generate a RANGE from the gate, do not economise into a corner.
    # "This shit is so cheap, I think I'd actually rather just have multiple things to choose
    # from right from the gate so I can get a better sense of the range of possibilities."
    # A sound effect is a search, not a retake: the first result cannot tell you what the space
    # of good answers looks like, and a second dispatch costs another round trip of Sam's
    # attention -- which is the expensive input here, not credits. 4 takes with the influence
    # spread on gives four genuinely different readings of one prompt.
    ap.add_argument("--takes", type=int, default=4, help="clips per job (for auditioning)")
    ap.add_argument(
        "--influence-spread",
        type=float,
        default=0.15,
        help="with --takes>1, vary prompt_influence by +/- this much across takes. Nonzero by "
             "default: identical settings give near-identical takes, which is a wasted audition",
    )
    ap.add_argument("--only", action="append", help="run just this key (repeatable)")
    ap.add_argument("--force", action="store_true", help="regenerate keys that already have clips")
    ap.add_argument("--dry-run", action="store_true", help="print the plan and the cost, send nothing")
    ap.add_argument("--list", action="store_true", help="list keys that already have clips, then exit")
    args = ap.parse_args()

    out_dir = args.out
    out_dir.mkdir(parents=True, exist_ok=True)

    if args.list:
        for k in sorted(existing_keys(out_dir)):
            takes = sorted(out_dir.glob(k + "*.mp3"))
            print("%-40s %d take(s)" % (k, len(takes)))
        return 0

    if not args.jobs:
        ap.error("--jobs is required unless --list is given")

    jobs = json.loads(args.jobs.read_text(encoding="utf-8"))
    if args.only:
        wanted = set(args.only)
        jobs = [j for j in jobs if j["key"] in wanted]

    have = set() if args.force else existing_keys(out_dir)
    todo = [j for j in jobs if j["key"] not in have]
    skipped = len(jobs) - len(todo)

    total_seconds = sum(float(j.get("duration", 5.0)) for j in todo) * args.takes
    print(
        "%d job(s), %d skipped (already have clips), %d take(s) each"
        % (len(todo), skipped, args.takes)
    )
    print(
        "~%.1fs of audio, ~%s credits"
        % (total_seconds, format(int(total_seconds * CREDITS_PER_SECOND), ","))
    )

    api_key = resolve_api_key(args.api_key)
    if not api_key:
        print(
            "\nNo ElevenLabs API key found.\n"
            "Set $ELEVENLABS_API_KEY, write tooling/sfx/.elevenlabs_key, or pass --api-key.\n"
            "Get one at https://elevenlabs.io/app/settings/api-keys",
            file=sys.stderr,
        )
        return 2

    print(subscription_line(api_key))

    if args.dry_run:
        for j in todo:
            print(
                "  %-40s %.1fs  %s"
                % (j["key"], float(j.get("duration", 5.0)), j["prompt"][:60])
            )
        return 0

    failures = []
    for j in todo:
        key = j["key"]
        duration = float(j.get("duration", 5.0))
        loop = bool(j.get("loop", False))
        base_influence = float(j.get("prompt_influence", 0.3))

        for take in range(1, args.takes + 1):
            influence = base_influence
            if args.takes > 1 and args.influence_spread:
                # spread takes evenly across [base-spread, base+spread]
                t = (take - 1) / (args.takes - 1)
                influence = base_influence + args.influence_spread * (2 * t - 1)
                influence = max(0.0, min(1.0, influence))

            label = key if args.takes == 1 else "%s__take%d" % (key, take)
            try:
                audio = generate(
                    api_key, j["prompt"], duration, loop, influence, args.output_format
                )
            except urllib.error.HTTPError as e:
                print("  %-40s FAILED  %s" % (label, http_error_text(e)))
                failures.append(label)
                continue
            except urllib.error.URLError as e:
                print("  %-40s FAILED  %s" % (label, e.reason))
                failures.append(label)
                continue

            dest = out_dir / (label + ".mp3")
            dest.write_bytes(audio)
            print(
                "  %-40s -> %s  (%.1fs, influence %.2f, %s KB)"
                % (label, dest.name, duration, influence, format(len(audio) // 1024, ","))
            )

    if failures:
        print("\n%d failed: %s" % (len(failures), ", ".join(failures)))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
