-- first_session_funnel — pre-publication item 2 (docs/dev-docs/specs/first-session-funnel.md is
-- the row contract; docs/dev-docs/project/pre-publication-first-session.md item 2 is the design).
--
-- APPLIED APART FROM schema.sql, on purpose. TelemetryDrain runs schema.sql as one command, then
-- this file as a second one inside its own try/catch: a view that fails to create is logged
-- ("[telemetry-schema] ...") and costs the view, never the drain. Put tables and indexes in
-- schema.sql, never here.
--
-- CHANGING THE COLUMN SET NEEDS A DROP FIRST. `create or replace view` can append columns at the
-- end but cannot drop, rename, reorder or retype one; Postgres refuses, the drain logs it, and the
-- OLD view stays live. Run `drop view first_session_funnel;` against the store, then deploy.
--
-- One row per session_id. Read it as:
--   * Every timestamp column is on the SESSION'S CLIENT CLOCK. Server-observed times (`at`,
--     match.ended_at) are shifted by `clock_offset` (server minus client): the last gate_served
--     `at` minus the client's gate row when the server answered the gate, else the smallest
--     `at - client_ts` over the session's rows, else zero.
--   * The window runs from started_at to ended_at: the first `exit`, else 10 minutes after a last
--     `pause` that no `resume` followed, else the last client row. Server rows with no session_id
--     attach by account (hello: by device) when their `at` falls inside the window, widened by one
--     minute each side and shifted onto the server clock.
--   * The first match is the first board_playable's match_id, else the account's first
--     first_action in the window, else its first match_seat whose match ended in the window. It
--     attaches by match_id, not by window, because a match outlives an `exit` row.
--   * furthest_milestone is monotonic: a later milestone implies the earlier ones, so a skipped
--     sign-in that went on to queue reads `queued` or further, with signed_in_at null.
--   * drop_reason is decided at furthest_milestone, i.e. where the session STOPPED. A session that
--     reached queued_again has none (null), crash or not; one younger than three days (server
--     clock) reads `pending`; otherwise `crash` outranks every other reason.
--   * client_ts / at are parsed only when they look like ISO 8601 with a zone; anything else reads
--     as null and the row is ignored. An impossible calendar date (Feb 30) still matches and would
--     fail the query: the client endpoint's validation is what keeps one out.
--
-- security_invoker: a plain view reads its tables with the OWNER's rights, so on Supabase (where
-- default privileges grant new public objects to anon/authenticated) it would serve raw_event's
-- device hashes and account ids through the REST API past any RLS on raw_event. With it, the
-- caller needs its own grant on the tables underneath. Needs Postgres 15+.
create or replace view first_session_funnel with (security_invoker = true) as
with
ev as not materialized (
  select
    r.type,
    r.line->>'milestone'        as milestone,
    r.line->>'session_id'       as session_id,
    r.line->>'device'           as device,
    r.line->>'account_id'       as account_id,
    r.line->>'build'            as build,
    r.line->>'platform'         as platform,
    r.line->>'outcome'          as outcome,
    case when r.line->>'attempts' ~ '^[0-9]{1,6}$' then (r.line->>'attempts')::int end as attempts,
    r.line->>'match_id'         as match_id,
    r.line->>'source'           as source,
    r.line->>'sentry_event_id'  as sentry_event_id,
    case when r.line->>'client_ts' ~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,6})?(Z|[+-][0-9]{2}:?[0-9]{2})$'
         then (r.line->>'client_ts')::timestamptz end as client_ts,
    case when r.line->>'at' ~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,6})?(Z|[+-][0-9]{2}:?[0-9]{2})$'
         then (r.line->>'at')::timestamptz end as at_ts
  from raw_event r
  where r.type in ('client_milestone', 'gate_served', 'hello', 'queue', 'queue_refused', 'first_action', 'crash')
),
-- Client rows that can be placed in a session and in time.
cm as not materialized (
  select * from ev
  where type = 'client_milestone' and session_id ~ '^[0-9a-f]{32}$' and client_ts is not null
),
-- Server rows that can be placed in time.
srv as not materialized (
  select * from ev where type <> 'client_milestone' and at_ts is not null
),
sess as (
  select
    session_id,
    min(client_ts)                                                       as first_ts,
    max(client_ts)                                                       as last_ts,
    min(client_ts) filter (where milestone = 'launch')                   as launched_at,
    min(client_ts) filter (where milestone = 'exit')                     as exit_ts,
    max(client_ts) filter (where milestone = 'pause')                    as last_pause_ts,
    max(client_ts) filter (where milestone = 'resume')                   as last_resume_ts,
    (array_agg(device order by client_ts) filter (where device is not null))[1]     as c_device,
    (array_agg(build order by client_ts) filter (where build is not null))[1]       as build,
    (array_agg(platform order by client_ts) filter (where platform is not null))[1] as platform,
    min(at_ts - client_ts)                                               as min_lag,
    min(client_ts) filter (where milestone = 'gate' and outcome = 'pass') as gate_pass_ts,
    max(client_ts) filter (where milestone = 'gate')                     as gate_last_ts,
    (array_agg(outcome order by client_ts desc) filter (where milestone = 'gate'))[1] as gate_outcome,
    sum(attempts) filter (where milestone = 'gate')                      as gate_attempts,
    min(client_ts) filter (where milestone = 'sign_in' and outcome = 'signed_in') as signed_in_at,
    (array_agg(outcome order by client_ts desc) filter (where milestone = 'sign_in'))[1] as sign_in_outcome,
    (array_agg(account_id order by client_ts)
       filter (where milestone = 'sign_in' and account_id is not null))[1] as acct_sign_in,
    (array_agg(account_id order by client_ts) filter (where account_id is not null))[1] as acct_any,
    min(client_ts) filter (where milestone = 'queue')                    as queue_ts,
    min(client_ts) filter (where milestone = 'board_playable')           as board_playable_at,
    (array_agg(match_id order by client_ts)
       filter (where milestone = 'board_playable' and match_id is not null))[1] as board_match_id
  from cm
  group by session_id
),
base as (
  select
    s.*,
    gs.pass_at                                   as gs_pass_at,
    gs.device                                    as gs_device,
    cr.sentry_event_id                           as crash_event_id,
    coalesce(s.launched_at, s.first_ts)          as started_at,
    case when s.exit_ts is not null then 'exit'
         when s.last_pause_ts is not null
              and (s.last_resume_ts is null or s.last_resume_ts < s.last_pause_ts) then 'pause'
         else 'last_row' end                     as ended_by,
    coalesce(case when s.gate_outcome in ('pass', 'stale') then gs.last_at - s.gate_last_ts end,
             s.min_lag, interval '0')            as clock_offset
  from sess s
  left join lateral (
    select max(g.at_ts)                                            as last_at,
           min(g.at_ts) filter (where g.outcome = 'pass')          as pass_at,
           (array_agg(g.device order by g.at_ts) filter (where g.device is not null))[1] as device
    from srv g where g.type = 'gate_served' and g.session_id = s.session_id
  ) gs on true
  left join lateral (
    select c.sentry_event_id from srv c
    where c.type = 'crash' and c.session_id = s.session_id
    order by c.at_ts limit 1
  ) cr on true
),
win as (
  select
    b.*,
    coalesce(b.c_device, b.gs_device)            as device,
    case b.ended_by
      when 'exit'  then b.exit_ts
      when 'pause' then greatest(b.last_pause_ts + interval '10 minutes', b.last_ts)
      else b.last_ts end                         as ended_at
  from base b
),
bound as (
  select
    w.*,
    w.started_at + w.clock_offset - interval '1 minute' as w_lo,
    w.ended_at   + w.clock_offset + interval '1 minute' as w_hi
  from win w
),
acct as (
  select
    b.*,
    coalesce(b.acct_sign_in, b.acct_any, h.account_id) as account_id
  from bound b
  left join lateral (
    select h.account_id from srv h
    where h.type = 'hello' and h.device = b.device and h.account_id is not null
      and h.at_ts between b.w_lo and b.w_hi
    order by h.at_ts limit 1
  ) h on true
),
firstmatch as (
  select
    a.*,
    coalesce(a.board_match_id, fa.match_id, ms.match_id) as match_id,
    q.at_ts  as srv_queue_at,
    qr.at_ts as srv_refused_at
  from acct a
  left join lateral (
    select f.match_id from srv f
    where f.type = 'first_action' and f.account_id = a.account_id and f.match_id is not null
      and f.at_ts between a.w_lo and a.w_hi
    order by f.at_ts limit 1
  ) fa on true
  left join lateral (
    select s.match_id from match_seat s join match m on m.match_id = s.match_id
    where s.account_id = a.account_id and m.ended_at between a.w_lo and a.w_hi
    order by m.ended_at limit 1
  ) ms on true
  left join lateral (
    select min(x.at_ts) as at_ts from srv x
    where x.type = 'queue' and x.account_id = a.account_id and x.at_ts between a.w_lo and a.w_hi
  ) q on true
  left join lateral (
    select min(x.at_ts) as at_ts from srv x
    where x.type = 'queue_refused' and x.account_id = a.account_id and x.at_ts between a.w_lo and a.w_hi
  ) qr on true
),
withmatch as (
  select
    f.*,
    m.ended_at                                   as match_ended_srv,
    m.status                                     as match_status,
    m.end_reason                                 as raw_end_reason,
    fa.at_ts                                     as first_action_srv
  from firstmatch f
  left join match m on m.match_id = f.match_id
  left join lateral (
    select min(x.at_ts) as at_ts from srv x
    where x.type = 'first_action' and x.match_id = f.match_id and x.account_id = f.account_id
  ) fa on true
),
milestones as (
  select
    w.*,
    coalesce(w.gate_pass_ts, w.gs_pass_at - w.clock_offset)  as gate_passed_at,
    least(w.queue_ts, w.srv_queue_at - w.clock_offset)       as queued_at,
    w.srv_refused_at - w.clock_offset                        as queue_refused_at,
    w.first_action_srv - w.clock_offset                      as first_action_at,
    w.match_ended_srv - w.clock_offset                       as match_end_c,
    case when w.match_status = 'complete' and w.raw_end_reason in ('win', 'concede', 'timeout')
         then w.match_ended_srv - w.clock_offset end         as match_finished_at,
    case when w.match_status = 'void' then 'void' else w.raw_end_reason end as match_end_reason
  from withmatch w
),
after_match as (
  select
    m.*,
    least(qc.client_ts, qs.at_ts - m.clock_offset)           as queued_again_at,
    qc.source                                                as queued_again_source,
    lc.milestone                                             as last_lifecycle
  from milestones m
  -- "Queued again": any queue after the first match's terminal outcome, client or server.
  left join lateral (
    select c.client_ts, c.source from cm c
    where c.session_id = m.session_id and c.milestone = 'queue'
      and m.match_end_c is not null and c.client_ts > m.match_end_c
    order by c.client_ts limit 1
  ) qc on true
  left join lateral (
    select min(x.at_ts) as at_ts from srv x
    where x.type = 'queue' and x.account_id = m.account_id
      and m.match_ended_srv is not null and x.at_ts > m.match_ended_srv and x.at_ts <= m.w_hi
  ) qs on true
  -- The last pause/resume/exit between the board and the match's end (by client_ts): decides
  -- backgrounded / exit / disconnect for a first match that did not finish. An exit at the same
  -- instant as a pause wins (a quit raises both).
  left join lateral (
    select c.milestone from cm c
    where c.session_id = m.session_id and c.milestone in ('pause', 'resume', 'exit')
      and c.client_ts >= coalesce(m.board_playable_at, m.first_action_at, m.started_at)
      and c.client_ts <= coalesce(m.match_end_c, m.ended_at)
    order by c.client_ts desc, (c.milestone = 'exit') desc limit 1
  ) lc on true
),
furthest as (
  select
    a.*,
    case
      when a.queued_again_at   is not null then 'queued_again'
      when a.match_finished_at is not null then 'match_finished'
      when a.first_action_at   is not null then 'first_action'
      when a.board_playable_at is not null then 'board_playable'
      when a.queued_at is not null or a.queue_refused_at is not null then 'queued'
      when a.signed_in_at      is not null then 'signed_in'
      when a.gate_passed_at    is not null then 'gate_passed'
      else 'launched'
    end as furthest_milestone
  from after_match a
)
select
  session_id,
  device,
  account_id,
  build,
  platform,
  started_at,
  ended_at,
  ended_by,
  clock_offset,
  launched_at,
  gate_passed_at,
  gate_outcome,
  gate_attempts,
  signed_in_at,
  sign_in_outcome,
  queued_at,
  queue_refused_at,
  match_id,
  board_playable_at,
  first_action_at,
  match_finished_at,
  match_end_reason,
  queued_again_at,
  queued_again_source,
  crash_event_id,
  furthest_milestone,
  case
    when furthest_milestone = 'queued_again' then null
    when started_at + clock_offset > now() - interval '3 days' then 'pending'
    when crash_event_id is not null then 'crash'
    when furthest_milestone = 'launched' then
      case gate_outcome
        when 'stale'       then 'gate:stale'
        when 'unreachable' then 'gate:unreachable'
        when 'error'       then 'gate:error'
        else 'unknown' end
    when furthest_milestone = 'gate_passed' then 'sign_in'
    when furthest_milestone = 'signed_in' then
      'no_queue' || case ended_by when 'exit' then ':exit' when 'pause' then ':pause' else '' end
    when furthest_milestone = 'queued' then
      case when queue_refused_at is not null then 'capacity' else 'unknown' end
    when furthest_milestone in ('board_playable', 'first_action') then
      case last_lifecycle
        when 'exit'  then 'exit'
        when 'pause' then 'backgrounded'
        else case when match_end_reason = 'disconnect' then 'disconnect' else 'unknown' end
      end
    when furthest_milestone = 'match_finished' then
      case match_end_reason
        when 'concede' then 'concede'
        when 'timeout' then 'forfeit'
        else 'no_second_match' end
    else 'unknown'
  end as drop_reason
from furthest;
