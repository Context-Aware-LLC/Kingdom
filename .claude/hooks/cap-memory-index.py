#!/usr/bin/env python3
"""PreToolUse(Write|Edit) guard: keep the auto-loaded memory index small.

WHY A HOOK AND NOT A RULE. The index is loaded into EVERY session that touches
this repo, so its size is a tax paid ~19 times a day forever. It reached 21KB on
2026-09-01 indexing 382 files, of which about ten matter to any given task. It had
been compacted the day before and grew back within a day -- because the growth is
linear (every session adds a line) and compaction is a constant. Mowing a lawn
does not stop grass.

The rule that fixes it -- "adding a line means retiring one" -- has to fire at the
moment someone is writing the line, which is the only moment they are in a
position to decide which line goes. Written in a doc it gets read once, by
whoever is not adding a line that day. Same reasoning as
block-content-through-shell.py, and the same price: a hook costs 0 tokens per
session, a skill costs ~120 (Sam, 2026-08-30).

WHAT IT DOES NOT CAP. Only MEMORY.md, which is the auto-loaded router. The
trigger-scoped indexes it points at (index-layout, index-unity, index-cards) are
deliberately uncapped -- a line in one of those costs nothing until somebody
matches its trigger, so "move it behind a trigger" must stay strictly easier than
"delete it". Capping those too would push knowledge out of the store entirely,
which is the failure this is meant to prevent, not cause.

EXIT CODES. 2 blocks the tool and shows stderr to the model; anything else lets it
through. Every unexpected condition exits 0 -- a guard that fails closed would
block writes to an unrelated file on a malformed payload, and losing a memory is
worse than an oversized index.

RETIREMENT TRIGGER: if MEMORY.md stops being auto-loaded, or the router split is
abandoned and it goes back to being one flat index, delete this file and drop its
registration from .claude/settings.json. Its whole justification is the per-session
carry; with no carry there is nothing to protect.
"""
import json
import os
import sys

# The auto-loaded router. Matched on basename + parent dir so it fires wherever
# the projects directory lives, and never on some other MEMORY.md in a repo.
INDEX_NAME = "MEMORY.md"
INDEX_PARENT = "memory"

# Headroom over the 7.1KB the router was cut to on 2026-09-01 -- roughly 40%, so
# ordinary additions land without friction and a drift back toward the old 21KB
# does not. Raising this is a decision, not a fix: every byte is carried by every
# session. Prefer moving the line behind a trigger.
CAP_BYTES = 10000


def targets_index(path):
    if not isinstance(path, str) or not path:
        return False
    norm = path.replace("\\", "/")
    parts = norm.rsplit("/", 2)
    if len(parts) < 2:
        return False
    return parts[-1] == INDEX_NAME and parts[-2] == INDEX_PARENT


def resulting_size(tool, tool_input, path):
    """Bytes MEMORY.md would hold after this call, or None if not knowable."""
    if tool == "Write":
        content = tool_input.get("content")
        if not isinstance(content, str):
            return None
        return len(content.encode("utf-8"))

    # Edit: current size, minus what goes, plus what arrives. replace_all is not
    # modelled -- it can only shrink or grow by a multiple, and a guard that
    # guesses wrong on it would block a legitimate rewrite.
    old = tool_input.get("old_string")
    new = tool_input.get("new_string")
    if not isinstance(old, str) or not isinstance(new, str):
        return None
    if tool_input.get("replace_all"):
        return None
    try:
        current = os.path.getsize(path)
    except OSError:
        return None
    return current - len(old.encode("utf-8")) + len(new.encode("utf-8"))


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    tool = payload.get("tool_name")
    if tool not in ("Write", "Edit"):
        return 0

    tool_input = payload.get("tool_input") or {}
    path = tool_input.get("file_path")
    if not targets_index(path):
        return 0

    try:
        size = resulting_size(tool, tool_input, path)
    except Exception:
        return 0

    if size is None or size <= CAP_BYTES:
        return 0

    sys.stderr.write(
        "[memory-index] This write leaves MEMORY.md at %d bytes, over its %d cap.\n"
        "It is auto-loaded into EVERY session, so every byte is paid for ~19x a day.\n"
        "Do ONE of these, then write again:\n"
        "  1. Retire a line. A memory that says \"X was broken, now FIXED\" is a record,\n"
        "     and records live in git -- delete it once nobody holds the old belief.\n"
        "     That is the single biggest source of regrowth.\n"
        "  2. Move the line into index-layout.md, index-unity.md or index-cards.md.\n"
        "     Those are uncapped and cost nothing until somebody matches their trigger.\n"
        "Raising the cap is a decision for Sam, not a way past this message.\n"
        % (size, CAP_BYTES)
    )
    return 2


if __name__ == "__main__":
    sys.exit(main())
