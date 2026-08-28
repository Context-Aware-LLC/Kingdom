#!/usr/bin/env python3
"""Generate hero voice lines from voice-lines.json straight into the Unity project.

Reads the cast and the script from tooling/sfx/voice-lines.json and writes
    unity/Emberfall/Assets/Sound/Voice/<Class>/vo_<class>_<moment>_<n>.wav

Uses model_id eleven_v3, because that is the only model that honours the inline
audio tags ([whispers], [shouts], emotion tags) -- older models read the brackets
aloud as literal text. MEASURED 2026-08-28: every voice on the account accepts v3,
even though the voice list's high_quality_base_model_ids never enumerates it.

There is no state file: a line whose wav already exists is SKIPPED, so the output
tree is the record and a re-run is free. --force regenerates.

    python tooling/sfx/dispatch_vo.py --dry-run
    python tooling/sfx/dispatch_vo.py --only GRAVEKEEP
    python tooling/sfx/dispatch_vo.py --only GRAVEKEEP --moment win
    python tooling/sfx/dispatch_vo.py                      # everything outstanding

AFTERWARDS, two steps this script cannot do:
  1. Run Emberfall/Addressables/Setup Groups + Migrate Card Data in the Editor.
     Files under Assets/Sound/Voice have NO address until that runs, and an
     unaddressed clip fails silently -- no exception, no console error.
  2. Commit the .meta Unity generates beside each clip. Without it the guid is
     re-minted on another machine and the Addressables entry orphans.

Filenames must be unique across the WHOLE Voice tree, not per class: the group
addresses by file name, so Grave/win_1 and Ember/win_1 would collide. The
vo_<class>_<moment>_<n> scheme keeps them unique by construction.
"""

import argparse
import json
import pathlib
import struct
import sys
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINES = pathlib.Path(__file__).resolve().parent / "voice-lines.json"
VOICE_ROOT = ROOT / "unity" / "Emberfall" / "Assets" / "Sound" / "Voice"
MODEL_ID = "eleven_v3"
API = "https://api.elevenlabs.io/v1/text-to-speech/"

# WAV at 24kHz, to match the .wav already in Assets/Sound and because Unity re-encodes on
# import -- handing it an mp3 makes the shipped clip lossy twice. pcm_44100 is refused below
# the Pro tier ("only available on the Pro tier and above"), and 24kHz is ample for speech:
# 12kHz Nyquist sits well above anything in a human voice.
#
# output_format is a QUERY PARAMETER. Passing it in the JSON body is accepted and silently
# ignored, so every clip comes back in the default format and the sizes look plausible --
# there is no error to notice.
OUTPUT_FORMAT = "pcm_24000"
PCM_RATE = 24000
PCM_CHANNELS = 1
PCM_BYTES_PER_SAMPLE = 2   # signed 16-bit little-endian


def wav_header(pcm_len):
    """The API returns HEADERLESS pcm. Unity will not import it without a RIFF header."""
    byte_rate = PCM_RATE * PCM_CHANNELS * PCM_BYTES_PER_SAMPLE
    return (
        b"RIFF" + struct.pack("<I", 36 + pcm_len) + b"WAVEfmt "
        + struct.pack("<IHHIIHH", 16, 1, PCM_CHANNELS, PCM_RATE, byte_rate,
                      PCM_CHANNELS * PCM_BYTES_PER_SAMPLE, PCM_BYTES_PER_SAMPLE * 8)
        + b"data" + struct.pack("<I", pcm_len)
    )

# TTS bills per character of input text, unlike sound effects which bill per second.
CREDITS_PER_CHAR = 1


def resolve_api_key(explicit):
    if explicit:
        return explicit
    import os

    for var in ("ELEVENLABS_API_KEY", "ELEVEN_API_KEY", "XI_API_KEY"):
        if os.environ.get(var):
            return os.environ[var].strip()
    keyfile = pathlib.Path(__file__).resolve().parent / ".elevenlabs_key"
    if keyfile.exists():
        return keyfile.read_text(encoding="utf-8").strip()
    return None


def class_folder(cls):
    """Voice/Grave/, not Voice/GRAVEKEEP/ -- matches the folder Sam already created."""
    return {
        "EMBERWARD": "Ember",
        "TIDECALLER": "Tide",
        "GRAVEKEEP": "Grave",
        "VERDANT": "Verdant",
        "CLOCKWRIGHT": "Clock",
        "NIGHTVEIL": "Night",
        "STONEWARDEN": "Warden",
        "TEMPEST": "Storm",
    }[cls]


def speak(api_key, voice_id, text):
    body = json.dumps({"text": text, "model_id": MODEL_ID}).encode("utf-8")
    req = urllib.request.Request(
        API + voice_id + "?output_format=" + OUTPUT_FORMAT,
        data=body,
        headers={
            "xi-api-key": api_key,
            "Content-Type": "application/json",
            "Accept": "audio/mpeg",
        },
    )
    with urllib.request.urlopen(req, timeout=180) as resp:
        pcm = resp.read()
    return wav_header(len(pcm)) + pcm


def http_error_text(e):
    body = e.read().decode("utf-8", "replace")
    try:
        detail = json.loads(body).get("detail", body)
        if isinstance(detail, dict):
            return "%s: %s" % (detail.get("status", e.code), detail.get("message", detail))
        return "%s: %s" % (e.code, detail)
    except ValueError:
        return "%s: %s" % (e.code, body[:200])


def plan(data, only_class, only_moment):
    """Every (class, moment, index, stem, text, voice_id) the run would produce."""
    for cls, entry in data["voices"].items():
        if only_class and cls != only_class:
            continue
        for moment, lines in entry["lines"].items():
            if only_moment and moment != only_moment:
                continue
            for i, text in enumerate(lines, 1):
                stem = "vo_%s_%s_%d" % (cls.lower(), moment, i)
                yield cls, moment, i, stem, text, entry["voice_id"], entry["voice_name"]


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--lines", type=pathlib.Path, default=LINES)
    ap.add_argument("--out", type=pathlib.Path, default=VOICE_ROOT)
    ap.add_argument("--api-key")
    ap.add_argument("--only", help="one class, e.g. GRAVEKEEP")
    ap.add_argument("--moment", help="one moment, e.g. win")
    ap.add_argument("--force", action="store_true", help="regenerate clips that exist")
    ap.add_argument("--dry-run", action="store_true", help="print the plan and cost, send nothing")
    args = ap.parse_args()

    data = json.loads(args.lines.read_text(encoding="utf-8"))
    if args.only and args.only not in data["voices"]:
        ap.error("unknown class %r; expected one of %s" % (args.only, ", ".join(data["voices"])))

    todo, skipped, uncast = [], 0, set()
    for cls, moment, i, stem, text, vid, vname in plan(data, args.only, args.moment):
        if not vid:
            uncast.add(cls)
            continue
        dest = args.out / class_folder(cls) / (stem + ".wav")
        if dest.exists() and not args.force:
            skipped += 1
            continue
        todo.append((cls, stem, text, vid, vname, dest))

    chars = sum(len(t) for _, _, t, _, _, _ in todo)
    print("%d line(s) to generate, %d already on disk" % (len(todo), skipped))
    print("~%s characters, ~%s credits" % (format(chars, ","), format(chars * CREDITS_PER_CHAR, ",")))
    if uncast:
        print("SKIPPED, no voice cast: %s" % ", ".join(sorted(uncast)), file=sys.stderr)

    if args.dry_run:
        for cls, stem, text, _, vname, _ in todo:
            print("  %-34s %-30s %s" % (stem, vname[:28], text))
        return 0

    if not todo:
        return 0

    api_key = resolve_api_key(args.api_key)
    if not api_key:
        print(
            "\nNo ElevenLabs API key. Set $ELEVENLABS_API_KEY, write tooling/sfx/.elevenlabs_key,"
            " or pass --api-key.",
            file=sys.stderr,
        )
        return 2

    failures = []
    for cls, stem, text, vid, vname, dest in todo:
        dest.parent.mkdir(parents=True, exist_ok=True)
        try:
            audio = speak(api_key, vid, text)
        except urllib.error.HTTPError as e:
            print("  %-34s FAILED  %s" % (stem, http_error_text(e)))
            failures.append(stem)
            continue
        except urllib.error.URLError as e:
            print("  %-34s FAILED  %s" % (stem, e.reason))
            failures.append(stem)
            continue
        dest.write_bytes(audio)
        print("  %-34s %-22s %s KB   %s" % (stem, vname[:20], format(len(audio) // 1024, ","), text))

    print(
        "\nNow run Emberfall/Addressables/Setup Groups + Migrate Card Data in the Editor,"
        "\nthen commit the .meta files Unity generates. Until then these clips have no"
        "\naddress and will fail silently at runtime."
    )
    if failures:
        print("\n%d failed: %s" % (len(failures), ", ".join(failures)))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
