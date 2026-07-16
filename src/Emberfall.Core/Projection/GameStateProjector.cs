using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Projection
{
    /// <summary>
    /// The hidden-information line (M2 WS A3; <c>decisions/ADR-0006</c>,
    /// <c>multiplayer/state-sync-and-security.md</c> §5, <c>engineering/a3-projector-design.md</c>).
    /// One redaction module, two entry points: <see cref="Project"/> for snapshots,
    /// <see cref="ProjectEvent"/> for the D7 stream. Pure — the authoritative graph is never
    /// touched; every projected object is freshly allocated, and redacted values are constructed
    /// as sentinels directly so hidden truth never exists in the projected graph, even transiently.
    ///
    /// Every field copied here is classified in the §5.2 redaction matrix; a Contracts field
    /// without a row fails the projection-manifest test (default-deny — the clone copies only
    /// what it explicitly names, so an unclassified field also cannot survive projection).
    /// </summary>
    public static class GameStateProjector
    {
        /// <summary>The reserved redaction sentinel (D-P1: the engine never mints id 0).</summary>
        public static readonly InstanceId Sentinel = new InstanceId(0);

        // ── Snapshot entry point ─────────────────────────────────────────────────

        /// <summary>Full projected snapshot for <paramref name="viewer"/> — the wire payload (ADR-0002).</summary>
        public static GameState Project(GameState s, PlayerId viewer)
        {
            int viewerSeat = s.SeatOf(viewer);   // unknown viewer throws — no default projection exists
            int maxVisibleId = 0;

            var p0 = ProjectPlayer(s, s.Players[0], 0, viewerSeat, ref maxVisibleId);
            var p1 = ProjectPlayer(s, s.Players[1], 1, viewerSeat, ref maxVisibleId);

            // Rng: REDACTED — fixed all-zero cursor; the live cursor predicts every future draw.
            var proj = new GameState(s.Id, new DeterministicRandom(0UL), p0, p1)
            {
                StateVersion = s.StateVersion,                          // PUBLIC
                ActivePlayer = s.ActivePlayer,                          // PUBLIC
                Phase = s.Phase,                                        // PUBLIC
                TurnNumber = s.TurnNumber,                              // PUBLIC
                FatigueCounter0 = s.FatigueCounter0,                    // PUBLIC
                FatigueCounter1 = s.FatigueCounter1,                    // PUBLIC
                Outcome = s.Outcome,                                    // PUBLIC
                ActionResolutionsThisTurn = s.ActionResolutionsThisTurn,// PUBLIC
                NextEventSequence = s.NextEventSequence,                // PUBLIC (events never dropped per-viewer)
                Mulligan0Done = s.Mulligan0Done,                        // PUBLIC
                Mulligan1Done = s.Mulligan1Done,                        // PUBLIC
                PendingChoice = ProjectPendingChoice(s.PendingChoice, viewer, viewerSeat, ref maxVisibleId),
            };

            // NextInstanceCounter: REDACTED — normalized to a pure function of what THIS viewer
            // sees (max visible id + 1), killing the hidden-mint gap channel (§5.2/§5.2.1).
            proj.NextInstanceCounter = maxVisibleId + 1;
            return proj;
        }

        private static PlayerState ProjectPlayer(GameState s, PlayerState ps, int seat, int viewerSeat, ref int max)
        {
            var hero = new HeroInstance(Track(ps.Hero.Id, ref max), ps.Hero.Controller, ps.Hero.Class, ps.Hero.MaxHealth)
            {
                CurrentHealth = ps.Hero.CurrentHealth,   // PUBLIC (all hero fields)
                Armor = ps.Hero.Armor,
                HeroPowerId = ps.Hero.HeroPowerId,
                IsFrozen = ps.Hero.IsFrozen,
            };

            var proj = new PlayerState(ps.Id, hero);

            // Deck: REDACTED for BOTH viewers (owner included — you know your list, never your
            // order). Placeholders preserve Deck.Count, which is public.
            foreach (var c in ps.Deck)
                proj.Deck.Add(Placeholder(c.Controller, Zone.Deck));

            // Hand: OWNER — as-is for the owning viewer, card-backs for the opponent.
            if (seat == viewerSeat)
                foreach (var c in ps.Hand) proj.Hand.Add(CloneCard(s, c, viewerSeat, ref max));
            else
                foreach (var c in ps.Hand) proj.Hand.Add(Placeholder(c.Controller, Zone.Hand));

            // Board / Graveyard / Weapon / Mana / turn flags: PUBLIC.
            foreach (var c in ps.Board) proj.Board.Add(CloneCard(s, c, viewerSeat, ref max));
            foreach (var c in ps.Graveyard) proj.Graveyard.Add(CloneCard(s, c, viewerSeat, ref max));

            if (ps.Weapon != null)
            {
                proj.Weapon = new WeaponInstance
                {
                    Id = Track(ps.Weapon.Id, ref max),
                    DefinitionId = ps.Weapon.DefinitionId,
                    Attack = ps.Weapon.Attack,
                    Durability = ps.Weapon.Durability,
                };
            }

            proj.Mana.Max = ps.Mana.Max;
            proj.Mana.Available = ps.Mana.Available;
            proj.Mana.Spent = ps.Mana.Spent;
            proj.Mana.Locked = ps.Mana.Locked;
            proj.Mana.Temporary = ps.Mana.Temporary;
            proj.HeroPowerUsedThisTurn = ps.HeroPowerUsedThisTurn;
            proj.HeroAttacksUsedThisTurn = ps.HeroAttacksUsedThisTurn;
            proj.NextTurnOverloadLock = ps.NextTurnOverloadLock;

            return proj;
        }

        private static CardInstance CloneCard(GameState s, CardInstance c, int viewerSeat, ref int max)
        {
            var proj = new CardInstance(Track(c.Id, ref max), c.DefinitionId, c.Controller)
            {
                Zone = c.Zone,
                CurrentAttack = c.CurrentAttack,
                CurrentHealth = c.CurrentHealth,
                MaxHealth = c.MaxHealth,
                CurrentDurability = c.CurrentDurability,
                HasAegisShield = c.HasAegisShield,
                IsFrozen = c.IsFrozen,
                SummonedThisTurn = c.SummonedThisTurn,
                AttacksUsedThisTurn = c.AttacksUsedThisTurn,
                HasUsedReborn = c.HasUsedReborn,
                Position = c.Position,
            };
            foreach (var k in c.Keywords) proj.Keywords.Add(k);
            foreach (var e in c.Enchantments)
            {
                // Enchantment.Source is spec-PUBLIC but resolution-guarded (design §4 / review
                // finding 3): an ephemeral hero-power id or a bounced-away minion's retired id
                // must not reach the wire. Visible referent → real id; else the sentinel.
                bool visible = IsVisibleTo(Classify(s, e.Source), viewerSeat);
                proj.Enchantments.Add(new Enchantment
                {
                    Source = visible ? Track(e.Source, ref max) : Sentinel,
                    AttackDelta = e.AttackDelta,
                    HealthDelta = e.HealthDelta,
                    GrantedKeyword = e.GrantedKeyword,
                });
            }
            return proj;
        }

        /// <summary>The §5.2.1 card-back form: sentinel id, empty definition, zeroed stats.</summary>
        private static CardInstance Placeholder(PlayerId controller, Zone zone)
            => new CardInstance(Sentinel, "", controller) { Zone = zone };

        private static PendingChoice? ProjectPendingChoice(PendingChoice? pc, PlayerId viewer, int viewerSeat, ref int max)
        {
            if (pc is null) return null;   // the only M2 case (A2b): null → null for both viewers.

            if (pc.Chooser == viewer)
            {
                // The chooser receives the record whole (they must be able to pick). Order of
                // Options is load-bearing (OBL-3) — copied, never re-sorted.
                var full = new PendingChoice
                {
                    Chooser = pc.Chooser,
                    Kind = pc.Kind,
                    Source = Track(pc.Source, ref max),
                    ChoiceSequence = pc.ChoiceSequence,
                };
                foreach (var o in pc.Options) full.Options.Add(o);
                return full;
            }

            // Non-chooser (ratified OBL-1 table): Chooser/Kind real; Source/Options/ChoiceSequence
            // sentineled. Empty Options — the option COUNT is not disclosed.
            return new PendingChoice
            {
                Chooser = pc.Chooser,
                Kind = pc.Kind,
                Source = Sentinel,
                ChoiceSequence = 0,
            };
        }

        private static InstanceId Track(InstanceId id, ref int max)
        {
            if (id.Value > max) max = id.Value;
            return id;
        }

        // ── Event entry point ────────────────────────────────────────────────────

        /// <summary>
        /// Per-viewer copy of one D7 event. Never dropped, only value-redacted — both viewers see
        /// the same event count and <see cref="GameEvent.Sequence"/> values (§5.3). <paramref name="vis"/>
        /// is the emit-time sidecar captured by <c>EventBuffer</c>.
        /// </summary>
        public static GameEvent ProjectEvent(in GameEvent e, int viewerSeat, in EventVisibility vis)
        {
            // OBL-8 unlisted-kind backstop: a Kind outside the classified vocabulary is fully
            // sentineled for every viewer. (All 22 current members are classified; this catches
            // enum members added without an OBL-8 row, alongside the CI row-coverage test.)
            if (!KindIsClassified(e.Kind))
                return new GameEvent(e.Kind, e.Sequence, Sentinel, Sentinel, null, EventFlags.None);

            InstanceId? src = Redact(e.Source, vis.Source, viewerSeat);
            InstanceId? tgt = Redact(e.Target, vis.Target, viewerSeat);

            // OBL-8 Amount column: P for every classified kind except the two sentinel rows —
            // onDebuff (no emitter exists; pre-classified conservatively, D-OQ3) and onDiscover
            // (the pool-size magnitude channel; Phase 2 wires the chooser's unredacted copy via
            // PendingChoice context when Discover actually lands — until then default-deny).
            int? amount = (e.Kind == VfxEvent.onDebuff || e.Kind == VfxEvent.onDiscover) ? null : e.Amount;

            // OBL-8 Flags column: P for every classified kind (incl. Overdraw/IsToken on onDraw).
            return new GameEvent(e.Kind, e.Sequence, src, tgt, amount, e.Flags);
        }

        private static InstanceId? Redact(InstanceId? id, IdVisibility vis, int viewerSeat)
        {
            if (!id.HasValue) return null;   // absence is shape, not information
            return IsVisibleTo(vis, viewerSeat) ? id : Sentinel;
        }

        private static bool IsVisibleTo(IdVisibility vis, int viewerSeat) => vis switch
        {
            IdVisibility.Public => true,
            IdVisibility.Owner0 => viewerSeat == 0,
            IdVisibility.Owner1 => viewerSeat == 1,
            _ => false,
        };

        private static bool KindIsClassified(VfxEvent kind)
            => kind >= VfxEvent.onPlay && kind <= VfxEvent.onGameOver;

        // ── Shared zone classification (snapshot Enchantment.Source + EventBuffer capture) ──

        /// <summary>
        /// Where does <paramref name="id"/> live in <paramref name="s"/> right now? Public zones
        /// (hero, equipped weapon, board, graveyard) → Public; a hand → its owner; deck-resident
        /// or unresolvable → Hidden (default-deny).
        /// </summary>
        internal static IdVisibility Classify(GameState s, InstanceId id)
        {
            for (int seat = 0; seat < 2; seat++)
            {
                var ps = s.Players[seat];
                if (ps.Hero.Id.Equals(id)) return IdVisibility.Public;
                if (ps.Weapon != null && ps.Weapon.Id.Equals(id)) return IdVisibility.Public;
                foreach (var c in ps.Board) if (c.Id.Equals(id)) return IdVisibility.Public;
                foreach (var c in ps.Graveyard) if (c.Id.Equals(id)) return IdVisibility.Public;
                foreach (var c in ps.Hand) if (c.Id.Equals(id)) return seat == 0 ? IdVisibility.Owner0 : IdVisibility.Owner1;
                foreach (var c in ps.Deck) if (c.Id.Equals(id)) return IdVisibility.Hidden;
            }
            return IdVisibility.Hidden;
        }
    }
}
