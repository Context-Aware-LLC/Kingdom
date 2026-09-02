#!/usr/bin/env python3
"""Run the retirement checks declared by memory pages and report which are dead.

WHY THIS EXISTS. CLAUDE.md's doctrine -- "a statement about the current state of
the world is a claim with an expiry date, and the expiry has to be attached to the
work that causes it" -- works in the repo because the trigger sits on the line an
implementer is forced to edit. Memory pages have no such line. A page saying "X is
broken" outlives the fix, and because a stale page asserts a dead fact with exactly
the same authority as a live one, the cost is not clutter, it is being CONFIDENTLY
WRONG. Sam, 2026-09-01: volume does not slow a search, it corrupts the answer.

cap-memory-index.py already forces a trade at write time -- "adding a line means
retiring one" -- but it cannot say WHICH line, so a session with no evidence
retires the wrong one. This supplies the evidence.

WHAT A PAGE DECLARES. Two optional flat keys in the frontmatter:

    retire_when:  <prose> -- the condition under which this page stops being true
    retire_check: <shell command> -- EXIT 0 MEANS THE PAGE IS DEAD

`retire_check` is the whole point. Prose rots silently; a command rots loudly,
because a check that no longer runs is reported BROKEN rather than passing quietly.
Where no command can settle it (an open ruling, a decision that is Sam's to make)
declare `retire_when` alone -- it lands in MANUAL and waits for a human.

WHICH PAGES GET ONE. Only pages that record a DEFECT OR A TRANSIENT STATE. The
router split MEMORY.md into two sections that already sort this way: everything
under "Live -- unfinished" is a broken/open claim that a fix falsifies, and
everything under "How Sam works" is a ruling that never expires. Rulings must NOT
carry a check -- a page recording what turned out to be TRUE is not a retirement
candidate, and treating "description mentions FIXED" as a verdict deletes exactly
the pages whose resolution is the live fact.

THE CACHE. Checks shell out and some touch the network (`git ls-remote`), so the
PreToolUse hook must never run them inline. This writes .agent/memory-retire.json
and cap-memory-index.py reads that, reporting what was dead as of the last sweep.

USAGE
    python .claude/hooks/memory_retire.py            # sweep, print, write cache
    python .claude/hooks/memory_retire.py --quiet    # cache only, no output

RETIREMENT TRIGGER: if memory pages stop carrying `retire_check:` -- the convention
abandoned, or the store itself retired -- delete this file, drop the DEAD block from
cap-memory-index.py, and drop the convention from the memory doctrine in MEMORY.md.
"""
import json
import os
import re
import shutil
import subprocess
import sys

MEMORY_DIR = os.path.join(
    os.path.expanduser("~"),
    ".claude", "projects", "c--Users-User-Documents-tcg-hearthlike", "memory",
)
REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(REPO_ROOT, ".agent", "memory-retire.json")

# A check is a shell one-liner over a repo the size of Emberfall; ls-remote against
# a cold origin is the slow case. Anything past this is a broken check, not a slow one.
TIMEOUT_SECONDS = 45

FRONTMATTER = re.compile(r"\A---\r?\n(.*?)\r?\n---\r?\n", re.S)


def frontmatter(text):
    m = FRONTMATTER.match(text)
    return m.group(1) if m else ""


def flat_key(block, key):
    """Read a top-level `key: value` out of the frontmatter.

    Deliberately not a YAML parse: the store has no yaml dependency, the pages are
    hand-written, and a parser that throws on one malformed page would take the
    whole sweep down with it.
    """
    m = re.search(r"^%s:[ \t]*(.+?)[ \t]*$" % re.escape(key), block, re.M)
    if not m:
        return None
    v = m.group(1).strip()
    if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
        q = v[0]
        # YAML escapes a quote inside a quoted scalar by doubling it. Undo that, or
        # every apostrophe in a check comes back doubled and a `grep "it''s"` misses.
        v = v[1:-1].replace(q * 2, q)
    return v or None


def pages():
    for name in sorted(os.listdir(MEMORY_DIR)):
        if not name.endswith(".md"):
            continue
        path = os.path.join(MEMORY_DIR, name)
        try:
            with open(path, encoding="utf-8") as fh:
                block = frontmatter(fh.read())
        except OSError:
            continue
        when = flat_key(block, "retire_when")
        check = flat_key(block, "retire_check")
        if when or check:
            yield name, when, check


def _shell():
    """Checks are POSIX one-liners, and `shell=True` on Windows is cmd.exe.

    Left to itself cmd reports every check as BROKEN with "operable program or batch
    file" -- which looks exactly like a check whose target moved. Git for Windows is
    a hard prerequisite of this repo, so bash is present; resolve it explicitly.
    """
    bash = shutil.which("bash")
    return ([bash, "-c"], False) if bash else (None, True)


def run(check):
    """(verdict, detail). Exit 0 means the page is DEAD."""
    prefix, use_shell = _shell()
    cmd = check if use_shell else prefix + [check]
    try:
        p = subprocess.run(
            cmd, shell=use_shell, cwd=REPO_ROOT, timeout=TIMEOUT_SECONDS,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
    except subprocess.TimeoutExpired:
        return "BROKEN", "timed out after %ds" % TIMEOUT_SECONDS
    except Exception as exc:
        return "BROKEN", str(exc)
    if p.returncode == 0:
        return "DEAD", ""
    if p.returncode == 1:
        return "LIVE", ""
    # 2+ is the shell failing to run the check at all -- a moved path, a renamed
    # branch, a tool that is gone. Loud on purpose: a check that cannot run is
    # NOT evidence that the page is still true.
    out = (p.stdout or b"").decode("utf-8", "replace").strip().splitlines()
    return "BROKEN", "exit %d: %s" % (p.returncode, out[-1] if out else "no output")


def main():
    quiet = "--quiet" in sys.argv
    results = {"DEAD": [], "LIVE": [], "BROKEN": [], "MANUAL": []}

    for name, when, check in pages():
        if not check:
            results["MANUAL"].append((name, when or "", ""))
            continue
        verdict, detail = run(check)
        results[verdict].append((name, when or "", detail))

    try:
        os.makedirs(os.path.dirname(CACHE), exist_ok=True)
        with open(CACHE, "w", encoding="utf-8") as fh:
            json.dump({k: [r[0] for r in v] for k, v in results.items()}, fh, indent=1)
    except OSError:
        pass

    if quiet:
        return 0

    total = sum(len(v) for v in results.values())
    print("memory retirement sweep -- %d of %d pages declare a trigger\n"
          % (total, len([f for f in os.listdir(MEMORY_DIR) if f.endswith('.md')])))
    for bucket, gloss in (
        ("DEAD", "the condition is satisfied -- RETIRE THESE"),
        ("BROKEN", "the check itself no longer runs -- fix or retire the check"),
        ("MANUAL", "no command can settle it -- needs a human"),
        ("LIVE", "still true"),
    ):
        rows = results[bucket]
        print("%-7s %2d  %s" % (bucket, len(rows), gloss))
        for name, when, detail in rows:
            print("        %s" % name)
            if when:
                print("          when: %s" % when)
            if detail:
                print("          note: %s" % detail)
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
