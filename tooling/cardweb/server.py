"""The web card authoring tool -- read the corpus, stage edits, never write a set file.

    python tooling/cardweb/server.py                 # http://127.0.0.1:8781
    python tooling/cardweb/server.py --host 0.0.0.0 --port 8781

WHAT THIS IS ALLOWED TO DO
    Read the three set files. Serve thumbnails. Append to a staging file.

WHAT THIS IS STRUCTURALLY UNABLE TO DO, and why that is the whole point
    Write a card-data file. CardAuthoringWindow.SaveAll writes each set file WHOLE, from what is
    in memory -- so a second editor holding a stale corpus does not merge badly, it reverts
    everything it did not know about. This process has no code path that opens a set file for
    writing. A remote author therefore cannot clobber a local one; the worst they can do is
    stage an edit that reconciliation later reports as conflicted.

    Mint an id, a handle or an artId. See corpus.py -- the client never receives one, and
    sanitize_edit() drops one if a client sends it anyway. The reconcile step is the only
    minting authority, which is what keeps ids from being handed out twice.

THE STAGING FILE IS THE DELIVERABLE
    Every accepted edit carries the row's verbatim source as it was READ, alongside the proposed
    fields. That base is what lets reconciliation distinguish "nobody touched this row locally"
    from "applying this would erase someone's work". An edit without a base is not a safer
    write, it is a delayed one.
"""

from __future__ import annotations

import argparse
import datetime
import http.server
import json
import mimetypes
import pathlib
import socketserver
import subprocess
import sys
import threading

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from corpus import REPO, Corpus, sanitize_edit  # noqa: E402

STATIC_DIR = HERE / "static"
THUMB_DIR = HERE / ".thumbs"
STAGING_DIR = REPO / "tooling" / "cardweb" / "staging"

_lock = threading.Lock()
_corpus: Corpus | None = None


def corpus() -> Corpus:
    assert _corpus is not None
    return _corpus


def enum_options() -> dict:
    """The vocabularies a form may offer, read off the corpus rather than hardcoded.

    Deriving these from the data means the tool cannot offer a class or keyword the engine does
    not have -- but it also means it will not offer a NEW one until some row uses it. That is the
    right trade for a tool that must not invent vocabulary; adding the first card of a new class
    stays a Unity job.
    """
    seen: dict[str, set] = {
        "type": set(), "class": set(), "rarity": set(),
        "setId": set(), "tribe": set(), "keywords": set(),
    }
    for row in corpus().rows:
        d = row.data
        for k in ("type", "class", "rarity", "setId", "tribe"):
            if d.get(k) is not None:
                seen[k].add(d[k])
        for kw in d.get("keywords", []) or []:
            seen["keywords"].add(kw)
    return {k: sorted(v) for k, v in seen.items()}


def staging_path() -> pathlib.Path:
    STAGING_DIR.mkdir(parents=True, exist_ok=True)
    return STAGING_DIR / "pending.json"


def read_staging() -> dict:
    p = staging_path()
    if not p.exists():
        return {"corpusDigest": corpus().source_digest, "edits": []}
    try:
        return json.loads(p.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        # Never silently start a fresh file over a damaged one -- that is how staged work
        # disappears without anything to point at.
        raise RuntimeError(f"{p} is not valid JSON. Move it aside before staging more edits.")


def write_staging(doc: dict) -> None:
    staging_path().write_text(
        json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def staging_view() -> dict:
    """The staging file as the browser may see it: card ids swapped for this run's tokens.

    Staging persists keyed on the card id, because that is the only permanent handle and the
    file outlives any one server run. Tokens are salted per run, so the id -> token mapping is
    resolved here, on the way out, and a staged edit for a row that no longer exists is reported
    rather than dropped.
    """
    doc = read_staging()
    by_id = {r.card_id: r for r in corpus().rows}
    out = []
    for e in doc.get("edits", []):
        row = by_id.get(e.get("cardId"))
        out.append({
            "token": row.token if row else None,
            "setId": e.get("setId"),
            "name": e.get("name", ""),
            "changed": e.get("changed", {}),
            "stagedAt": e.get("stagedAt"),
            "orphaned": row is None,
        })
    return {"corpusDigest": doc.get("corpusDigest", ""), "edits": out}


def stage_edit(token: str, proposed: dict) -> dict:
    """Record one proposed change against the row's base, or replace an earlier one for it."""
    with _lock:
        row = corpus().by_token.get(token)
        if row is None:
            raise KeyError("unknown card token -- reload the page to pick up a fresh corpus.")

        fields = sanitize_edit(proposed)
        if not fields:
            raise ValueError("no editable fields in that edit.")

        # Only keep fields that actually differ from what is on disk, so a staging file records
        # intent rather than a full row echo. A no-op edit stages nothing.
        changed = {k: v for k, v in fields.items() if row.data.get(k) != v}
        doc = read_staging()
        edits = [e for e in doc.get("edits", []) if e.get("cardId") != row.card_id]

        if changed:
            edits.append({
                "op": "update",
                "setId": row.set_id,
                "cardId": row.card_id,
                "name": row.data.get("name", ""),
                "base": row.verbatim,
                "changed": changed,
                "stagedAt": datetime.datetime.now().astimezone().isoformat(timespec="seconds"),
            })

        doc["corpusDigest"] = corpus().source_digest
        doc["edits"] = sorted(edits, key=lambda e: (e.get("setId", ""), e.get("cardId", 0)))
        write_staging(doc)
        return {"staged": bool(changed), "changedFields": sorted(changed), "total": len(edits)}


def run_validation() -> dict:
    """Validate the CURRENT ON-DISK corpus through the shared C-series gate.

    This deliberately validates what is on disk, not what is staged: the staged edits are not a
    corpus until reconciliation has merged them, and validating a merge that has not happened
    would report on a state that exists nowhere. It is here so an author can see whether the
    corpus they are editing against is clean.
    """
    proj = REPO / "src" / "Emberfall.Tools.CardCi"
    if not proj.exists():
        return {"ok": False, "output": "Emberfall.Tools.CardCi not found."}
    try:
        r = subprocess.run(
            ["dotnet", "run", "--project", str(proj), "--",
             str(REPO / "unity" / "Emberfall" / "Assets" / "CardData"),
             str(REPO / "unity" / "Emberfall" / "Assets" / "Art" / "Cards")],
            capture_output=True, text=True, timeout=300, cwd=str(REPO),
        )
        return {"ok": r.returncode == 0, "exit": r.returncode,
                "output": (r.stdout + r.stderr)[-8000:]}
    except FileNotFoundError:
        return {"ok": False, "output": "dotnet not on PATH."}
    except subprocess.TimeoutExpired:
        return {"ok": False, "output": "CardCi timed out after 300s."}


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "EmberfallCardWeb/1.0"

    def log_message(self, fmt, *a):  # quieter than the default
        sys.stderr.write(f"  {self.address_string()} {fmt % a}\n")

    # ---- helpers ----------------------------------------------------------
    def _send(self, code: int, body: bytes, ctype: str, cache: str = "no-store"):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", cache)
        self.end_headers()
        self.wfile.write(body)

    def _json(self, code: int, obj) -> None:
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"),
                   "application/json; charset=utf-8")

    def _static(self, rel: str) -> None:
        # Resolve and confirm containment: a path that escapes the static root is a traversal
        # attempt, and joining before checking is the classic way to serve one by accident.
        target = (STATIC_DIR / rel).resolve()
        if not target.is_file() or STATIC_DIR.resolve() not in target.parents:
            self._json(404, {"error": "not found"})
            return
        ctype = mimetypes.guess_type(target.name)[0] or "application/octet-stream"
        self._send(200, target.read_bytes(), ctype, cache="no-cache")

    # ---- routes -----------------------------------------------------------
    def do_GET(self):
        path = self.path.split("?", 1)[0]

        if path in ("/", "/index.html"):
            self._static("index.html")
        elif path == "/favicon.ico":
            self._send(204, b"", "image/x-icon")
        elif path.startswith("/static/"):
            self._static(path[len("/static/"):])
        elif path == "/api/cards":
            self._json(200, {
                "corpusDigest": corpus().source_digest,
                "options": enum_options(),
                "cards": corpus().views(),
            })
        elif path == "/api/staging":
            try:
                self._json(200, staging_view())
            except RuntimeError as e:
                self._json(500, {"error": str(e)})
        elif path == "/api/validate":
            self._json(200, run_validation())
        elif path.startswith("/thumb/"):
            self._thumb(path[len("/thumb/"):])
        else:
            self._json(404, {"error": "not found"})

    def _thumb(self, token: str) -> None:
        """Serve art by TOKEN, never by artId.

        The URL is the last place an identity key could leak to the client, so the mapping from
        token to file stays here. A row with no art yet gets a 404 and the UI shows a
        placeholder -- new cards have no art until the art pipeline runs, which is expected.
        """
        token = token.split(".")[0]
        row = corpus().by_token.get(token)
        if row is None:
            self._json(404, {"error": "unknown token"})
            return
        src = row.art_path()
        if src is None:
            self._json(404, {"error": "no art"})
            return
        rel = src.relative_to(src.parents[1])
        thumb = THUMB_DIR / rel.parent / (rel.stem + ".webp")
        if thumb.is_file():
            self._send(200, thumb.read_bytes(), "image/webp", cache="max-age=300")
        else:
            # No thumbnail built yet. Refuse rather than serving a 2 MB source PNG -- silently
            # falling back is how a "working" tool ships half a gigabyte per page load.
            self._json(404, {"error": "thumbnail not built; run tooling/cardweb/build_thumbs.py"})

    def do_POST(self):
        path = self.path.split("?", 1)[0]
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            self._json(400, {"error": "bad Content-Length"})
            return
        if length > 1_000_000:
            self._json(413, {"error": "payload too large"})
            return
        raw = self.rfile.read(length) if length else b"{}"
        try:
            body = json.loads(raw.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError) as e:
            self._json(400, {"error": f"bad JSON: {e}"})
            return

        if path == "/api/stage":
            try:
                self._json(200, stage_edit(body.get("token", ""), body.get("fields") or {}))
            except KeyError as e:
                self._json(409, {"error": str(e.args[0])})
            except (ValueError, RuntimeError) as e:
                self._json(400, {"error": str(e)})
        elif path == "/api/reload":
            with _lock:
                corpus().load()
            self._json(200, {"corpusDigest": corpus().source_digest,
                             "rows": len(corpus().rows)})
        else:
            self._json(404, {"error": "not found"})


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> int:
    global _corpus
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=8781)
    args = ap.parse_args()

    _corpus = Corpus()
    sets = {}
    for r in _corpus.rows:
        sets[r.set_id] = sets.get(r.set_id, 0) + 1

    thumbs = len(list(THUMB_DIR.rglob("*.webp"))) if THUMB_DIR.exists() else 0
    print(f"corpus     {len(_corpus.rows)} rows  " +
          "  ".join(f"{k}={v}" for k, v in sorted(sets.items())))
    print(f"digest     {_corpus.source_digest}")
    print(f"thumbs     {thumbs}" + ("" if thumbs else "   (run build_thumbs.py)"))
    print(f"staging    {staging_path()}")
    print(f"serving    http://{args.host}:{args.port}")

    with Server((args.host, args.port), Handler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
