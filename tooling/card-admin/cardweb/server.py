"""The web card authoring tool -- read the corpus, stage edits, never write a set file.

    python tooling/cardweb/server.py                 # http://127.0.0.1:8781
    python tooling/cardweb/server.py --host 0.0.0.0 --port 8781

WHAT THIS IS ALLOWED TO DO
    Read the three set files. Serve thumbnails. Append to a staging file. Append to a comments
    sidecar. Serve the card-admin panel and a rollup computed off the loaded corpus.

    Both writable files live OUTSIDE the corpus, on the volume: staging/pending.json and
    comments/comments.json. Neither can move the canonical hash and neither ships to a client.

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
import base64
import datetime
import hmac
import http.server
import json
import mimetypes
import os
import pathlib
import socketserver
import subprocess
import sys
import threading
from collections import defaultdict

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import comments as comments_store  # noqa: E402
from corpus import (  # noqa: E402
    IDENTITY_FIELDS, REPO, SET_FILENAMES, THUMB_DIR, Corpus, sanitize_edit,
)

STATIC_DIR = HERE / "static"

# The corpus analytics panel, served as a tab rather than reimplemented. Overridable for the
# same reason the card directory is: the container carries this directory and nothing around it.
ADMIN_DIR = pathlib.Path(
    os.environ.get("EMBERFALL_ADMIN_DIR", REPO / "tooling" / "card-admin"))
sys.path.insert(0, str(ADMIN_DIR))

# The aggregation is IMPORTED, never reimplemented. Two rollups over one corpus would disagree
# eventually and the tab that disagreed would be the one nobody re-derived. build_data.py globs
# its own CARD_FILES at import time; that glob is unused here (the rows come from the loaded
# Corpus) and resolves to an empty list in the container, which is harmless.
from build_data import CARD_TYPES, normalize, rollup  # noqa: E402
# Staged edits must outlive a restart. On Fly this points at a mounted volume; a machine that
# suspends and resumes with staging on its ephemeral disk would silently drop an author's work.
STAGING_DIR = pathlib.Path(
    os.environ.get("EMBERFALL_STAGING_DIR", REPO / "tooling" / "cardweb" / "staging"))

# HTTP Basic auth. With NO credential configured the server runs open, which is correct for the
# 127.0.0.1 default and wrong for anything else; main() refuses to bind a non-loopback interface
# without one rather than leaving that to whoever writes the deploy.
#
# TWO SOURCES, because the live app already holds a CARDWEB_PASSWORD secret that is write-only
# and cannot be read back. Dropping support for it would have locked the deploy out until
# somebody re-set a secret nobody can read:
#
#   CARDWEB_USER / CARDWEB_PASSWORD   one pair, the original form
#   CARDWEB_USERS                     "user:password" entries, comma- or newline-separated
#
# Passwords live in the environment (a `fly secrets` value in production) and NOWHERE in the
# repo. A credential in a tracked file is one that outlives every rotation of it.
AUTH_USER = os.environ.get("CARDWEB_USER", "emberfall")
AUTH_PASSWORD = os.environ.get("CARDWEB_PASSWORD", "")


def _load_credentials() -> dict[str, str]:
    """The user -> password table, from both env forms.

    A malformed entry is DROPPED WITH A WARNING rather than ignored silently: the failure mode
    it produces is "my login stopped working", which nobody debugs by reading the parser.
    """
    creds: dict[str, str] = {}
    if AUTH_PASSWORD:
        creds[AUTH_USER] = AUTH_PASSWORD
    raw = os.environ.get("CARDWEB_USERS", "")
    for entry in raw.replace("\n", ",").split(","):
        entry = entry.strip()
        if not entry:
            continue
        user, sep, password = entry.partition(":")
        user, password = user.strip(), password.strip()
        if not sep or not user or not password:
            print(f"CARDWEB_USERS: ignoring malformed entry {entry.split(':')[0]!r} "
                  "(expected user:password).", file=sys.stderr)
            continue
        creds[user] = password
    return creds


CREDENTIALS = _load_credentials()

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
    out = {k: sorted(v) for k, v in seen.items()}
    # Fixed vocabulary, not read off the corpus: no row carries DRAFT or RETIRED until one is
    # authored, and a derived list would never offer them. Mirrors the CardLifecycle enum.
    out["lifecycle"] = ["PUBLISHED", "DRAFT", "RETIRED"]
    return out


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


def discard_edit(token: str) -> dict:
    """Drop the staged edit for one row.

    Without this a staged edit is unreachable from the UI: reverting the fields makes the Stage
    button inert (there is nothing to stage), so the only way back was to hand-edit the staging
    file. Staging something by mistake has to be undoable from the same screen that did it.
    """
    with _lock:
        row = corpus().by_token.get(token)
        if row is None:
            raise KeyError("unknown card token -- reload the page to pick up a fresh corpus.")
        doc = read_staging()
        before = len(doc.get("edits", []))
        doc["edits"] = [e for e in doc.get("edits", []) if e.get("cardId") != row.card_id]
        write_staging(doc)
        return {"discarded": before != len(doc["edits"]), "total": len(doc["edits"])}


def admin_data() -> dict:
    """The rollup the corpus panel renders, computed from the corpus THIS PROCESS HOLDS.

    Deliberately not tooling/card-admin/data.json. That file is a build artifact of
    build_data.py and is only ever as current as the last rebake, so serving it would put a
    rollup of some earlier corpus in a tab sitting next to the live grid -- and nothing about a
    stale number looks stale. Reading the loaded rows instead means the tab cannot disagree
    with the grid beside it.

    IDENTITY IS STRIPPED HERE, not trusted to the panel. normalize() emits `id` and `artId`
    because its file:// consumer runs off a repo checkout that already has them; this one is
    the browser, which must never receive either. Filtering on corpus.IDENTITY_FIELDS rather
    than an allowlist of panel fields means a field the panel starts reading arrives on its own,
    while a new identity key added to IDENTITY_FIELDS is withheld on its own.
    """
    cards = []
    for row in corpus().rows:
        rec = normalize(row.data, SET_FILENAMES.get(row.set_id, row.set_id))
        cards.append({k: v for k, v in rec.items() if k not in IDENTITY_FIELDS})

    classes = sorted({c["class"] for c in cards if c["class"]})
    by_class = defaultdict(list)
    for c in cards:
        by_class[c["class"]].append(c)

    return {
        "generatedFrom": sorted(SET_FILENAMES.values()),
        "totalCards": len(cards),
        "classes": classes,
        "cardTypes": CARD_TYPES,
        "corpus": rollup(cards),
        "byClass": {cls: rollup(by_class[cls]) for cls in classes},
        "cards": cards,
    }


def comments_view() -> dict:
    """The sidecar as the browser may see it: card ids swapped for this run's tokens.

    The same resolution staging_view() does, and for the same reason -- the file is keyed on the
    card id because that is the only permanent handle and it outlives any one server run, while
    tokens are salted per run. A comment on a row that no longer exists is REPORTED, not
    dropped: silently hiding it is how a note about a deleted card becomes unfindable.
    """
    doc = comments_store.read_all()
    by_id = {str(r.card_id): r for r in corpus().rows}
    out: dict[str, list] = {}
    orphans = 0
    for card_id, items in doc.items():
        row = by_id.get(card_id)
        if row is None:
            orphans += len(items)
            continue
        out[row.token] = items
    return {"comments": out, "orphans": orphans}


def add_comment(token: str, text: str, author: str) -> dict:
    with _lock:
        row = corpus().by_token.get(token)
        if row is None:
            raise KeyError("unknown card token -- reload the page to pick up a fresh corpus.")
        return comments_store.add(row.card_id, text, author)


def mutate_comment(token: str, comment_id: str, op: str, resolved: bool = True) -> dict:
    with _lock:
        row = corpus().by_token.get(token)
        if row is None:
            raise KeyError("unknown card token -- reload the page to pick up a fresh corpus.")
        if op == "delete":
            return {"ok": comments_store.delete(row.card_id, comment_id)}
        return {"ok": comments_store.set_resolved(row.card_id, comment_id, resolved)}


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
             str(REPO / "unity" / "Assets" / "CardData"),
             str(REPO / "unity" / "Assets" / "Art" / "Cards")],
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

    # ---- auth -------------------------------------------------------------
    def _credential(self) -> str | None:
        """The authenticated username, or None. The ONE place a password is checked.

        Constant time in two directions, and the second one is easy to lose:

          * Against the PASSWORD -- a plain `==` leaks its length and its prefix to a patient
            caller; hmac.compare_digest is the same line without that.
          * Against the SET OF USERNAMES -- a dict lookup that misses returns before any
            password comparison happens, so response time says whether a username exists.
            That is username enumeration, and with two named accounts it is most of the guess.
            So every entry is compared every time: the loop does not break early, and the
            combining operator is `&` rather than `and` precisely because `and` short-circuits.

        Returns the CONFIGURED spelling of the user, not the client's, so nothing downstream
        can be handed a string the table never contained.
        """
        header = self.headers.get("Authorization", "")
        if not header.startswith("Basic "):
            return None
        try:
            user, _, password = base64.b64decode(header[6:]).decode("utf-8").partition(":")
        except (ValueError, UnicodeDecodeError):
            return None

        matched: str | None = None
        for known_user, known_password in CREDENTIALS.items():
            ok = (hmac.compare_digest(user, known_user)
                  & hmac.compare_digest(password, known_password))
            if ok:
                matched = known_user
        return matched

    def _authed(self) -> bool:
        if not CREDENTIALS:
            return True
        return self._credential() is not None

    def _auth_user(self) -> str:
        """Who is writing, taken from the credential rather than from the request body.

        A byline the client supplies is one anyone can forge, and the only value the field has
        is that it says who actually wrote the note. With no credential configured the server is
        bound to loopback (main() refuses otherwise), so there is exactly one author and it is
        whoever is at the keyboard.
        """
        if not CREDENTIALS:
            return "local"
        return self._credential() or "unknown"

    def _challenge(self) -> None:
        body = b'{"error":"authentication required"}'
        self.send_response(401)
        self.send_header("WWW-Authenticate", 'Basic realm="Emberfall card authoring"')
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

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

    # The card-admin panel's own files, served UNMODIFIED under /admin/ so that every relative
    # reference in its index.html (styles.css, data.js, admin-panel.js) resolves without a
    # rewrite. One copy of that markup exists, in tooling/card-admin/, and it stays the copy
    # both the embedded tab and the standalone file:// mode render -- an id renamed there
    # cannot desynchronise from a duplicate here, because there is no duplicate.
    ADMIN_FILES = {
        "": ("index.html", "text/html; charset=utf-8"),
        "index.html": ("index.html", "text/html; charset=utf-8"),
        "styles.css": ("styles.css", "text/css; charset=utf-8"),
        "admin-panel.js": ("admin-panel.js", "application/javascript; charset=utf-8"),
    }

    # Appended to styles.css ON THE WAY OUT, and only there. The standalone file:// page reads
    # styles.css off disk and never sees this, so the embedded tab can drop the panel's own
    # page title -- redundant under the tab strip that already names it -- without changing how
    # the panel looks when opened on its own.
    ADMIN_EMBED_CSS = (
        b"\n/* Appended by cardweb's /admin/styles.css route for the embedded tab only. */\n"
        b"header { padding: 8px 16px; }\n"
        b"header h1 { display: none; }\n"
        b".sidebar { min-height: calc(100vh - 34px); }\n"
    )

    def _admin(self, rel: str) -> None:
        # data.js is INTERCEPTED rather than served: on disk it is build_data.py's mirror, as
        # current as the last rebake and no more. Generating it from the loaded corpus is what
        # keeps the tab from showing a rollup of a corpus that has since moved -- the failure
        # this tab would otherwise have by default, silently.
        if rel == "data.js":
            body = ("window.CARD_DATA = "
                    + json.dumps(admin_data(), ensure_ascii=False) + ";\n")
            self._send(200, body.encode("utf-8"), "application/javascript; charset=utf-8")
            return
        entry = self.ADMIN_FILES.get(rel)
        if entry is None:
            self._json(404, {"error": "not found"})
            return
        name, ctype = entry
        target = ADMIN_DIR / name
        if not target.is_file():
            self._json(500, {"error": f"{name} missing from {ADMIN_DIR}"})
            return
        body = target.read_bytes()
        if name == "styles.css":
            body += self.ADMIN_EMBED_CSS
        self._send(200, body, ctype, cache="no-cache")

    # ---- routes -----------------------------------------------------------
    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/healthz":
            # Unauthenticated on purpose: Fly's checks have no credentials, and it reveals
            # nothing but that the process is up.
            self._json(200, {"ok": True, "rows": len(corpus().rows)})
            return
        if not self._authed():
            self._challenge()
            return

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
        elif path == "/api/comments":
            try:
                self._json(200, comments_view())
            except RuntimeError as e:
                self._json(500, {"error": str(e)})
        elif path == "/api/admin-data":
            self._json(200, admin_data())
        elif path == "/admin":
            # A redirect, not the page. Served at /admin the panel's relative "styles.css"
            # would resolve to /styles.css and the tab would render unstyled -- the trailing
            # slash is what makes serving its markup verbatim work at all.
            self.send_response(301)
            self.send_header("Location", "/admin/")
            self.send_header("Content-Length", "0")
            self.end_headers()
        elif path.startswith("/admin/"):
            self._admin(path[len("/admin/"):])
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
        thumb = row.thumb_path()
        if thumb is None:
            # Either the card has no art yet, or the cache was never built. Never fall back to
            # the source PNG: that is 1-2 MB per card, and silently doing it is how a tool that
            # looks fine locally ships half a gigabyte per page load in production.
            self._json(404, {"error": "no thumbnail (card may have no art; "
                                      "otherwise run tooling/cardweb/build_thumbs.py)"})
            return
        self._send(200, thumb.read_bytes(), "image/webp", cache="max-age=300")

    def do_POST(self):
        if not self._authed():
            self._challenge()
            return
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
        elif path == "/api/discard":
            try:
                self._json(200, discard_edit(body.get("token", "")))
            except KeyError as e:
                self._json(409, {"error": str(e.args[0])})
            except RuntimeError as e:
                self._json(400, {"error": str(e)})
        elif path == "/api/comments":
            try:
                # The author is the AUTHENTICATED user, never body["author"].
                self._json(200, add_comment(
                    body.get("token", ""), body.get("text", ""), self._auth_user()))
            except KeyError as e:
                self._json(409, {"error": str(e.args[0])})
            except (ValueError, RuntimeError) as e:
                self._json(400, {"error": str(e)})
        elif path in ("/api/comments/delete", "/api/comments/resolve"):
            op = "delete" if path.endswith("delete") else "resolve"
            try:
                self._json(200, mutate_comment(
                    body.get("token", ""), body.get("commentId", ""), op,
                    bool(body.get("resolved", True))))
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

    # The corpus is unreleased game content. Binding it to anything but loopback without a
    # password is the one mistake this tool must not let a deploy make quietly, so it is a
    # refusal here rather than a warning in a README nobody reads at 2am.
    if args.host not in ("127.0.0.1", "localhost", "::1") and not CREDENTIALS:
        print("REFUSING TO START: binding a non-loopback interface with no CARDWEB_PASSWORD or "
              "CARDWEB_USERS set would publish the unreleased card corpus. Set one, or bind "
              "127.0.0.1.", file=sys.stderr)
        return 2

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
    print(f"comments   {comments_store.comments_path()}")
    print(f"panel      {ADMIN_DIR}")
    # Names only. A banner that echoed a password would put it in every deploy log, where it
    # outlives the rotation that was supposed to retire it.
    print(f"auth       {', '.join(sorted(CREDENTIALS)) if CREDENTIALS else 'OPEN (no credential set)'}")
    print(f"serving    http://{args.host}:{args.port}")

    with Server((args.host, args.port), Handler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nstopped.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
