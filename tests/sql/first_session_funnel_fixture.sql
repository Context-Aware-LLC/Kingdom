-- Fixture for the view first_session_funnel (src/Kingdom.Net.Server/Telemetry/first_session_funnel.sql).
--
-- THROWAWAY DATABASE ONLY. It writes rows into raw_event, match and match_seat; never point it at
-- the real store. Run against a scratch Postgres after applying schema.sql and the view file:
--
--   docker run -d --rm --name funnel-pg -e POSTGRES_PASSWORD=fx -p 55439:5432 postgres:16
--   psql -v ON_ERROR_STOP=1 -f src/Kingdom.Net.Server/Telemetry/schema.sql
--   psql -v ON_ERROR_STOP=1 -f src/Kingdom.Net.Server/Telemetry/first_session_funnel.sql
--   psql -v ON_ERROR_STOP=1 -f tests/sql/first_session_funnel_fixture.sql
--
-- One session per drop reason plus the full path, a clock-skewed device, and a crash after the
-- full path. Times are offsets from T0 = five days ago (one session is one day old, to read
-- `pending`). The last query prints each session beside the drop it should read; `ok` must be
-- true on every row.

-- ── Helpers (session-local) ─────────────────────────────────────────────────────────────────
create or replace function pg_temp.ts(off interval) returns text language sql stable as $$
  select to_char((date_trunc('minute', now()) - interval '5 days' + off) at time zone 'UTC',
                 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
$$;

create or replace function pg_temp.put(j jsonb) returns void language sql as $$
  insert into raw_event (event_uid, type, line)
  values (md5(j::text), j->>'type', j)
  on conflict (event_uid) do nothing
$$;

-- A client_milestone row. `skew` is the device clock minus true time; the server's `at` is true
-- time plus 200 ms (or `late`, for a row flushed by a later launch).
create or replace function pg_temp.cm(sid text, dev text, acct text, ms text, off interval,
                                      extra jsonb default '{}', skew interval default '0',
                                      late interval default '0')
returns void language sql as $$
  select pg_temp.put(jsonb_strip_nulls(jsonb_build_object(
    'type', 'client_milestone', 'milestone', ms,
    'session_id', 'f' || lpad(sid, 31, '0'),
    'client_ts', pg_temp.ts(off + skew),
    'build', '1.0.0+7', 'platform', 'android',
    'device', dev, 'account_id', acct,
    'at', pg_temp.ts(off + interval '200 milliseconds' + late)) || extra))
$$;

-- A server row at true time `off`.
create or replace function pg_temp.sv(typ text, off interval, fields jsonb) returns void language sql as $$
  select pg_temp.put(jsonb_build_object('type', typ, 'at', pg_temp.ts(off)) || fields)
$$;

-- A finished match with the player in seat p0 against a bot.
create or replace function pg_temp.mt(mid text, acct text, off interval, reason text, result text)
returns void language sql as $$
  insert into match (match_id, env, source, ended_at, status, end_reason, winner_seat, turn_count)
  values (mid, 'local', 'bot_fill', pg_temp.ts(off)::timestamptz, 'complete', reason,
          case result when 'win' then 'p0' else 'p1' end, 9)
  on conflict do nothing;
  insert into match_seat (match_id, seat, account_id, result) values
    (mid, 'p0', acct, result), (mid, 'p1', 'bot-' || mid, case result when 'win' then 'loss' else 'win' end)
  on conflict do nothing;
$$;

-- The common opening: launch, a passing gate the server also logged, a sign-in.
create or replace function pg_temp.opening(sid text, dev text, acct text, skew interval default '0')
returns void language sql as $$
  select pg_temp.cm(sid, dev, null, 'launch', interval '0 s', '{}', skew);
  select pg_temp.sv('gate_served', interval '1.9 s', jsonb_build_object(
    'session_id', 'f' || lpad(sid, 31, '0'), 'device', dev, 'outcome', 'pass',
    'build', '1.0.0+7', 'platform', 'android'));
  select pg_temp.cm(sid, dev, null, 'gate', interval '2 s', '{"outcome":"pass","attempts":1}', skew);
  select pg_temp.cm(sid, dev, acct, 'sign_in', interval '10 s', '{"outcome":"signed_in"}', skew);
$$;

-- The common start of a first match: queue (client + server), hello, board, first action.
create or replace function pg_temp.into_match(sid text, dev text, acct text, mid text,
                                              skew interval default '0')
returns void language sql as $$
  select pg_temp.cm(sid, dev, acct, 'queue', interval '20 s', '{"source":"menu"}', skew);
  select pg_temp.sv('queue', interval '20.1 s', jsonb_build_object('account_id', acct));
  select pg_temp.sv('hello', interval '20.2 s', jsonb_build_object('account_id', acct, 'device', dev));
  select pg_temp.cm(sid, dev, acct, 'board_playable', interval '25 s',
                    jsonb_build_object('match_id', mid), skew);
  select pg_temp.sv('first_action', interval '40 s', jsonb_build_object('match_id', mid, 'account_id', acct));
$$;

-- ── Sessions ────────────────────────────────────────────────────────────────────────────────
\o /dev/null
-- 01 full path: finishes a win, queues again from the result view, exits.  → null
select pg_temp.opening('01', 'd000000000000001', 'acct-01');
select pg_temp.into_match('01', 'd000000000000001', 'acct-01', 'm01');
select pg_temp.mt('m01', 'acct-01', interval '300 s', 'win', 'win');
select pg_temp.cm('01', 'd000000000000001', 'acct-01', 'queue', interval '310 s', '{"source":"result_view"}');
select pg_temp.sv('queue', interval '310.1 s', '{"account_id":"acct-01"}');
select pg_temp.cm('01', 'd000000000000001', 'acct-01', 'exit', interval '400 s');

-- 02 crash mid-match: a Sentry event carries the session id.  → crash
select pg_temp.opening('02', 'd000000000000002', 'acct-02');
select pg_temp.into_match('02', 'd000000000000002', 'acct-02', 'm02');
select pg_temp.sv('crash', interval '61 s', jsonb_build_object(
  'session_id', 'f' || lpad('02', 31, '0'), 'device', 'd000000000000002',
  'sentry_event_id', 'sentry-02', 'release', '1.0.0+7'));

-- 03 stale build: the gate sent the player to the store.  → gate:stale
select pg_temp.cm('03', 'd000000000000003', null, 'launch', interval '0 s');
select pg_temp.sv('gate_served', interval '1.9 s', jsonb_build_object(
  'session_id', 'f' || lpad('03', 31, '0'), 'device', 'd000000000000003', 'outcome', 'stale',
  'build', '1.0.0+7', 'platform', 'android'));
select pg_temp.cm('03', 'd000000000000003', null, 'gate', interval '2 s', '{"outcome":"stale","attempts":1}');

-- 04 unreachable: three attempts, rows flushed a day later by the next launch.  → gate:unreachable
select pg_temp.cm('04', 'd000000000000004', null, 'launch', interval '0 s', '{}', '0', interval '1 day');
select pg_temp.cm('04', 'd000000000000004', null, 'gate', interval '45 s',
                  '{"outcome":"unreachable","attempts":3}', '0', interval '1 day');
select pg_temp.cm('04', 'd000000000000004', null, 'exit', interval '90 s', '{}', '0', interval '1 day');

-- 05 gate error.  → gate:error
select pg_temp.cm('05', 'd000000000000005', null, 'launch', interval '0 s');
select pg_temp.cm('05', 'd000000000000005', null, 'gate', interval '3 s', '{"outcome":"error","attempts":1}');

-- 06 gate passed, sign-in failed, quit.  → sign_in
select pg_temp.cm('06', 'd000000000000006', null, 'launch', interval '0 s');
select pg_temp.cm('06', 'd000000000000006', null, 'gate', interval '2 s', '{"outcome":"pass","attempts":2}');
select pg_temp.cm('06', 'd000000000000006', null, 'sign_in', interval '30 s', '{"outcome":"failed","cause":"token_rejected"}');
select pg_temp.cm('06', 'd000000000000006', null, 'exit', interval '40 s');

-- 07 signed in, never queued, quit.  → no_queue:exit
select pg_temp.opening('07', 'd000000000000007', 'acct-07');
select pg_temp.cm('07', 'd000000000000007', 'acct-07', 'exit', interval '60 s');

-- 08 signed in, never queued, backgrounded and never came back.  → no_queue:pause
select pg_temp.opening('08', 'd000000000000008', 'acct-08');
select pg_temp.cm('08', 'd000000000000008', 'acct-08', 'pause', interval '60 s');

-- 09 queued into a full court, then backgrounded.  → capacity
select pg_temp.opening('09', 'd000000000000009', 'acct-09');
select pg_temp.cm('09', 'd000000000000009', 'acct-09', 'queue', interval '20 s', '{"source":"menu"}');
select pg_temp.sv('queue_refused', interval '20.1 s', '{"account_id":"acct-09","reason":"capacity"}');
select pg_temp.cm('09', 'd000000000000009', 'acct-09', 'pause', interval '50 s');

-- 10 backgrounded mid-match; the server ended it as a disconnect afterwards.  → backgrounded
select pg_temp.opening('10', 'd000000000000010', 'acct-10');
select pg_temp.into_match('10', 'd000000000000010', 'acct-10', 'm10');
select pg_temp.cm('10', 'd000000000000010', 'acct-10', 'pause', interval '100 s');
select pg_temp.mt('m10', 'acct-10', interval '200 s', 'disconnect', 'loss');

-- 11 quit mid-match; the match ends after the session's exit row.  → exit
select pg_temp.opening('11', 'd000000000000011', 'acct-11');
select pg_temp.into_match('11', 'd000000000000011', 'acct-11', 'm11');
select pg_temp.cm('11', 'd000000000000011', 'acct-11', 'pause', interval '100 s');
select pg_temp.cm('11', 'd000000000000011', 'acct-11', 'exit', interval '100 s');
select pg_temp.mt('m11', 'acct-11', interval '160 s', 'disconnect', 'loss');

-- 12 socket dropped with no pause or exit; last client row is the board.  → disconnect
select pg_temp.opening('12', 'd000000000000012', 'acct-12');
select pg_temp.into_match('12', 'd000000000000012', 'acct-12', 'm12');
select pg_temp.mt('m12', 'acct-12', interval '200 s', 'disconnect', 'loss');

-- 13 conceded the first match, quit.  → concede
select pg_temp.opening('13', 'd000000000000013', 'acct-13');
select pg_temp.into_match('13', 'd000000000000013', 'acct-13', 'm13');
select pg_temp.mt('m13', 'acct-13', interval '200 s', 'concede', 'loss');
select pg_temp.cm('13', 'd000000000000013', 'acct-13', 'exit', interval '230 s');

-- 14 turn timer forfeited the first match, then backgrounded.  → forfeit
select pg_temp.opening('14', 'd000000000000014', 'acct-14');
select pg_temp.into_match('14', 'd000000000000014', 'acct-14', 'm14');
select pg_temp.mt('m14', 'acct-14', interval '400 s', 'timeout', 'loss');
select pg_temp.cm('14', 'd000000000000014', 'acct-14', 'pause', interval '410 s');

-- 15 won the first match, never queued again.  → no_second_match
select pg_temp.opening('15', 'd000000000000015', 'acct-15');
select pg_temp.into_match('15', 'd000000000000015', 'acct-15', 'm15');
select pg_temp.mt('m15', 'acct-15', interval '300 s', 'win', 'win');
select pg_temp.cm('15', 'd000000000000015', 'acct-15', 'exit', interval '330 s');

-- 16 launched, no gate row ever arrived.  → unknown
select pg_temp.cm('16', 'd000000000000016', null, 'launch', interval '0 s');

-- 17 a day old, stopped at sign-in.  → pending
select pg_temp.cm('17', 'd000000000000017', null, 'launch', interval '4 days');
select pg_temp.cm('17', 'd000000000000017', null, 'gate', interval '4 days 2 s', '{"outcome":"pass","attempts":1}');

-- 18 device clock two hours fast; sign-in SKIPPED, so the account binds through hello; full path.
--    → null, account_id = acct-18, clock_offset = -02:00 (minus the gate latency)
select pg_temp.cm('18', 'd000000000000018', null, 'launch', interval '0 s', '{}', interval '2 hours');
select pg_temp.sv('gate_served', interval '1.9 s', jsonb_build_object(
  'session_id', 'f' || lpad('18', 31, '0'), 'device', 'd000000000000018', 'outcome', 'pass',
  'build', '1.0.0+7', 'platform', 'android'));
select pg_temp.cm('18', 'd000000000000018', null, 'gate', interval '2 s', '{"outcome":"pass","attempts":1}', interval '2 hours');
select pg_temp.cm('18', 'd000000000000018', null, 'sign_in', interval '10 s', '{"outcome":"skipped"}', interval '2 hours');
select pg_temp.cm('18', 'd000000000000018', null, 'queue', interval '20 s', '{"source":"menu"}', interval '2 hours');
select pg_temp.sv('queue', interval '20.1 s', '{"account_id":"acct-18"}');
select pg_temp.sv('hello', interval '20.2 s', '{"account_id":"acct-18","device":"d000000000000018"}');
select pg_temp.cm('18', 'd000000000000018', null, 'board_playable', interval '25 s', '{"match_id":"m18"}', interval '2 hours');
select pg_temp.sv('first_action', interval '40 s', '{"match_id":"m18","account_id":"acct-18"}');
select pg_temp.mt('m18', 'acct-18', interval '300 s', 'win', 'win');
select pg_temp.sv('queue', interval '320 s', '{"account_id":"acct-18"}');
select pg_temp.cm('18', 'd000000000000018', null, 'pause', interval '330 s', '{}', interval '2 hours');

-- 19 full path, then a crash on the way out. Reached every milestone, so no drop.  → null
select pg_temp.opening('19', 'd000000000000019', 'acct-19');
select pg_temp.into_match('19', 'd000000000000019', 'acct-19', 'm19');
select pg_temp.mt('m19', 'acct-19', interval '300 s', 'win', 'win');
select pg_temp.cm('19', 'd000000000000019', 'acct-19', 'queue', interval '310 s', '{"source":"result_view"}');
select pg_temp.sv('crash', interval '320 s', jsonb_build_object(
  'session_id', 'f' || lpad('19', 31, '0'), 'device', 'd000000000000019',
  'sentry_event_id', 'sentry-19', 'release', '1.0.0+7'));

-- A neighbour the windows must NOT pick up: acct-15 queues again a day after its session.
select pg_temp.sv('queue', interval '1 day', '{"account_id":"acct-15"}');

\o

-- ── Check ───────────────────────────────────────────────────────────────────────────────────
-- Milestone times as seconds after started_at, on the session's client clock.
select right(session_id, 2) as s,
       extract(epoch from gate_passed_at    - started_at)::numeric(6,1) as gate,
       extract(epoch from signed_in_at      - started_at)::numeric(6,1) as sign_in,
       extract(epoch from queued_at         - started_at)::numeric(6,1) as queued,
       extract(epoch from queue_refused_at  - started_at)::numeric(6,1) as refused,
       extract(epoch from board_playable_at - started_at)::numeric(6,1) as board,
       extract(epoch from first_action_at   - started_at)::numeric(6,1) as first_act,
       extract(epoch from match_finished_at - started_at)::numeric(6,1) as finished,
       extract(epoch from queued_again_at   - started_at)::numeric(6,1) as again,
       extract(epoch from ended_at          - started_at)::numeric(6,1) as ended
from first_session_funnel
where session_id like 'f%'
order by session_id;

-- The drop each session should read.
with expected(n, want) as (values
  ('01', null), ('02', 'crash'), ('03', 'gate:stale'), ('04', 'gate:unreachable'),
  ('05', 'gate:error'), ('06', 'sign_in'), ('07', 'no_queue:exit'), ('08', 'no_queue:pause'),
  ('09', 'capacity'), ('10', 'backgrounded'), ('11', 'exit'), ('12', 'disconnect'),
  ('13', 'concede'), ('14', 'forfeit'), ('15', 'no_second_match'), ('16', 'unknown'),
  ('17', 'pending'), ('18', null), ('19', null))
select right(f.session_id, 2)            as s,
       f.account_id                      as acct,
       f.ended_by,
       f.clock_offset,
       f.gate_outcome                    as gate,
       f.gate_attempts                   as att,
       f.match_id                        as mid,
       f.match_end_reason                as end_reason,
       f.queued_again_source             as qa_src,
       f.furthest_milestone,
       f.drop_reason,
       f.drop_reason is not distinct from e.want as ok
from first_session_funnel f
join expected e on f.session_id = 'f' || lpad(e.n, 31, '0')
order by f.session_id;
