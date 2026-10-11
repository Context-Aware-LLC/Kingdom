#!/usr/bin/env python3
"""Apply the "Kingdom VO Trim" treatment to clips with ffmpeg instead of Audacity.

    python tooling/sfx/macro_ffmpeg.py art-pipeline/sfx/_tomacro --out unity/Assets/Sound/SFX
    python tooling/sfx/macro_ffmpeg.py <dir> --dry-run          # measure and print, write nothing

WHAT IT REPRODUCES, LINE FOR LINE
    %APPDATA%\\audacity\\Macros\\Kingdom VO Trim.txt:

      TruncateSilence: Threshold=-45  Minimum=0.2  Truncate=0.1  Independent=0
      Normalize:       PeakLevel=-3   RemoveDcOffset=1  StereoIndependent=0
      ExportWav

    Silence quieter than -45 dB lasting more than 0.2s is shortened to 0.1s, both channels judged
    together; then the pair is normalised to a -3 dBFS peak with DC removed. THE MACRO IS THE
    SPEC. If it changes, this changes with it -- and if the two ever disagree, the macro wins,
    because that is what Sam listens to.

MEASURING THE PEAK IS THE PART WORTH KEEPING
    ffmpeg's volumedetect works on INTEGER samples and saturates at 0.0 dB, so a clip already
    clipping in the mp3 reports exactly "0.0" and a naive (-3 - 0.0) gain lands wherever the real
    peak happened to be. Decoding to f32 and reading astats Peak_level sees the part above full
    scale, which is what Audacity's normalize sees. Every output is re-measured and corrected once.

WHY NOT DRIVE AUDACITY
    Apply Macro to Files aborts on the first command that errors and its dialog is easy to
    dismiss unread, so a partial run and a complete one look identical afterwards. On 2026-08-28 a
    batch of 29 produced one file, twice. mod-script-pipe is the other option and is disabled in
    audacity.cfg (set to 4, "New"); arming it needs Audacity closed.

    A real difference remains: Audacity's TruncateSilence and ffmpeg's silenceremove are different
    implementations of the same idea and will not agree sample-for-sample. The levels are exact;
    the trim is equivalent, not identical.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

THRESHOLD_DB = -45.0
MIN_SILENCE_S = 0.2
TRUNCATE_TO_S = 0.1
TARGET_DBFS = -3.0
TOLERANCE_DB = 0.05

TRIM = (
    # Leading silence goes entirely: it is the one run whose "truncate to 0.1s" would leave a
    # tenth of a second of nothing before the attack, which on a 2s effect is audible as lateness.
    "silenceremove=start_periods=1:start_duration=0:start_threshold=%.0fdB:detection=peak,"
    # Internal and trailing runs longer than Minimum collapse to Truncate.
    "silenceremove=stop_periods=-1:stop_duration=%.3f:stop_threshold=%.0fdB:"
    "stop_silence=%.3f:detection=peak"
) % (THRESHOLD_DB, MIN_SILENCE_S, THRESHOLD_DB, TRUNCATE_TO_S)


def probe(path: pathlib.Path) -> tuple[float, float, float]:
    """(peak dBFS, worst DC offset, duration) measured on f32 samples."""
    proc = subprocess.run(
        ["ffmpeg", "-v", "info", "-i", str(path),
         "-af", "aformat=sample_fmts=flt,astats=measure_perchannel=Peak_level+DC_offset",
         "-f", "null", "-"], capture_output=True, text=True)
    out = proc.stdout + proc.stderr
    peaks = [float(m) for m in re.findall(r"Peak level dB:\s*(-?\d+\.?\d*)", out)]
    dcs = [float(m) for m in re.findall(r"DC offset:\s*(-?\d+\.?\d*)", out)]
    dur = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
                          "-of", "csv=p=0", str(path)], capture_output=True, text=True).stdout
    if not peaks:
        raise RuntimeError("no peak level for %s" % path.name)
    return max(peaks), (max(dcs, key=abs) if dcs else 0.0), float(dur or 0)


def render(src: pathlib.Path, dest: pathlib.Path, gain_db: float, dc: float, trim: bool) -> None:
    chain = ["aformat=sample_fmts=flt"]
    if trim:
        chain.append(TRIM)
    if abs(dc) > 0.0001:
        chain.append("dcshift=shift=%.6f" % -dc)
    chain.append("volume=%.4fdB" % gain_db)
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-i", str(src),
                    "-af", ",".join(chain), "-c:a", "pcm_s16le", str(dest)], check=True)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source", type=pathlib.Path, help="file or directory of clips")
    ap.add_argument("--out", type=pathlib.Path, help="output directory (default: beside source)")
    ap.add_argument("--target", type=float, default=TARGET_DBFS)
    ap.add_argument("--no-trim", action="store_true", help="normalise only, skip TruncateSilence")
    ap.add_argument("--replace", action="store_true",
                    help="delete the source and carry its .meta over to the .wav")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    sources = ([args.source] if args.source.is_file()
               else sorted(p for p in args.source.iterdir()
                           if p.suffix.lower() in (".mp3", ".wav", ".ogg")))
    if not sources:
        print("nothing to process under %s" % args.source)
        return 1
    out_dir = args.out or args.source.parent
    out_dir.mkdir(parents=True, exist_ok=True)

    print("%-22s %9s %8s %9s %16s" % ("clip", "src peak", "gain", "out peak", "duration"))
    failures = []
    for src in sources:
        peak, dc, dur = probe(src)
        gain = args.target - peak
        if args.dry_run:
            print("%-22s %9.1f %8.1f %9s %8.2fs" % (src.name, peak, gain, "-", dur))
            continue

        dest = out_dir / (src.stem + ".wav")
        render(src, dest, gain, dc, trim=not args.no_trim)
        out_peak, _, out_dur = probe(dest)
        if abs(out_peak - args.target) > TOLERANCE_DB:
            # Trimming changes which sample is the peak, so the first pass can miss. Correct
            # against the rendered file, which is the thing that is actually wrong.
            fixed = dest.with_suffix(".fix.wav")
            render(dest, fixed, args.target - out_peak, 0.0, trim=False)
            fixed.replace(dest)
            out_peak, _, out_dur = probe(dest)
        if abs(out_peak - args.target) > TOLERANCE_DB:
            failures.append(dest.name)
        if args.replace and dest != src:
            # THE GUID FOLLOWS THE SOUND. AudioImporter meta is format-agnostic, and an
            # Addressables entry points at the guid inside it -- so the meta is RENAMED, never
            # deleted and regenerated. Deleting the pair mints a new guid and leaves the entry
            # dangling, silently, which is the failure the "never delete the file and .meta
            # pair" rule exists for.
            meta = src.with_name(src.name + ".meta")
            if meta.exists():
                meta.rename(dest.with_name(dest.name + ".meta"))
            src.unlink()

        print("%-22s %9.1f %8.1f %9.1f %7.2fs -> %.2fs" % (
            dest.name, peak, gain, out_peak, dur, out_dur))

    if failures:
        print("\n%d clip(s) missed %.1f dBFS: %s" % (len(failures), args.target,
                                                     ", ".join(failures)), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
