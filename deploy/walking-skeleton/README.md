# Walking Skeleton — deploy scaffold (C-G0-6 → B1)

**Status:** ✅ B1 — the scaffold fronts the REAL match server (`Kingdom.Net.Server`,
`hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/b1-match-server-design.md`). Live at `wss://emberfall.fly.dev/ws` on **fly.io**
(app `emberfall`, region `sjc`); Fly terminates TLS with its own `*.fly.dev` cert.

The custom hostname `emberfall.outofcontext.to` was **retired 2026-07-30** — it existed only to
keep already-distributed tester builds working across a host move, and that is no longer worth a
DNS record. It also pointed at Fly's **shared** IPv4, which made Cloudflare's scanner attribute a
neighbouring tenant's open port 3389 to this domain. The client talks to `emberfall.fly.dev`
directly and Cloudflare is out of this path entirely.

History: the original wss **echo** proved transport + TLS + off-network reachability on
2026-07-15 (condition C-G0-6, ADR-0005 single-box topology) and was deleted in B1 commit 2 as
designed — "do not grow the echo; start `Kingdom.Net.Server` fresh."

## Deploying an update

1. From the **repo root** (that is where `fly.toml` lives, and the image builds FROM THE REPO
   ROOT — RC F4, since `Dockerfile.server` needs `src/` + the card data): `fly deploy`.
2. Verify from off-network: `curl https://emberfall.fly.dev/healthz` →
   `{"status":"ok","sessions":0,"cap":64,...}`, then connect two WebSocket clients to
   `wss://emberfall.fly.dev/ws` and confirm both receive `match_start`.

No `.env`, no Caddy, no box to sync to — Fly builds from the repo and provisions TLS itself.
`flyctl` is at `C:\Users\User\.fly\bin\flyctl.exe` (the installer did not set PATH).

## Files
- `Caddyfile` — auto-TLS reverse proxy, terminates `wss://`, forwards to `emberfall-server:8080`
  (service name + port move together with `docker-compose.yml`).
- `Dockerfile.server` — multi-stage build of `Kingdom.Net.Server`; bakes in the canonical card
  data from `unity/Assets/CardData/`.
- `docker-compose.yml` — Caddy + server, one command. Keeps the `dns:` pin on the caddy service
  (systemd-resolved stub resolver breaks ACME inside the container netns — documented gotcha).
  Replay logs persist in the `replay_data` volume (`/data/replays`).
- `.env.example` — `DOMAIN` / `EMAIL` placeholders.

> **Scope honesty:** B1 = pairing-placeholder FIFO gate, prebuilt decks, JSONL replay logs.
> Guest identity, the real queue, and Postgres persistence are WS B2; in-flight matches are
> lost on server restart
