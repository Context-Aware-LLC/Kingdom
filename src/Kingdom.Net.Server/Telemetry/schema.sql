-- Kingdom match telemetry — durable store (C-G1-8; dev-docs/data/player-data-and-telemetry.md:17).
--
-- This file is the CANONICAL schema and is applied idempotently by TelemetryDrain on
-- startup (it ships as an EmbeddedResource, so the drain runs exactly this text). Edit
-- here; there is no second copy to keep in sync.
--
-- Durability boundary. The server writes fsync'd JSONL to the Fly volume as a crash-safe
-- BUFFER; TelemetryDrain ships each line into this Postgres store. This store — not the
-- volume — is the durability guarantee (decision 2026-07-22: telemetry durability = hosted
-- Supabase). A Fly volume is single-machine, single-region, and un-replicated, so at scale
-- telemetry would scatter across N volumes and losing one is indistinguishable from n=0.
--
-- No foreign keys between the child tables and `match`, deliberately. The JSONL does not
-- guarantee a match's card_seen lines land after its match_complete line, and an FK would
-- turn insert ORDER into a correctness condition on the drain. Analytics joins on match_id.
--
-- Idempotency is the natural keys + ON CONFLICT DO NOTHING. A re-read after a crash (the
-- byte-offset checkpoint lagging the committed rows) is therefore harmless. Correctness
-- lives in these keys, not in the checkpoint.
--
-- ACCOUNT DELETION names every table and raw_event line shape here by hand. A new table, or a new
-- line type carrying an account id or device hash, must be added to
-- docs/dev-docs/operations/account-deletion.md and the deletion tool in the same commit.

-- One row per finished-or-voided match. `match_complete` and `match_void` are mutually
-- exclusive per match, so they share this table and `status` discriminates.
create table if not exists match (
  match_id    text        primary key,
  env         text        not null,   -- fly | local  (which machine ground the row)
  source      text        not null,   -- ai_soak | ladder | bot_fill | friendly
  ended_at    timestamptz not null,
  status      text        not null,   -- complete | void
  end_reason  text,                   -- win|concede|timeout|disconnect|unknown ; null for void
  winner_seat text,                   -- p0|p1|draw|unknown ; null for void
  turn_count  int         not null
);

-- Two rows per match (the seats). Enriched for a completed match; for a void only
-- account_id + seat are known (a void is accounts-only, no per-seat outcome).
create table if not exists match_seat (
  match_id      text   not null,
  seat          text   not null,      -- p0 | p1
  account_id    text   not null,
  class_id      text,                 -- null for void
  deck_id       text,
  deck_hash     text,
  result        text,                 -- win | loss | draw ; null for void
  mulligan_kept text[],               -- card def ids kept through mulligan ; null for void
  primary key (match_id, seat)
);

-- The per-card sample (telemetry-spec §2): one row per (match, account, cardDefId).
-- env/source/ended_at are denormalized from the line — this is the highest-volume table
-- and is filtered by them constantly, so it stays self-sufficient without a join to match.
create table if not exists card_seen (
  match_id          text        not null,
  env               text        not null,
  source            text        not null,
  ended_at          timestamptz not null,
  account_id        text        not null,
  class_id          text        not null,
  card_def_id       text        not null,
  in_deck           boolean     not null,
  drawn_this_match  boolean     not null,
  match_won         boolean     not null,
  kept_in_mulligan  boolean     not null,
  first_played_turn int,               -- null = never played (feeds §2 avg-turn-played)
  primary key (match_id, account_id, card_def_id)
);

-- The descope/deferral lever. Every drained line, verbatim, hash-deduped. Lets any schema
-- change to the typed tables above be backfilled by REPROJECTION instead of forcing the
-- "perfect" shape up front — so schema decisions become deferrable and reversible.
-- Retention-managed: purge rows once the schema has settled past reprojection risk; the
-- typed tables are the permanent record, this is the recent safety net, and it is the first
-- thing to offload to cold storage if volume ever demands it.
create table if not exists raw_event (
  event_uid   text        primary key,  -- sha256 (hex) of the exact JSONL line bytes
  type        text        not null,     -- match_complete | match_void | card_seen | the funnel's rows
  ingested_at timestamptz not null default now(),
  line        jsonb       not null
);

-- Who played each seat (launch item 5): `human`, or a bot's policy identity such as `greedy-ai.v3`
-- or `checkpoint:<sha>:argmax`. Added after the table; null on rows drained before it existed.
alter table match_seat add column if not exists policy text;

create index if not exists card_seen_card_idx  on card_seen (card_def_id);
create index if not exists card_seen_time_idx   on card_seen (ended_at);
create index if not exists match_env_src_idx    on match (env, source);
create index if not exists match_seat_acct_idx  on match_seat (account_id);
create index if not exists raw_event_type_idx   on raw_event (type);

-- The first-session funnel's join keys (docs/dev-docs/specs/first-session-funnel.md). Its rows
-- live only in raw_event, and the view `first_session_funnel` looks them up per session by these
-- fields. The view itself is NOT in this file: it is first_session_funnel.sql, applied after this
-- one in its own try/catch, so a view that fails to create cannot stop the drain from starting.
create index if not exists raw_event_session_idx on raw_event ((line->>'session_id'));
create index if not exists raw_event_account_idx on raw_event ((line->>'account_id'));
create index if not exists raw_event_device_idx  on raw_event ((line->>'device'));
create index if not exists raw_event_match_idx   on raw_event ((line->>'match_id'));
