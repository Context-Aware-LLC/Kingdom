# Walking Skeleton — deploy scaffold (condition C-G0-6)

**Status:** SCAFFOLD READY — awaiting provisioning (needs Sam: VPS + domain purchase, gate ruling R-3).
**Goal (C-G0-6):** smallest VPS + domain + Caddy TLS + a `wss://` echo, reachable from a Unity
client **off-network** — the thinnest end-to-end proof that the `decisions/ADR-0005` single-box
topology works, before any real serializer/protocol code hardens.

This folder is everything that does **not** require the purchase. The blocked steps are marked 🔒.

## What Sam needs to buy (the only blockers)
- 🔒 **VPS** — smallest tier (~$6/mo class). Hetzner CX22 vs. DigitalOcean $6 droplet — "decided by
  doing" per decision 7; either is inside the $25/mo ceiling. US-East.
- 🔒 **Domain** — ~$12/yr (needed for the `wss://` TLS cert; Caddy auto-provisions Let's Encrypt).
  Point an `A` record at the VPS IP. (This is gate ruling R-3 — a named purchase awaiting your stamp.)

## Turnkey steps once provisioned
1. `A` record: `emberfall.<yourdomain>` → VPS IP.
2. Copy this folder to the box; set `DOMAIN` + `EMAIL` in `.env` (see `.env.example`).
3. `docker compose up -d` — builds the echo, starts Caddy, TLS is automatic on first request.
4. From a machine **off** the VPS network, run `wscat -c wss://emberfall.<yourdomain>/ws` (or the
   Unity `WalkingSkeletonProbe`, TODO WS B1) and confirm the echo round-trips.
5. Demo at G1 (the gate wants this shown live).

## Files
- `Caddyfile` — auto-TLS reverse proxy, terminates `wss://`, forwards to the echo on `:5000`.
- `Dockerfile` — builds the minimal Kestrel WebSocket echo (`EchoServer/`).
- `docker-compose.yml` — Caddy + echo, one command.
- `.env.example` — `DOMAIN` / `EMAIL` placeholders.
- `EchoServer/` — ~40-line ASP.NET Core minimal WebSocket echo. **Deliberately standalone** — it is
  NOT added to `Emberfall.slnx` (keeps the pure-.NET test solution clean). It is throwaway proof;
  the real `Emberfall.Net.Server` (Kestrel host running the core) replaces it in WS B1.

> **Scope honesty:** this proves *transport + TLS + off-network reachability* only. It runs no game
> logic. That is the whole point of a walking skeleton — de-risk the ops path before the protocol
> exists. Do not grow this echo into the real server; start `Emberfall.Net.Server` fresh (ADR-0005).
