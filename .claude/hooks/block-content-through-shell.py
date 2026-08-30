#!/usr/bin/env python3
"""PreToolUse(Bash) guard: stop content being routed through the shell.

WHY THIS IS A HOOK AND NOT A SKILL. The failure it prevents is a reflex, not a
decision -- a heredoc gets typed mid-flow, and by the time anyone would think to
consult guidance the command is already written. A hook fires at the moment of
invocation, and costs nothing on every command it does not stop. A skill's
description, by contrast, is carried in every session whether or not it is
relevant; measured at ~120 tokens x ~19 sessions/day, that carry outweighed the
bug it was meant to prevent by roughly 4x. (Sam, 2026-08-30.)

DELIBERATELY NARROW. An earlier draft tried to predict which heredocs would break
by scanning the body for apostrophes and quotes. That is not knowable from here:
the same session had heredocs containing apostrophes succeed and others fail, and
the real trigger is how many parsing layers a command crosses before bash sees it.
A guard that guesses produces friction on commands that work, which costs more
than the bug.

So this blocks only shapes where a strictly better alternative always exists, and
stays silent otherwise. A short `python - <<PY` for a scratch computation is a
good use of the shell and passes untouched.

EXIT CODES. 2 blocks the tool and shows stderr to the model; anything else lets it
through. Every unexpected condition here exits 0 -- a guard that fails closed
would block all shell use on a malformed payload, which is far worse than the
thing it guards against.

RETIREMENT TRIGGER: if `.claude/settings.json` stops registering a PreToolUse hook
on Bash, delete this file with it.
"""
import json
import re
import sys

# Heredoc opener: `<<EOF`, `<<-EOF`, `<<'EOF'`, `<<"EOF"`.
HEREDOC = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")

# An inline script long enough that it wants to be a file: editable, re-runnable,
# and diffable when it needs a second pass.
INLINE_SCRIPT_MAX_LINES = 50


def heredoc_bodies(cmd):
    """Yield (delimiter, body) for each heredoc in the command."""
    for m in HEREDOC.finditer(cmd):
        delim = m.group(2)
        rest = cmd[m.end():]
        end = re.search(r"^\s*%s\s*$" % re.escape(delim), rest, re.M)
        yield delim, (rest[:end.start()] if end else rest)


def verdict(cmd):
    """Return a block message, or None to allow."""

    # Writing a file through the shell. Write does this without a parser in the way.
    if re.search(r"\bcat\s*>>?\s*[^\s|&;]+\s*<<", cmd):
        return ("Writing a file through a shell heredoc.\n"
                "Use the Write tool instead -- it does no shell parsing at all.")

    # Prose smuggled into a flag via command substitution.
    m = re.search(r"(--body|--message|--notes|-m|-d)\s+[\"']?\$\(\s*cat\b", cmd)
    if m:
        flag = m.group(1)
        better = {"-m": "git commit -F <path>",
                  "--message": "a file flag",
                  "--body": "--body-file <path>",
                  "--notes": "--notes-file <path>",
                  "-d": "--input <path> / -d @<path>"}.get(flag, "a file flag")
        return ("`%s \"$(cat ...)\"` routes your content through command substitution.\n"
                "Write the content to a file and pass it: %s" % (flag, better))

    # A long inline script. Short ones are fine and stay fine.
    for delim, body in heredoc_bodies(cmd):
        n = body.count("\n")
        if n > INLINE_SCRIPT_MAX_LINES:
            return ("A %d-line script inline in a heredoc (<<%s).\n"
                    "Write it to a .py/.sh file and run that -- it survives a second "
                    "pass, and the content never crosses a shell parser." % (n, delim))

    return None


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return 0

    if payload.get("tool_name") != "Bash":
        return 0

    cmd = (payload.get("tool_input") or {}).get("command")
    if not isinstance(cmd, str) or not cmd:
        return 0

    try:
        msg = verdict(cmd)
    except Exception:
        return 0

    if msg:
        sys.stderr.write("[content-through-shell] %s\n" % msg)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
