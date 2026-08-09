-- Emberfall accounts + entitlement ledger — durable store (EF-072; LAUNCH-WORKPLAN Track S).
--
-- This file is the CANONICAL schema and is applied idempotently on startup by the Postgres stores
-- (it ships as an EmbeddedResource, so they run EXACTLY this text). Edit here; there is no second
-- copy to drift. Modelled on Telemetry/schema.sql — same posture, same reasons.
--
-- WHY THIS SHAPE (the decision-decoupler). The ledger is keyed on player_id, not on a store. A phone
-- purchase is therefore visible to a desktop build by construction, and the eventual vendor decision
-- binds an implementation, not a rewrite. The two interfaces (IAccountStore, IEntitlementLedger) have
-- an in-memory implementation and this Postgres one, and both pass the same contract suite.
--
-- APPEND-ONLY. The grant table is never updated and never deleted. A correction is a NEW line: a
-- refund is a negative grant with its own external_txn_id. So the full history is always
-- reconstructable and a balance is a PROJECTION (the signed sum of qty), never a stored,
-- overwritable number that could drift from the events that produced it.
--
-- IDEMPOTENCY is the natural key + ON CONFLICT DO NOTHING, exactly as the telemetry drain does it.
-- external_txn_id is the primary key, so a replayed receipt conflicts and writes nothing — the
-- balance moves exactly once no matter how many times the receipt is delivered. Correctness lives in
-- this key, not in application logic: even two servers racing the same receipt insert once between
-- them.
--
-- No foreign key from entitlement_grant to account, deliberately (same reasoning as telemetry): a
-- grant may be recorded before the account row is observed, and an FK would turn insert ORDER into a
-- correctness condition. Both are keyed on player_id and analytics joins on it.

-- One row per player. The stable identity the ledger is keyed on. credential_hash is the SHA-256 of
-- the bearer credential (a device token today; an OAuth subject later) — never the raw credential, so
-- a leaked row cannot be replayed as somebody's identity. UNIQUE on the credential makes get-or-create
-- idempotent: the same credential always resolves to the same player_id.
create table if not exists account (
  player_id        text        primary key,
  credential_kind  text        not null,   -- device_token | google | apple | email | ...
  credential_hash  text        not null,   -- sha256 hex of the bearer credential
  created_at       timestamptz not null,
  unique (credential_kind, credential_hash)
);

-- EF-074c: email is the player's recovery path (VULN-003 §4.2). Added after the initial
-- schema landed; idempotent on existing tables. Null on accounts created before this column.
alter table account add column if not exists email text;

-- Account standing — 'standard' | 'superuser'. Superuser bypasses L6 card-ownership legality (see
-- AllCardsOwnedCounts) for bot/QA identities. Never set through ResolveOrCreateAsync's client-facing
-- credential path — only through SetKindAsync, an operator-side call. Defaults every existing and
-- new row to 'standard', same idempotent-alter posture as email above.
alter table account add column if not exists kind text not null default 'standard';

-- The append-only grant log. external_txn_id is the idempotency anchor (primary key ⇒ UNIQUE); qty is
-- signed so a refund is simply a negative grant carrying its OWN transaction id (a refund's id differs
-- from the purchase's, so it does not conflict with it and nets the balance correctly).
create table if not exists entitlement_grant (
  external_txn_id  text        primary key,  -- globally-unique; a replayed receipt conflicts here
  player_id        text        not null,
  item             text        not null,     -- pack_core | gold | dust | card_<id> | ...
  qty              bigint      not null,      -- signed: > 0 grant, < 0 refund/clawback
  source           text        not null,      -- play_billing | apple | web | promo | signup | refund | ...
  created_at       timestamptz not null
);

-- The balance projection reads by (player_id, item); the audit/history view reads by player_id.
create index if not exists entitlement_grant_player_item_idx on entitlement_grant (player_id, item);
