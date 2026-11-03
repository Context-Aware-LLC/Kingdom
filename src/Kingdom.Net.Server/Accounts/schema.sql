-- Kingdom accounts + entitlement ledger — durable store (EF-072; LAUNCH-WORKPLAN Track S).
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
--
-- ACCOUNT DELETION names every table here by hand, because nothing cascades. A new table or column
-- that holds a player_id, email or anything else tied to a person must be added to
-- docs/dev-docs/operations/account-deletion.md and the deletion tool in the same commit.

-- One row per player. The stable identity the ledger is keyed on. credential_hash is the SHA-256 of
-- the bearer credential (a device token today; an OAuth subject later) — never the raw credential, so
-- a leaked row cannot be replayed as somebody's identity. UNIQUE on the credential makes get-or-create
-- idempotent: the same credential always resolves to the same player_id.
create table if not exists account (
  player_id        text        primary key,
  credential_kind  text        not null,   -- supabase (a player) | bot (a roster bot)
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

-- Has this player ever completed onboarding? (Sam, 2026-09-01.) ONE-WAY: false → true, never back.
--
-- It exists to kill an ambiguity the client cannot otherwise resolve. A returning player's roster is
-- read from the server, and an empty answer means either "brand new, send them to character
-- creation" or "the read failed" — indistinguishable, and guessing wrong walks a player with an
-- existing collection through creation a second time. With this flag the client has three states
-- instead of two, and only one of them creates anything.
--
-- **The flag only works if ABSENT cannot decode to false.** A missing field defaulting to false
-- reproduces the exact bug it was added to remove, because a failed read then reads as a new player.
-- Whatever carries this to the client keeps "no answer" as its own value — the same rule
-- CollectionSnapshot.ShouldReplaceCache enforces for ownership, and for the same reason.
--
-- Server-side deliberately: a client-local "seen it" bit dies with the app, so a reinstall would
-- replay onboarding for a player who already owns cards.
alter table account add column if not exists onboarded boolean not null default false;

-- When this player last logged in (Sam, 2026-10-02): stamped by GET /session, the handshake every
-- client boot blocks on, from the server's clock. NULL means never recorded — every row that
-- predates the column, and every bot, which never calls /session. Deliberately not backfilled from
-- created_at: that would report a login nobody made. Overwritten in place, so this is the LAST
-- login only; a history of logins would be its own append-only table.
alter table account add column if not exists last_seen timestamptz;

-- The append-only grant log. external_txn_id is the idempotency anchor (primary key ⇒ UNIQUE); qty is
-- signed so a refund is simply a negative grant carrying its OWN transaction id (a refund's id differs
-- from the purchase's, so it does not conflict with it and nets the balance correctly).
create table if not exists entitlement_grant (
  external_txn_id  text        primary key,  -- globally-unique; a replayed receipt conflicts here
  player_id        text        not null,
  -- Item ids are OWNED BY THE CODE, not by this comment; the constants are canonical and this line
  -- has drifted from them once already (it said card_<id> while the code wrote card:). Shapes today:
  --   card:<numeric card id>        BoosterPackGrantService.CardItemPrefix
  --   pack.<expansion>              BoosterPack.Id — retail, saleable
  --   pack.<expansion>.starter      BoosterPack.Id with StarterSku — granted at signup, never sold
  --   pack_opened:<pack id>         completion marker; pack_seed:<pack id> the replayable roll seed
  --   gold | gems | shards          EconomyItems
  item             text        not null,
  qty              bigint      not null,      -- signed: > 0 grant, < 0 refund/clawback
  source           text        not null,      -- play_billing | apple | web | promo | signup | refund | ...
  created_at       timestamptz not null
);

-- The balance projection reads by (player_id, item); the audit/history view reads by player_id.
create index if not exists entitlement_grant_player_item_idx on entitlement_grant (player_id, item);

-- The ranked ladder projection. Ratings are server-authored from completed human-vs-human matches;
-- clients can read the public /leaderboard endpoint but never write these tables directly.
create table if not exists ladder_player (
  player_id     text        primary key,
  display_name  text        not null,
  rating        integer     not null,
  games_played  bigint      not null default 0,
  wins          bigint      not null default 0,
  losses        bigint      not null default 0,
  draws         bigint      not null default 0,
  updated_at    timestamptz not null,
  constraint ladder_player_counts_nonnegative check (
    games_played >= 0 and wins >= 0 and losses >= 0 and draws >= 0
    and games_played = wins + losses + draws)
);

-- One immutable audit row per ranked match. match_id is the idempotency anchor: a retry or a second
-- server applying the same replay conflicts before either player's standing can move twice.
create table if not exists ladder_match (
  match_id          text        primary key,
  ended_at          timestamptz not null,
  p0_player_id      text        not null,
  p1_player_id      text        not null,
  outcome           text        not null,
  p0_rating_before  integer,
  p0_rating_after   integer,
  p1_rating_before  integer,
  p1_rating_after   integer,
  constraint ladder_match_distinct_players check (p0_player_id <> p1_player_id),
  constraint ladder_match_outcome check (outcome in ('p0_win', 'p1_win', 'draw'))
);

-- Serves the top-N endpoint as an index-only scan once the visibility map is warm.
create index if not exists ladder_player_rank_idx
  on ladder_player (rating desc, wins desc, games_played, player_id)
  include (display_name, losses, draws);

create index if not exists ladder_match_p0_idx on ladder_match (p0_player_id, ended_at desc);
create index if not exists ladder_match_p1_idx on ladder_match (p1_player_id, ended_at desc);

-- These public-schema tables are server-private. Supabase's Data API gets no policies and therefore
-- no rows; the game reads the deliberately narrow Kestrel projection instead.
alter table ladder_player enable row level security;
alter table ladder_match enable row level security;

-- A player's decks. body is the deck as canonical JSON (CollectionJson.WriteDecks' element shape),
-- NOT a row per card.
--
-- WHY A BLOB. Nothing server-side ever queries inside a decklist: balance is measured in house by
-- ML bot self-play, not by mining player decks (Sam, 2026-09-01). Rows would be structure with no
-- consumer, and CollectionJson already round-trips this shape on both sides of the wire. If a
-- metagame projection is ever wanted, add it beside this — do not shred the blob.
--
-- AN ILLEGAL DECK IS STORED. Drafts are a legitimate named state; validation happens on QUEUE.
-- Refusing a write would delete a player's work-in-progress the moment a balance patch or a spent
-- card made it illegal.
--
-- THE PAIRING IS IN `body`, NOT IN COLUMNS (ADR-0009, 2026-09-04). `primaryClass`/`secondaryClass`
-- are fields of the JSON, for the same reason the cards are: nothing server-side queries on them.
-- The consequence is worth knowing before you write an UPDATE — the pairing is immutable, and that
-- CANNOT be enforced here by leaving a column out of an upsert's `do update set`, the way the old
-- character table enforced it. `body` is replaced wholesale on every save. So
-- PostgresPlayerDataStore.SaveDeckAsync does a `select body ... for update`, merges through
-- DeckPairing.Preserve and writes the result, all in one transaction. A hand-written UPDATE against
-- this table bypasses that and can move a pairing the application refuses to move.
create table if not exists deck (
  deck_id      text        primary key,
  player_id    text        not null,
  body         text        not null,
  created_at   timestamptz not null,
  updated_at   timestamptz not null
);

create index if not exists deck_player_idx on deck (player_id);

-- Daily quest progress (dev-docs/specs/daily-quests.md §6). ONE ROW PER MATCH that advanced a
-- quest, never a counter: the primary key below is the whole idempotency story, so a redelivered
-- match writes nothing and a quest cannot be inflated by a retry. A player's progress is
--     select coalesce(sum(credit), 0) ... where player_id = $1 and quest_id = $2 and quest_day = $3
-- and it is derived on every read rather than stored.
--
-- THERE IS NO ASSIGNMENT TABLE, DELIBERATELY. Which quest a player holds on a day is a pure
-- function of (player_id, quest_day) — see QuestCatalog — so nothing is written at rollover and a
-- player who never logs in has no rows at all. The consequence to know before adding a reroll:
-- a reroll is a stored assignment, and it needs a table this schema does not have.
--
-- `credit` is not always 1. The card quests contribute the number of cards played that match.
create table if not exists quest_progress (
  player_id    text        not null,
  quest_id     text        not null,
  quest_day    date        not null,
  match_id     text        not null,
  credit       integer     not null,
  credited_at  timestamptz not null,
  primary key (player_id, quest_id, quest_day, match_id),
  constraint quest_progress_credit_positive check (credit > 0)
);

-- Serves the per-player read that GET /quests makes for its three active days.
create index if not exists quest_progress_player_day_idx
  on quest_progress (player_id, quest_day desc, quest_id);

-- Server-private, like the ladder tables above: Supabase's Data API gets no policies and no rows.
alter table quest_progress enable row level security;

-- Every match a player has finished, bot matches included (launch item 5, Sam 2026-10-26: a
-- newcomer's first match is against Greedy and their second against H1). ONE ROW PER (player, match),
-- never a counter, for quest_progress's reason: the key is the idempotency story, so a redelivered
-- result writes nothing. The gate reads only the first two ids per player, through the primary key.
-- A seat is a human's; bot accounts and AI soak never get a row (CompletedMatchLedger).
create table if not exists completed_match (
  player_id  text        not null,
  match_id   text        not null,
  ended_at   timestamptz not null,
  primary key (player_id, match_id)
);

create index if not exists completed_match_player_ended_idx on completed_match (player_id, ended_at);

alter table completed_match enable row level security;

-- Achievements, the Ledger of Bad Ideas (AchievementCatalog). ONE ROW PER ACHIEVEMENT A PLAYER HAS
-- EARNED, written once: the primary key keeps the first match and date, and a later earning
-- writes nothing. Nothing ever deletes a row, because the stores behind it cannot delete an
-- achievement either. The primary key also serves GET /achievements' per-player read.
create table if not exists achievement_earned (
  player_id       text        not null,
  achievement_id  text        not null,
  match_id        text        not null,
  earned_at       timestamptz not null,
  primary key (player_id, achievement_id)
);

alter table achievement_earned enable row level security;

-- ── King of the Week (#421) ────────────────────────────────────────────────────────────────────
-- favor_score: one row per (reign, player) holding that player's BEST score for the reign. The
-- board reads it; reached_at breaks ties (earlier wins) and does not move on an equal score.
-- favor_prize: what a placed player was paid for a reign, written once when the reign's gold is
-- paid. A top-three row's back is settled later, by a separate pass, because the place backs' art
-- can land after the reign closes: back_settled_at null means its back is still owed.
-- king_reign: one row per closed reign. favorite_id/favorite_name are nulled by account deletion
-- (the reign stays, like ladder_match, and reads as having no Favorite).
create table if not exists favor_score (
  reign        int         not null,
  player_id    text        not null,
  display_name text        not null,
  score        int         not null,
  reached_at   timestamptz not null,
  primary key (reign, player_id)
);

-- The Ledger's seals read every reign one player scored in; the key leads with reign, not player.
create index if not exists favor_score_player_reign_idx on favor_score (player_id, reign);

create table if not exists favor_prize (
  reign       int     not null,
  player_id   text    not null,
  place       int     not null,
  gold        bigint  not null default 0,
  back_id     int,
  choice_open boolean not null default false,
  primary key (reign, player_id)
);

alter table favor_prize add column if not exists back_settled_at timestamptz;

create table if not exists king_reign (
  reign         int     primary key,
  favorite_id   text,
  favorite_name text,
  struck        boolean not null default false,
  paid_at       timestamptz
);

alter table favor_score enable row level security;
alter table favor_prize enable row level security;
alter table king_reign enable row level security;

-- ── Friends (#409) ─────────────────────────────────────────────────────────────────────────────
-- Keyed on player_id like everything else here, and on NOTHING from `account.email`: some accounts
-- authenticate without one (Sam, 2026-10-01), and a friends graph that needed it would exclude them.

-- A player's friend code — the only identifier of another player that ever reaches a client — and
-- their sign-up name, copied here from the token so a friend can be shown it. Minted lazily, on a player's first social request, so
-- a player who has never opened the social surface has no row and cannot be found by a code.
--
-- display_name is client-authored text rendered on SOMEBODY ELSE'S machine. It is stored only after
-- PlayerName.Check (FriendsEndpoint.SyncNameAsync) — a hand-written UPDATE bypasses that.
create table if not exists friend_code (
  player_id     text        primary key,
  code          text        not null unique,   -- FriendCode canonical form: ten symbols, no hyphen
  display_name  text,
  created_at    timestamptz not null
);

-- One row per PAIR, whoever asked: the lower player_id is always player_lo, so A→B and B→A are the
-- same row and cannot both exist. The comparison is pinned to "C" because the application orders
-- the pair ordinally (InMemoryFriendStore.Key), and a database whose default collation disagreed
-- would reject a row the application considers ordered.
--
-- A block is NOT a status here — see friend_block.
create table if not exists friendship (
  player_lo     text        not null,
  player_hi     text        not null,
  status        text        not null,
  requested_by  text        not null,
  created_at    timestamptz not null,
  primary key (player_lo, player_hi),
  constraint friendship_ordered check (player_lo collate "C" < player_hi collate "C"),
  constraint friendship_status check (status in ('pending', 'accepted')),
  constraint friendship_requester check (requested_by in (player_lo, player_hi))
);

-- The primary key serves the player_lo half of "every row touching this player".
create index if not exists friendship_hi_idx on friendship (player_hi);

-- Blocks are directional and each side's is its own row. Folding them into friendship.status would
-- give a pair ONE block between them, so the first side to unblock would lift the other's too.
create table if not exists friend_block (
  blocker     text        not null,
  blocked     text        not null,
  created_at  timestamptz not null,
  primary key (blocker, blocked),
  constraint friend_block_distinct check (blocker <> blocked)
);

create index if not exists friend_block_blocked_idx on friend_block (blocked);

-- Server-private, like the ladder and quest tables above: Supabase's Data API gets no policies and
-- no rows.
alter table friend_code enable row level security;
alter table friendship enable row level security;
alter table friend_block enable row level security;

-- ── Play Games (#423) ──────────────────────────────────────────────────────────────────────────
-- The player's own Play Games id, read from Google server-side with the Game server credential
-- (PlayGamesLink), never taken from the phone. One Google account maps to one account here: a
-- second account linking the same id takes it over. Friends lists are NEVER stored: Google's terms
-- allow keeping them 30 days at most, so they are fetched per request and dropped.
create table if not exists play_games_link (
  player_id      text        primary key,
  play_games_id  text        not null unique,
  linked_at      timestamptz not null
);

alter table play_games_link enable row level security;

-- ── Account deletion (docs/dev-docs/operations/account-deletion.md) ───────────────────────────
-- One row per approved deletion run, written ONLY by Kingdom.Tools.AccountDelete. Each step of the
-- run stamps its column as it completes, so a run that stops partway resumes from here rather than
-- repeating a step. The accounts step stamps inside its own transaction, so it is done or not done,
-- never half.
--
-- NO EMAIL AND NO player_id, EVER — not even while the run is in flight. The plan file (kept outside
-- the repo, deleted when the run completes) carries those; this row is the company's record that a
-- deletion happened and when, and must not become a second copy of the identity it removed.
-- The request is named by its Gmail message id only.
--
-- tombstone_id is the random `deleted-<uuid>` written over the player's id in the rows that are kept
-- for other people's history (ladder_match, match_seat, card_seen). archive_expires_on is when the
-- kept store:/refund: ledger rows may be purged: April 15 of the year after deletion, plus 7 years.
create table if not exists account_deletion (
  plan_sha256             text        primary key,   -- the approved plan file's hash
  request_message_id      text        not null,      -- the Gmail message id of the request
  request_received_at     timestamptz not null,
  tombstone_id            text        not null unique,
  started_at              timestamptz not null,
  auth_delete_started_at  timestamptz,               -- the Admin API call was about to be made
  auth_deleted_at         timestamptz,               -- step a; the JWT wait counts from here
  accounts_db_at          timestamptz,               -- step c
  telemetry_db_at         timestamptz,               -- step d
  prereg_at               timestamptz,               -- step e (Cloudflare D1)
  completed_at            timestamptz,               -- step f
  archive_expires_on      date
);

alter table account_deletion enable row level security;
