"""Editor comments on cards, kept in a sidecar keyed on the numeric card id.

WHY A SIDECAR AND NOT A CARD FIELD (Sam, 2026-08-27: the comment does not have to reach
cards.json -- it only has to be tied to the card's id so the authoring tools can show it in the
right place).

    * `_comment` parses on a row (it is in CardJson.CardProperties) and is THE WRONG HOME twice
      over. It already means something else -- it is the annotation convention for test fixture
      rows in Fixtures/CardSets/M1.cards.snapshot.json -- and it does not survive a round trip:
      nothing reads it into CardDefinition and CardJsonRowIO.Serialize never writes it back, so
      a comment there lives exactly until the next Unity SaveAll and is then silently dropped.
    * A real new field means editing CardJson, CardDefinition, CardJsonRowIO.Serialize, canon.py
      and every call site that parses a row -- to ship editor chatter inside a client bundle.
    * A sidecar cannot move the canonical hash, so it stays clear of the state-shape hazard in
      CLAUDE.md entirely.

KEYED ON THE NUMERIC ID, WHICH IS THE POINT. The id is the only permanent handle: a handle may
change and nothing may resolve against it, and an artId is the art pipeline's join key. Keying
on either would strand a comment the first time one moved. Ids are stringified because they are
JSON object keys; read them back with int().

THE FILE IS THE INTERFACE. It is plain JSON next to the corpus, so CardAuthoringWindow reads the
same bytes with no engine change and no schema change (see CardComments.cs). Anything that
changes the shape here changes it there in the same commit.

    { "151": [ { "id": "...", "author": "sam", "at": "...", "text": "...",
                 "resolved": false } ] }
"""

from __future__ import annotations

import datetime
import json
import os
import pathlib
import secrets

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[1]

# Overridable for the same reason EMBERFALL_STAGING_DIR is: on Fly this points into the mounted
# volume. A comment is author work exactly like a staged edit, and a machine that suspends with
# it on the ephemeral rootfs loses it with nothing to point at.
COMMENTS_DIR = pathlib.Path(
    os.environ.get("EMBERFALL_COMMENTS_DIR", HERE / "comments"))

COMMENTS_FILENAME = "comments.json"

# Bounds, not policy. This tool is behind Basic auth and single-tenant, so these are not a
# defence against an attacker -- they are the reason a paste accident cannot produce a file the
# Unity window has to render. Both are enforced server-side because a client-side limit is a
# suggestion.
MAX_TEXT = 4000
MAX_PER_CARD = 200


def comments_path() -> pathlib.Path:
    COMMENTS_DIR.mkdir(parents=True, exist_ok=True)
    return COMMENTS_DIR / COMMENTS_FILENAME


def read_all() -> dict[str, list[dict]]:
    """The whole sidecar, or an empty one. Never a fresh file over a damaged one.

    A JSONDecodeError here means somebody's comments are still on disk in a form this cannot
    parse. Starting over would delete them and report success, which is the failure mode the
    staging file already refuses; this refuses it the same way.
    """
    p = COMMENTS_DIR / COMMENTS_FILENAME
    if not p.exists():
        return {}
    try:
        doc = json.loads(p.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        raise RuntimeError(
            f"{p} is not valid JSON ({e}). Move it aside before writing more comments."
        ) from e
    if not isinstance(doc, dict):
        raise RuntimeError(f"{p}: root is not a JSON object keyed on card id.")
    return {str(k): list(v) for k, v in doc.items() if isinstance(v, list)}


def write_all(doc: dict[str, list[dict]]) -> None:
    """Write the sidecar, dropping cards whose last comment was deleted.

    Sorted by numeric id so the file diffs cleanly -- it is a hand-readable artifact that a
    human may well open next to the corpus, and an unordered map re-orders itself on every
    write for no reason.
    """
    pruned = {k: v for k, v in doc.items() if v}
    ordered = {k: pruned[k] for k in sorted(pruned, key=lambda s: int(s))}
    comments_path().write_text(
        json.dumps(ordered, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def _now() -> str:
    return datetime.datetime.now().astimezone().isoformat(timespec="seconds")


def add(card_id: int, text: str, author: str) -> dict:
    """Append one comment to a card. Returns the stored record.

    `author` is the AUTHENTICATED user, passed in by the caller -- never a field off the
    request. A client-supplied author is a byline anyone can forge, and the whole value of the
    field is that it says who actually wrote the note.
    """
    text = (text or "").strip()
    if not text:
        raise ValueError("a comment needs some text.")
    if len(text) > MAX_TEXT:
        raise ValueError(f"comment is {len(text)} characters; the limit is {MAX_TEXT}.")

    doc = read_all()
    bucket = doc.setdefault(str(card_id), [])
    if len(bucket) >= MAX_PER_CARD:
        raise ValueError(f"this card already has {MAX_PER_CARD} comments.")

    record = {
        "id": secrets.token_hex(8),
        "author": author,
        "at": _now(),
        "text": text,
        "resolved": False,
    }
    bucket.append(record)
    write_all(doc)
    return record


def delete(card_id: int, comment_id: str) -> bool:
    doc = read_all()
    bucket = doc.get(str(card_id), [])
    kept = [c for c in bucket if c.get("id") != comment_id]
    if len(kept) == len(bucket):
        return False
    doc[str(card_id)] = kept
    write_all(doc)
    return True


def set_resolved(card_id: int, comment_id: str, resolved: bool) -> bool:
    """Mark a comment done without deleting it.

    Resolve rather than delete is the default action in the UI because a comment is a record of
    a decision as often as it is a task, and the two are indistinguishable once the text is
    gone.
    """
    doc = read_all()
    hit = False
    for c in doc.get(str(card_id), []):
        if c.get("id") == comment_id:
            c["resolved"] = bool(resolved)
            c["resolvedAt"] = _now() if resolved else None
            if not resolved:
                c.pop("resolvedAt", None)
            hit = True
    if hit:
        write_all(doc)
    return hit


def for_card(card_id: int) -> list[dict]:
    return list(read_all().get(str(card_id), []))


def merge(incoming: dict[str, list[dict]]) -> tuple[int, int]:
    """Union another sidecar into this one, keyed on (card id, comment id).

    THIS IS WHY EVERY COMMENT CARRIES A RANDOM ID. Comments accumulate in two places at once --
    on the Fly volume, where the web tool writes them, and in the repo, which is the copy the
    Unity window reads -- and the repo copy is not reachable from the container. Bringing them
    back is a merge, not a copy, and without a stable per-comment id a merge is either a
    duplicate storm or a silent overwrite of whichever side was pulled second.

    Deliberately additive: a comment present here and absent in `incoming` is KEPT. The two
    files are the same log seen from two machines, and a delete on one is indistinguishable
    from a comment the other has not seen yet. Resolving in the wrong direction would delete
    somebody's note to make two files agree.

    Returns (added, updated).
    """
    doc = read_all()
    added = updated = 0
    for card_id, items in incoming.items():
        bucket = doc.setdefault(str(card_id), [])
        by_id = {c.get("id"): c for c in bucket}
        for c in items:
            cid = c.get("id")
            if cid is None or cid not in by_id:
                bucket.append(c)
                added += 1
            elif by_id[cid] != c:
                # Newer `resolved`/`resolvedAt` state wins; the text is append-only and never
                # differs for one id, so this only ever propagates a resolve or a reopen.
                by_id[cid].update(c)
                updated += 1
    write_all(doc)
    return added, updated


def _main(argv: list[str]) -> int:
    """`python tooling/cardweb/comments.py merge <file>` -- bring the deployed sidecar home.

        fly ssh sftp get /data/comments/comments.json pulled.json -a emberfall-cardweb
        python tooling/cardweb/comments.py merge pulled.json

    The repo copy is the one CardAuthoringWindow reads, and the container cannot reach it. This
    is the same shape as reconcile_card_staging.py and exists for the same reason: work done on
    the deployed tool is not in the repo until somebody moves it.
    """
    if len(argv) == 2 and argv[0] == "merge":
        incoming = json.loads(pathlib.Path(argv[1]).read_text(encoding="utf-8"))
        added, updated = merge(incoming)
        print(f"merged into {comments_path()}")
        print(f"COMMENTS_ADDED={added}")
        print(f"COMMENTS_UPDATED={updated}")
        print(f"COMMENTS_CARDS={len(read_all())}")
        return 0
    if not argv or argv[0] == "show":
        doc = read_all()
        total = sum(len(v) for v in doc.values())
        openc = sum(1 for v in doc.values() for c in v if not c.get("resolved"))
        print(f"file       {comments_path()}")
        print(f"COMMENTS_CARDS={len(doc)}")
        print(f"COMMENTS_TOTAL={total}")
        print(f"COMMENTS_OPEN={openc}")
        return 0
    print(__doc__)
    print("usage: comments.py [show] | comments.py merge <file.json>")
    return 2


if __name__ == "__main__":
    import sys
    raise SystemExit(_main(sys.argv[1:]))
