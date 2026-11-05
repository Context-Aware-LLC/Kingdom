"""Canonical card-row serialization, byte-compatible with the C# writer.

WHY THIS EXISTS, AND WHY IT IS DELICATE
`CardJsonRowIO.Serialize` is the only thing that writes a card row, and it lives in
`Kingdom.Editor` -- an Editor-only assembly. `CardJson` in Core.Cards is a READER only. So
nothing outside Unity can write a card row, which is exactly why the web tool stages edits
instead of applying them, and why this module has to exist for the reconcile step.

Two independent things have to match, or the corpus churns:

1. FIELD ORDER. Mirrored from CardJsonRowIO.Serialize below. A field the C# writer omits when
   empty must be omitted here too -- writing `"text": ""` where C# writes nothing is a diff on
   every subsequent save, in both directions, forever.

2. ESCAPING. JavaScriptEncoder.UnsafeRelaxedJsonEscaping, the writer's encoder since
   3c4a516c (2026-10-15; before that it was the default encoder, which escaped + ' & < > `).
   Probed directly against net10.0 on 2026-11-04 over every ASCII code point plus
   representative non-ASCII; this is that measurement, not a reading of the docs:

       "   ->  \\"          \\   ->  \\\\          every other printable ASCII is raw
       Backspace/tab/newline/formfeed/return take their short forms; every other control
       and U+007F go to \\uXXXX, UPPERCASE hex.
       Non-ASCII is raw (e with acute, em dash, euro, even U+00AD) EXCEPT separators
       (U+00A0, U+2028, U+2029), U+FEFF and astral code points (surrogate pairs).

VERIFY, DO NOT TRUST. `--selftest` re-serializes every row of every set file and compares
bytes. Run it after any change here, and after any change to the C# writer. A field added to
CardJsonRowIO.Serialize and not to CANONICAL_ORDER shows up there and nowhere else.

RETIREMENT TRIGGER: if `Serialize` ever moves from Kingdom.Editor into Kingdom.Core.Cards
(where a net10.0 tool could call it directly), this module should be deleted and the reconcile
step should shell out to that instead. Two implementations of one format is a standing cost;
it is only paid here because there is currently no second way to write a row.
"""

from __future__ import annotations

import json
import pathlib
import sys
import unicodedata

# Short escapes System.Text.Json emits instead of \uXXXX. Measured, not assumed.
_SHORT = {
    0x08: "\\b", 0x09: "\\t", 0x0A: "\\n", 0x0C: "\\f", 0x0D: "\\r", 0x22: '\\"', 0x5C: "\\\\",
}

# Non-ASCII categories the relaxed encoder still escapes: separators, controls, surrogates,
# private use and unassigned. U+FEFF is escaped though it is Cf; U+00AD (also Cf) is not.
_ESCAPED_CATEGORIES = frozenset(("Zs", "Zl", "Zp", "Cc", "Cs", "Co", "Cn"))


def _escapes_non_ascii(cp: int) -> bool:
    return cp > 0xFFFF or cp == 0xFEFF or unicodedata.category(chr(cp)) in _ESCAPED_CATEGORIES


def escape(s: str) -> str:
    """Encode a string body the way UnsafeRelaxedJsonEscaping does."""
    out = []
    for ch in s:
        cp = ord(ch)
        if cp in _SHORT:
            out.append(_SHORT[cp])
        elif cp < 0x20 or cp == 0x7F or (cp > 0x7F and _escapes_non_ascii(cp)):
            # Uppercase hex, and surrogate pairs for astral code points -- STJ writes UTF-16.
            if cp > 0xFFFF:
                cp -= 0x10000
                out.append(f"\\u{0xD800 + (cp >> 10):04X}")
                out.append(f"\\u{0xDC00 + (cp & 0x3FF):04X}")
            else:
                out.append(f"\\u{cp:04X}")
        else:
            out.append(ch)
    return "".join(out)


def _val(v) -> str:
    """Serialize any JSON value. Objects keep their existing key order.

    Preserving insertion order is what makes `rules` and `soundEffects` round-trip byte-exactly
    without reimplementing WriteActivation/WriteCondition/WriteLifetime/WriteAction here --
    json.loads preserves key order, so a sub-object the web tool never edits comes back out in
    the order the C# writer put it in.
    """
    if v is None:
        return "null"
    if v is True:
        return "true"
    if v is False:
        return "false"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        # No card field is a float today, and the engine forbids float anywhere state-adjacent.
        raise ValueError(f"float in card data: {v!r}")
    if isinstance(v, str):
        return f'"{escape(v)}"'
    if isinstance(v, list):
        return "[" + ",".join(_val(x) for x in v) + "]"
    if isinstance(v, dict):
        return "{" + ",".join(f'"{escape(k)}":{_val(x)}' for k, x in v.items()) + "}"
    raise TypeError(f"cannot serialize {type(v).__name__}")


# Mirrors CardJsonRowIO.Serialize, in its order. `omit_empty` marks the fields C# writes only
# when non-empty; `always` are written unconditionally even when empty.
CANONICAL_ORDER = [
    ("id", "always"), ("handle", "always"), ("name", "always"), ("type", "always"),
    ("cost", "always"),
    ("attack", "omit_none"), ("health", "omit_none"), ("durability", "omit_none"),
    ("class", "always"), ("rarity", "always"), ("keywords", "always"),
    ("tribe", "omit_none"),
    ("setId", "always"), ("collectible", "always"), ("lifecycle", "omit_published"),
    ("artId", "always"),
    ("soundEffects", "omit_empty"),
    ("flavorText", "omit_empty"), ("tooltip", "omit_empty"), ("factionId", "omit_empty"),
    ("text", "omit_empty"),
    ("rules", "always"),
]
_KNOWN = {k for k, _ in CANONICAL_ORDER}


def serialize_row(row: dict) -> str:
    """One card row, exactly as CardJsonRowIO.Serialize would write it."""
    unknown = set(row) - _KNOWN
    if unknown:
        # A field this module does not know about would be silently DROPPED, which is data loss
        # that no test would catch. Refuse instead.
        raise ValueError(
            f"card {row.get('id')} has field(s) {sorted(unknown)} that CANONICAL_ORDER does not "
            "list. CardJsonRowIO.Serialize has gained a field; add it here in the same position."
        )

    parts = []
    for key, mode in CANONICAL_ORDER:
        if key not in row:
            continue
        v = row[key]
        if mode == "omit_none" and v is None:
            continue
        if mode == "omit_empty" and (v is None or v == "" or v == []):
            continue
        if mode == "omit_published" and v in (None, "PUBLISHED"):
            continue
        parts.append(f'"{escape(key)}":{_val(v)}')
    return "{" + ",".join(parts) + "}"


def serialize_file(rows: list[dict]) -> str:
    """A whole set file: one row per line, two-space indent, trailing newline. LF endings.

    Matches CardJsonRowIO.SaveRows. The one-row-per-line shape is not cosmetic -- it is what
    makes a card-level change a single-line diff, and therefore what makes this whole
    stage-and-reconcile scheme reviewable.
    """
    out = ["[\n"]
    for i, r in enumerate(rows):
        out.append("  " + serialize_row(r))
        if i < len(rows) - 1:
            out.append(",")
        out.append("\n")
    out.append("]\n")
    return "".join(out)


# --------------------------------------------------------------------------
def selftest(card_dir: pathlib.Path) -> int:
    """Re-serialize every row of every set file and compare bytes with what is on disk."""
    files = sorted(card_dir.glob("*.cards.json"))
    if not files:
        print(f"no *.cards.json under {card_dir}", file=sys.stderr)
        return 2

    total = mismatched = 0
    for f in files:
        original = f.read_text(encoding="utf-8")
        rows = json.loads(original)
        total += len(rows)

        bad = []
        for r in rows:
            src = json.dumps(r)  # only to detect a parse-order surprise; not compared
            del src
            try:
                got = serialize_row(r)
            except ValueError as e:
                bad.append((r.get("id"), str(e)))
                continue
            # Find this row's line in the original and compare.
            want = None
            for line in original.splitlines():
                stripped = line.strip().rstrip(",")
                if stripped.startswith('{"id":%d,' % r["id"]):
                    want = stripped
                    break
            if want is None:
                bad.append((r.get("id"), "row not found as a single line in the source file"))
            elif want != got:
                bad.append((r.get("id"), "BYTES DIFFER"))
                if len(bad) <= 2:
                    for i, (a, b) in enumerate(zip(want, got)):
                        if a != b:
                            print(f"    first difference at offset {i}:")
                            print(f"      disk: ...{want[max(0,i-40):i+40]}...")
                            print(f"      ours: ...{got[max(0,i-40):i+40]}...")
                            break

        # Whole-file check too, which catches separator/indent/trailing-newline drift.
        rebuilt = serialize_file(rows)
        file_ok = rebuilt == original
        mismatched += len(bad)
        status = "OK " if (not bad and file_ok) else "FAIL"
        print(f"{status} {f.name:24} {len(rows):4} rows"
              + ("" if file_ok else "   whole-file bytes DIFFER")
              + ("" if not bad else f"   {len(bad)} row(s) differ"))
        for cid, why in bad[:5]:
            print(f"       card {cid}: {why}")

    print(f"\n{total} rows checked, {mismatched} mismatched.")
    return 0 if mismatched == 0 else 1


if __name__ == "__main__":
    REPO = pathlib.Path(__file__).resolve().parents[3]  # tooling/card-admin/cardweb/canon.py
    raise SystemExit(selftest(REPO / "unity" / "Assets" / "CardData"))
