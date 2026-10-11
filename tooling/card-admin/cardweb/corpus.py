"""Corpus loading for the web card authoring tool.

WHY THIS EXISTS (Sam, 2026-08-27). The Unity authoring window is the only way to edit a card
row, and it can only run where Unity runs. This module is the read half of a web front end for
the same corpus: it loads every set file, hands out an IDENTITY-FREE view of each row, and keeps
the identity server-side.

THE THREE KEYS NEVER LEAVE THIS PROCESS.
`id`, `handle` and `artId` are not rendered, not sent, and not accepted back. The client holds an
opaque `token` and nothing else. That is a deliberate constraint, not a convenience:

  - `id` is minted from a per-type band by the reconcile step, which is the ONLY minting
    authority. CardAuthoringWindow.NextIdInBand says outright that it is "not a reservation:
    two authors working at once would both be handed the same next number". A web client that
    cannot see an id cannot propose one, so the collision class does not exist here.
  - `handle` must never be resolved against. `CardDefinition.Handle` carries Sam's 2026-08-15
    ruling -- "the test is that you can change a handle and have nothing break".
  - `artId` is the art pipeline's permanent join key. Exposing it to an editor invites editing
    it. A changed artId no longer strands seeds -- those key on the numeric id since
    2026-08-27 -- but it still moves the png, its .meta and the prompt manifest (see seed_key in
    gen_card_prompt_briefs.py, which lists the 18 that were stranded for real).

LOADING IS ALL-OR-NOTHING, ACROSS ALL SETS.
CardAuthoringWindow.LoadAll learned this the expensive way twice, and both lessons apply here:
a partial load makes cross-set rule references read as unresolved, and it makes any id scan
wrong. We never write set files from this process, so we cannot repeat the truncation half of
that bug -- but a validation result computed against 179 of 268 rows is wrong in exactly the
way that looks right.
"""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import secrets
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]

# Overridable so the server can run somewhere that is not a repo checkout -- a container image
# carries the three set files and the thumbnail cache, and nothing else of the 518 MB art tree.
CARD_DATA_DIR = pathlib.Path(
    os.environ.get("EMBERFALL_CARD_DIR", REPO / "unity" / "Assets" / "CardData"))
ART_DIR = pathlib.Path(
    os.environ.get("EMBERFALL_ART_DIR", REPO / "unity" / "Assets" / "Art" / "Cards"))
# The servable images. This is the ONLY art the deployed server has -- a container ships the
# 4.2 MB cache and none of the 518 MB source tree, so art resolution must succeed against the
# thumbnails alone and treat ART_DIR as an optional extra.
THUMB_DIR = pathlib.Path(
    os.environ.get("EMBERFALL_THUMB_DIR", pathlib.Path(__file__).resolve().parent / ".thumbs"))

# art_file() is THE definition of a card's art filename. Import it; do not re-derive it.
# Three scripts re-derived it off the handle and two of them were wrong -- see its docstring.
sys.path.insert(0, str(REPO / "tooling" / "scripts"))
from gen_card_prompt_briefs import art_file  # noqa: E402

# The set files, and the only list of them this tool has. CardJsonRowIO.PathForSet is the
# authority on which sets exist; this mirrors it and a new set means editing both.
SET_FILENAMES = {
    "CORE": "Core.cards.json",
    "PENDINGHAM": "Pendingham.cards.json",
    "REKINDLING": "Rekindling.cards.json",
}


def set_files(card_dir: pathlib.Path | None = None) -> dict[str, pathlib.Path]:
    """The set-file map, rooted anywhere. Overridable so the reconcile step can be exercised
    against a copy of the corpus rather than the live one -- a merge tool that can only be
    tested by running it on the real thing does not get tested."""
    root = card_dir or CARD_DATA_DIR
    return {s: root / n for s, n in SET_FILENAMES.items()}


SET_FILES = set_files()

# Fields a web author may change. Everything outside this set is passed through verbatim.
#
# `rules` and `soundEffects` are deliberately NOT here. A rule that parses is not a rule that
# fires, liveness is per source type, and the trigger/source pairing is enforced by CardValidator
# rule C21 -- authoring that in a browser form would produce cards that validate and do nothing.
# The web tool shows them read-only; the Unity window remains the place to author them.
EDITABLE_FIELDS = frozenset({
    "name", "type", "cost", "attack", "health", "durability",
    "class", "rarity", "keywords", "text", "flavorText", "tooltip",
    "setId", "collectible", "lifecycle", "tribe", "factionId",
})

# Never accepted from a client, at any nesting depth, for any reason.
IDENTITY_FIELDS = frozenset({"id", "handle", "artId"})


def row_base(data: dict) -> str:
    """The BASE fingerprint of a row: what it looked like when a web author started editing it.

    Both the server (writing a staged edit) and the reconcile step (deciding whether that edit
    is still safe to apply) must compute this the same way, so it is defined once, here. It is
    deliberately NOT the canonical on-disk serialization -- sorting the keys makes it immune to
    a field-order change in the writer, which would otherwise invalidate every staged edit the
    moment CardJsonRowIO.Serialize gained a field.
    """
    return json.dumps(data, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


class Row:
    """One card row: its identity (server-side only), its verbatim source, and its parsed form.

    `verbatim` is the row's source JSON exactly as it was read. It is the BASE for reconciliation
    -- the thing that answers "did anyone change this row locally while the web author was
    editing it?". Without it a staged edit cannot tell "nobody touched this" from "I am about to
    erase Sam's work", and the whole scheme degrades to last-write-wins with extra steps.
    """

    __slots__ = ("set_id", "index", "data", "verbatim", "token")

    def __init__(self, set_id: str, index: int, data: dict, verbatim: str, token: str):
        self.set_id = set_id
        self.index = index
        self.data = data
        self.verbatim = verbatim
        self.token = token

    @property
    def card_id(self) -> int:
        return self.data["id"]

    def art_path(self) -> pathlib.Path | None:
        """The source art file for this row, or None if it has none yet.

        Joined on artId through art_file(), never rebuilt from the id or the handle. The set
        folder is part of the path because <setId> decides which Addressables bundle the file
        ships in -- a file under the wrong set folder still renders and still passes CI.
        """
        stem = art_file(self.data)
        for ext in (".png", ".jpg", ".jpeg", ".webp"):
            p = ART_DIR / self.data["setId"] / f"{stem}{ext}"
            if p.exists():
                return p
        # Fall back to a scan: a row whose art sits under the WRONG set folder still renders in
        # game, so refusing to find it here would report a card as art-less when it is not.
        for ext in (".png", ".jpg", ".jpeg", ".webp"):
            hits = list(ART_DIR.glob(f"*/{stem}{ext}"))
            if hits:
                return hits[0]
        return None

    def thumb_path(self) -> pathlib.Path | None:
        """The servable image for this row, resolved against the thumbnail cache.

        Joined on artId through art_file() like everything else in the art pipeline. The cache
        mirrors the source tree's <setId>/<stem> layout, so this is the same lookup art_path()
        does -- but it must work with NO source tree present, which is the deployed case.
        """
        stem = art_file(self.data)
        p = THUMB_DIR / self.data["setId"] / f"{stem}.webp"
        if p.is_file():
            return p
        # A row whose art sits under a different set folder than its setId still renders in
        # game, so refusing to find it here would report a card as art-less when it is not.
        hits = list(THUMB_DIR.glob(f"*/{stem}.webp"))
        return hits[0] if hits else None

    def view(self) -> dict:
        """The identity-free payload the browser receives."""
        out = {"token": self.token}
        for k, v in self.data.items():
            if k in IDENTITY_FIELDS:
                continue
            out[k] = v
        out["hasArt"] = self.thumb_path() is not None
        return out


class Corpus:
    """Every row of every set file, loaded whole."""

    def __init__(self):
        self.rows: list[Row] = []
        self.by_token: dict[str, Row] = {}
        self.source_digest: str = ""
        self._salt = secrets.token_hex(8)
        self.load()

    def load(self) -> None:
        self.rows.clear()
        self.by_token.clear()
        hasher = hashlib.sha256()

        missing = [s for s, p in SET_FILES.items() if not p.exists()]
        if missing:
            # Loud, and fatal. A corpus missing a set file is the state in which every cross-set
            # reference reads as unresolved -- a silent partial load is the failure this refuses.
            raise SystemExit(
                f"card-data files missing for set(s) {', '.join(missing)} under {CARD_DATA_DIR}. "
                "Refusing to serve a partial corpus."
            )

        for set_id in sorted(SET_FILES):
            path = SET_FILES[set_id]
            text = path.read_text(encoding="utf-8")
            hasher.update(text.encode("utf-8"))
            try:
                parsed = json.loads(text)
            except json.JSONDecodeError as e:
                raise SystemExit(f"{path.name} is not valid JSON: {e}") from e
            if not isinstance(parsed, list):
                raise SystemExit(f"{path.name}: root is not a JSON array of card rows.")

            for i, row_data in enumerate(parsed):
                verbatim = row_base(row_data)
                token = hashlib.sha256(
                    f"{self._salt}:{set_id}:{row_data['id']}".encode("utf-8")
                ).hexdigest()[:16]
                row = Row(set_id, i, row_data, verbatim, token)
                self.rows.append(row)
                self.by_token[token] = row

        self.source_digest = hasher.hexdigest()[:16]

    def views(self) -> list[dict]:
        return [r.view() for r in self.rows]


def sanitize_edit(proposed: dict) -> dict:
    """Keep only fields a web author is allowed to change.

    Anything else -- an identity key, a rules array, a field invented by a stale client -- is
    dropped here rather than filtered downstream, so there is exactly one place to read to know
    what a web edit can possibly touch.
    """
    return {k: v for k, v in proposed.items() if k in EDITABLE_FIELDS}
