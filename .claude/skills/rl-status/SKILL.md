---
name: rl-status
description: Report RL bot training/capture progress — what is running, how far along, latest verdict, deployed bot, and what is next up (decided and possible). Use when Sam asks how training, a capture or "the runs" are going.
---

# RL status

Read-only. Answer in **14 lines or fewer**, phone-readable, in this order: running, latest
verdict, deployed bot, next up (decided), next up (possible). Quote numbers off the commands; never estimate one.

## 1. What is running

```powershell
Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='dotnet.exe' OR Name='bash.exe'" |
  ? { $_.CommandLine -match 'training_trainer|NetSoak|work[\\/]' } |
  select ProcessId, CreationDate, @{n='cmd';e={$_.CommandLine.Substring(0,[math]::Min(160,$_.CommandLine.Length))}}
```

Nothing matches → say "nothing running" and go to step 3. Two `dotnet.exe` alone are usually
build servers, not a run — only count one whose command line names NetSoak or a `work/` script.

## 2. How far along

Each experiment is `work/<experiment>/` with a `run_*.sh` driver and a `<name>.log`. **Logs are
gitignored, so they exist only in the main checkout** — from a worktree, `work/*/*.log` finds
nothing. Use the absolute path. Take the log(s) of the running experiment, or else the newest:

```bash
W=/c/Users/User/Documents/tcg-hearthlike/work
ls -t $W/*/*.log | head -3
date; grep -n -E '^\[' $W/<x>/<x>.log | cut -c1-200 | tail -n 15
```

Read off: the current stage (`[S1 train] start …` without its `exit` line), the last
`epoch N/M` line, and whether the log ends `DONE` / `READY`. **The log's last timestamp against
`date` is the liveness check** — a running process whose log has not moved in an hour is stuck.
Capture throughput prints `0.0/s` from rounding: compute games ÷ seconds from that same line.

## 3. Latest verdict

- Newest `work/*/result-*.md`: its headline result line.
- In a finished log: the `… against bar …: PASS|FAIL` and `score=… wilson95=[…]` lines.
- Newest `F:\EmberfallData\arenas\*\arena-report.md`: its `Result:` line.

## 4. Deployed bot

`tooling/data/bot-model/bot-model.json` names the shipped model. `https://emberfall.fly.dev/healthz`
(GET only) reports `bot.decisions` and `bot.fallbacks`; a fallback count climbing means the ONNX
model is not answering.

## 5. Next up — already decided

What has been planned but not run:

- A `work/*/preregistration-*.md` **newer than every `result-*.md` in its folder** is a run that is
  designed and not yet reported: queued, or the one running now.
- The newest result's forward-looking section — `## What this says to do next`, `## Open, for Sam`,
  `## Shipping the next champion` and the like (`grep -n '^## ' <result>`) — and the RL lines of
  `C:\Users\User\Atlas-Brain\handoff\Kingdom-handoff.md` that start `Next`.

Report each in one line with its source file. Do not report the brain page
`projects/kingdom/model-training`'s `## Open` section without checking its date; it has trailed the
work before.

## 6. Next up — possible

At most three candidates nobody has scheduled, each **labelled as a suggestion** and tied to a
number from steps 3–5: an `Open, for Sam` item nobody has picked up, a near-miss bar worth a
confirmation run, a weak deck the latest arena names. Never present a suggestion as a plan.

## Never

- `cat`, `head` or `Read` a `decisions.jsonl` / `episodes.jsonl` — they run to 6 GB.
- Start, stop or restart a run. This skill reports; Sam decides.
