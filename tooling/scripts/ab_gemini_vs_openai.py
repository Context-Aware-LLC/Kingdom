"""A/B: can Gemini hold the card-art house look that gpt-image-2 established?

THE CONTROL. Every one of the 37 shipped illustrations was generated from
design/art-brief-m1.md on gpt-image-2. This script re-generates a card that
ALREADY SHIPPED, from the SAME brief text, on Gemini — so the prompt is held
fixed and the model is the only variable. The gpt-image-2 side of the
comparison is the file already on disk; this script never calls OpenAI, and
there is no code path here that could.

PROMPT COMPOSITION is art-brief-m1.md's own instruction, §6 header line:
"Prepend §2 global style; use the class palette noted." So, in order:

  1. §2 global style prompt, verbatim.
  2. the §3 class-palette row for the card's class (palette + motifs + mood).
  3. the §6 "Prompt core" cell for that artId.
  4. §2's negative prompt, stated as an "Avoid:" instruction — the Gemini
     image endpoint has no negative-prompt parameter.

Every one of those four strings is PARSED OUT OF THE BRIEF at run time rather
than retyped here. A transcription typo would silently make this a test of a
different prompt, which is the one failure mode that would invalidate the whole
comparison. Deliberately NOT included: gen_card_art.py's TECHNICAL block and
the card-drafts house-look paragraphs (silhouette-first, no background scene) —
both postdate the shipped art, and neither was in front of gpt-image-2 when it
made the images we are matching against.

The Gemini client itself is imported from gen_card_art.py, not reimplemented.

Output, three files side by side per subject:

    art-bakeoff/ab-gemini-vs-gpt/<artId>/gemini_1.png
                                        /gemini_2.png
                                        /gpt_shipped.png   (copy of the shipped PNG)
                                        /prompt.txt

Key comes from GEMINI_API_KEY in the environment only. It is never printed and
never written to a file.

Usage:
  ab_gemini_vs_openai.py [--dry-run] [--candidates N] [--subject art_...]
"""

import argparse
import os
import pathlib
import re
import shutil
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from gen_card_art import (  # noqa: E402  (path must be set first)
    DESIGN,
    EDGE,
    NEGATIVE,
    OUT as CARD_ART_OUT,
    PROVIDERS,
    REPO,
    UNITY_CARDS,
    _conform,
    _post_gemini,
)

BRIEF = DESIGN / "art-brief-m1.md"
OUT = CARD_ART_OUT.parent / "ab-gemini-vs-gpt"

# artId -> (class key as it appears in the §3 table, setId of the shipped PNG).
SUBJECTS = {
    "art_core_throneless_king": ("Neutral (CORE)", "CORE"),
    "art_cinder_drake": ("Emberward", "EMBERFALL"),
}


def _unbold(s: str) -> str:
    return re.sub(r"\*{1,2}(.+?)\*{1,2}", r"\1", s).strip()


def global_style(md: str) -> str:
    """§2's blockquote, verbatim (it is a single '>' paragraph)."""
    sec = re.search(r"^## 2\. Global style prompt.*?^(> .*?)\n\n", md, re.M | re.S)
    if not sec:
        raise ValueError("art-brief-m1.md §2 global style blockquote not found")
    return " ".join(
        line.lstrip("> ").strip() for line in sec.group(1).splitlines()
    ).strip()


def class_palette(md: str, cls: str) -> str:
    """The §3 row for one class, as a sentence: palette, motifs, mood."""
    sec = re.search(r"^## 3\. Class palettes.*?(?=^## 4\.)", md, re.M | re.S)
    if not sec:
        raise ValueError("art-brief-m1.md §3 not found")
    for line in sec.group(0).splitlines():
        cells = [_unbold(c) for c in line.strip().strip("|").split("|")]
        if len(cells) == 4 and cells[0] == cls:
            return (f"Palette: {cells[1]}. Motifs: {cells[2]}. "
                    f"Mood: {cells[3]}.")
    raise ValueError(f"no §3 palette row for class {cls!r}")


def prompt_core(md: str, art_id: str) -> tuple[str, str]:
    """(card name, §6 'Prompt core' cell) for one artId."""
    sec = re.search(r"^## 6\. Card illustrations.*?(?=^## 7\.)", md, re.M | re.S)
    if not sec:
        raise ValueError("art-brief-m1.md §6 not found")
    for line in sec.group(0).splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) == 3 and cells[0].strip("`") == art_id:
            # §6's header line reads "artId — Name (stats/type) → prompt core",
            # so the Card cell carries "Throneless King (6/6, 6, Legendary,
            # Taunt+Reborn)". The parenthetical is card data, not art
            # direction — and feeding literal numerals to an image model
            # invites the numerals onto the canvas, which §2's negative list
            # forbids. Name only.
            name = re.sub(r"\s*\(.*\)\s*$", "", _unbold(cells[1])).strip()
            return name, cells[2]
    raise ValueError(f"no §6 row for artId {art_id!r}")


def build_prompt(md: str, art_id: str) -> tuple[str, str]:
    cls, _ = SUBJECTS[art_id]
    name, core = prompt_core(md, art_id)
    return name, "\n\n".join(
        [
            global_style(md),
            class_palette(md, cls),
            f"SUBJECT — {name}: {core}",
            NEGATIVE,
        ]
    )


def shipped(art_id: str) -> pathlib.Path:
    return UNITY_CARDS / SUBJECTS[art_id][1] / f"{art_id}.png"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--candidates", type=int, default=2)
    ap.add_argument("--subject", action="append", choices=sorted(SUBJECTS))
    args = ap.parse_args()

    md = BRIEF.read_text(encoding="utf-8")
    subjects = args.subject or sorted(SUBJECTS)

    if args.dry_run:
        for art_id in subjects:
            name, prompt = build_prompt(md, art_id)
            print("=" * 78)
            print(f"{art_id}  ({name})   shipped: {shipped(art_id)}")
            print("-" * 78)
            print(prompt)
            print()
        return 0

    key = os.environ.get("GEMINI_API_KEY")
    if not key:
        print("GEMINI_API_KEY is not set in THIS process. A User env var set "
              "after the shell started is not visible here; pull it in the "
              "same command:\n"
              "  $env:GEMINI_API_KEY = [Environment]::GetEnvironmentVariable("
              "'GEMINI_API_KEY','User'); python ...", file=sys.stderr)
        return 2

    model = PROVIDERS["gemini"]["model"]
    made = 0
    for art_id in subjects:
        name, prompt = build_prompt(md, art_id)
        dest_dir = OUT / art_id
        dest_dir.mkdir(parents=True, exist_ok=True)
        (dest_dir / "prompt.txt").write_text(prompt, encoding="utf-8")

        src = shipped(art_id)
        if src.is_file():
            shutil.copyfile(src, dest_dir / "gpt_shipped.png")
            print(f"[{art_id}] gpt_shipped.png <- {src.relative_to(REPO)}")
        else:
            print(f"[{art_id}] WARNING shipped image missing: {src}", file=sys.stderr)

        for i in range(1, args.candidates + 1):
            tag = f"{art_id} gemini {i}"
            print(f"[{tag}] requesting from {model} ...", flush=True)
            t0 = time.time()
            raw, err = _post_gemini(prompt, key)
            if raw is None:
                print(f"[{tag}] FAILED {err}", flush=True)
                continue
            dest = dest_dir / f"gemini_{i}.png"
            dest.write_bytes(raw)
            dims = _conform(dest, tag)
            made += 1
            print(f"[{tag}] -> {dest.relative_to(REPO)} "
                  f"({len(raw)/1e6:.1f} MB, {dims}, {time.time()-t0:.0f}s)",
                  flush=True)

    print(f"\n{made} image(s) -> {OUT}  (requested {EDGE}x{EDGE} square)")
    return 0 if made else 1


if __name__ == "__main__":
    raise SystemExit(main())
