---
name: content-via-files
description: "Use BEFORE running any shell command whose argument is multi-line or is prose you wrote — a commit message, an issue or PR body, a Python/Node script, file content, a JSON payload. Covers heredocs (`<<'EOF'`, `python - <<'PY'`), `cat >`, `-m \"$(cat ...)\"`, `--body \"...\"`, inline `-d '{...}'`, and PowerShell here-strings. Reach for it the moment you catch yourself thinking about how to quote something, or when a heredoc has just died on an apostrophe."
---

# Content goes in a file, never in the command

**The rule:** if what you are handing a shell command is more than one line, or is prose
you wrote, author it with the Write tool and pass the path. Keep the command line short
and free of your content.

## Why the obvious workaround keeps failing

A single-quoted heredoc (`<<'EOF'`) is literal — *in bash*. That is exactly why it looks
safe and breaks anyway.

A command string here is parsed more than once before bash sees it: the tool layer, the
shell the harness launches, then bash itself. You cannot see how many layers there are
from where you sit, and you find out the same way every time — the parse dies on the
apostrophe in *"the panel's edge"*, and the error points at a line number that explains
nothing.

So do not reason about quoting. Reasoning about quoting is the bug. Move the content out
of the command and the question stops existing.

This bites hardest on exactly the content most worth writing well: prose with
apostrophes, em dashes, curly quotes, backticks around `identifiers`, `$` in a shell
example, and fenced code blocks inside a Markdown issue body.

## The pattern

1. **Write** the content to a file — the Write tool does no shell parsing at all.
2. **Run** the command with the path.
3. The content never touches a parser that could reinterpret it.

## The map

| Content | Do this | Not this |
| --- | --- | --- |
| Commit message | `git commit -F msg.txt` | `-m "$(cat <<'EOF' …)"` |
| Issue body | `gh issue create --body-file b.md` | `--body "…"` |
| PR body | `gh pr create --body-file b.md` | `--body "…"` |
| Python / Node script | Write `x.py`, then `python x.py` | `python - <<'PY'` |
| File contents | Write the file directly | `cat > f <<'EOF'` |
| JSON payload | Write `p.json`, then `--input p.json` | inline `-d '{…}'` |

Many CLIs take `-` to mean stdin and also offer a file flag. Prefer the file flag; it
survives being copied into a different shell later.

## The trap inside the workaround

When a heredoc breaks, the reflex is to Write a **generator** — a script whose job is to
emit the content — and then run it. That is one layer too many. You already reached for
Write. Write the content itself.

Write a generator only when the content is **derived** rather than authored: 39 issue
bodies expanded from one template, a table computed from a sweep, anything where a loop
produces the text. The test is cheap:

> Would you have to type this out by hand anyway? Write it directly.
> Does a loop produce it? Write the loop.

Both are correct in their place, and confusing them costs a round trip in each direction.

## Windows specifics in this repo

- **Never round-trip a BOM-less UTF-8 file through PS 5.1 text cmdlets.** `Get-Content`
  reads ANSI without a BOM and `Out-File` writes UTF-8, so one read-then-write turns
  every em dash into mojibake, permanently. Use Write/Edit, or Python with an explicit
  `encoding='utf-8'`.
- **Commit messages always take `-F <path>`.** A here-string mangles quotes and a piped
  `Get-Content` injects a BOM into the subject line.
- When a script writes a file, pin both ends:
  `io.open(path, "w", encoding="utf-8", newline="\n")`.
- PowerShell here-strings (`@'…'@`) are not available in the Bash tool, and the closing
  `'@` must be at column 0 in PowerShell — another reason not to go this way at all.

## When a heredoc is still fine

Short content you are composing on the spot that contains no apostrophe, double quote,
backtick, or `$` — a three-line config block, a single SQL statement.

The boundary is not "how likely is this to break". It is: **if you would have to stop and
check, you have already spent more than writing the file would have cost.** That checking
cost is the whole argument. Prose always fails the test, because prose always has an
apostrophe in it eventually.
