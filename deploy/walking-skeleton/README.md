# Walking Skeleton — deploy scaffold (C-G0-6 → B1)

**Status:** ✅ B1 — the scaffold now fronts the REAL match server (`Emberfall.Net.Server`,
`engineering/b1-match-server-design.md`). Live at `wss://emberfall.outofcontext.to/ws` on the
DigitalOcean droplet (root@24.144.123.177), real Let's Encrypt cert.

History: the original wss **echo** proved transport + TLS + off-network reachability on
2026-07-15 (condition C-G0-6, ADR-0005 single-box topology) and was deleted in B1 commit 2 as
designed — "do not grow the echo; start `Emberfall.Net.Server` fresh."

## Deploying an update
1. Sync the build inputs to the box (the image builds FROM THE REPO ROOT — RC F4):
   `src/`, `unity/Emberfall/Assets/CardData/`, and `deploy/walking-skeleton/`.
2. On the box, from `deploy/walking-skeleton/`: `docker compose up -d --build`.
3. Verify from off-network: `curl https://emberfall.<domain>/healthz` →
   `{"status":"ok","sessions":0,"cap":64,...}`, then connect two WebSocket clients to
   `wss://emberfall.<domain>/ws` and confirm both receive `match_start`.

`DOMAIN` + `EMAIL` live in `.env` (see `.env.example`); Caddy auto-provisions TLS.

## Files
- `Caddyfile` — auto-TLS reverse proxy, terminates `wss://`, forwards to `emberfall-server:8080`
  (service name + port move together with `docker-compose.yml`).
- `Dockerfile.server` — multi-stage build of `Emberfall.Net.Server`; bakes in the canonical card
  data from `unity/Emberfall/Assets/CardData/`.
- `docker-compose.yml` — Caddy + server, one command. Keeps the `dns:` pin on the caddy service
  (systemd-resolved stub resolver breaks ACME inside the container netns — documented gotcha).
  Replay logs persist in the `replay_data` volume (`/data/replays`).
- `.env.example` — `DOMAIN` / `EMAIL` placeholders.

> **Scope honesty:** B1 = pairing-placeholder FIFO gate, prebuilt decks, JSONL replay logs.
> Guest identity, the real queue, and Postgres persistence are WS B2; in-flight matches are
> lost on server restart until then (design §1).
