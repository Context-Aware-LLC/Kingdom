#!/usr/bin/env python3
"""SessionStart: surface memory pages whose retirement condition has been met.

WHY THIS REPLACED A WRITE-TIME CAP. The first version of this idea was a
PreToolUse(Write|Edit) hook that capped MEMORY.md at 10000 bytes and, when it
blocked you, named the pages a previous sweep had found dead. Sam killed it on
2026-09-01 and he was right twice over:

  1. NOTHING RAN THE SWEEP. The cap hook only READ a cache. If no one ran
     memory_retire.py by hand, the cache went stale and the hook reported "No
     page is currently known-dead" -- the exact false comfort its own text warned
     against. A rule with no scheduler.

  2. THE TRIGGER POINT WAS WRONG. It fired when the index was FULL. But a page
     goes stale the day its bug is fixed, which is usually a day nobody is adding
     a memory at all. The 2026-09-01 sweep found five stale lines out of 22 --
     23% of what every session carried was false -- with the index nowhere near
     the cap. Staleness and size are not the same problem and the cap only ever
     addressed the second.

So: report at the START of a session, when there is still a whole session in which
to act, rather than at the one moment someone is mid-write and least willing to
stop. Size is no longer enforced at all; the sweep plus the doctrine in MEMORY.md
carry it, and an oversized index is a cheaper failure than a confidently wrong one.

WHAT IT COSTS. Zero tokens on a clean sweep -- it prints NOTHING unless a page is
actually dead, because SessionStart stdout is injected into the context window and
a hook that says "all clear" every day is a tax for no information. The refresh is
spawned DETACHED and never waited on: the checks shell out, some hit the network
(gh, git ls-remote), and a SessionStart hook that blocks on those delays every
session by ~15s.

REPORTS THE CACHE, REFRESHES FOR NEXT TIME. What you see is the LAST sweep's
result, which is at most a day old by construction. Reporting a fresh sweep would
mean waiting for it, which is the thing we refuse to do.

RETIREMENT TRIGGER: if memory pages stop carrying `retire_check:`, or the project
memory store moves out from under memory_retire.py, delete this file and drop its
SessionStart registration from .claude/settings.json.
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SWEEP = os.path.join(HERE, "memory_retire.py")
CACHE = os.path.join(REPO, ".agent", "memory-retire.json")

# One sweep a day. A page's retirement condition is satisfied by work that lands
# over hours, not seconds, so a fresher cache buys nothing and costs a subprocess
# on every resume.
MAX_AGE_SECONDS = 24 * 60 * 60


def stale():
    try:
        return (time.time() - os.path.getmtime(CACHE)) > MAX_AGE_SECONDS
    except OSError:
        return True  # missing cache: never swept, or someone cleaned .agent/


def refresh():
    """Spawn the sweep and return immediately. Never raises."""
    if not os.path.exists(SWEEP):
        return
    kwargs = {"stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL,
              "stdin": subprocess.DEVNULL, "cwd": REPO}
    if os.name == "nt":
        # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP -- outlives this hook's
        # 10s timeout, and no console window flashes over whatever Sam is doing.
        kwargs["creationflags"] = 0x00000008 | 0x00000200
    else:
        kwargs["start_new_session"] = True
    try:
        subprocess.Popen([sys.executable, SWEEP, "--quiet"], **kwargs)
    except Exception:
        pass


def dead():
    try:
        with open(CACHE, encoding="utf-8") as fh:
            return [p for p in json.load(fh).get("DEAD", []) if isinstance(p, str)]
    except Exception:
        return []


def main():
    try:
        sys.stdin.read()
    except Exception:
        pass

    pages = dead()
    if pages:
        # Only ever printed when there is something to do. The instruction matters
        # as much as the list: a firing check means "stop paying for this HERE",
        # and the three outcomes are in MEMORY.md's own doctrine section.
        print("=== MEMORY: %d page(s) met their retirement condition ===" % len(pages))
        for p in pages:
            print("  " + p)
        print("These declared `retire_check:` and it now passes. Retire, de-index or")
        print("correct each one -- see 'Keeping this file small' in MEMORY.md -- and")
        print("DISCHARGE the trigger once acted on, or it reports DEAD forever.")
        print("Re-sweep: python .claude/hooks/memory_retire.py")

    if stale():
        refresh()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        sys.exit(0)  # a bookkeeping hook must never break a session start
