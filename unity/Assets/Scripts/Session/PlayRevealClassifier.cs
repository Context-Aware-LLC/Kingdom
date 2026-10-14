using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Session
{
    /// <summary>
    /// How long a played card of each type holds the playback queue for its reveal beat, in
    /// seconds. This is the WHOLE beat — fly-in, hold, and fly-out — because the entry may not
    /// release until the reveal card has finished travelling: the instant it does, the board
    /// redraws with the real card in place, and anything still in flight is a second copy of a
    /// card already on the board (Sam, 2026-08-19: "it both renders to the board and renders to
    /// the hold position at the same time").
    ///
    /// UnityEngine-free: VfxDirector owns the authored numbers and hands a copy down, same as it
    /// already did for the single spell hold.
    /// </summary>
    public readonly struct PlayRevealHolds
    {
        public readonly float Minion;
        public readonly float Spell;
        public readonly float Weapon;

        /// <summary>A spell-school effect lands after the reveal card has collapsed, so the beat
        /// runs on past it, and the entry releases (the damage or healing shows) as the effect
        /// reaches its last target. See <see cref="SchoolTiming"/>.</summary>
        public readonly SchoolTiming Earth;
        public readonly SchoolTiming Lightning;
        public readonly SchoolTiming Heal;

        public PlayRevealHolds(float minion, float spell, float weapon,
                               SchoolTiming earth = default, SchoolTiming lightning = default,
                               SchoolTiming heal = default)
        {
            Minion = minion;
            Spell  = spell;
            Weapon = weapon;
            Earth = earth;
            Lightning = lightning;
            Heal = heal;
        }

        public SchoolTiming For(SpellEffect effect) => effect switch
        {
            SpellEffect.Earth     => Earth,
            SpellEffect.Lightning => Lightning,
            _                     => default,
        };

        /// <summary>Hold for a card type, or 0 for one with no reveal beat. HERO_POWER lands on
        /// the 0 arm and never reaches here anyway: a hero power is not a card played from hand
        /// and raises no CardPlayed (it raises ON_CAST off a carrierless HERO_POWER source).</summary>
        public float For(CardType type) => type switch
        {
            CardType.MINION => Minion,
            CardType.SPELL  => Spell,
            CardType.WEAPON => Weapon,
            _               => 0f,
        };
    }

    /// <summary>
    /// Where a reveal card goes when its beat ends. This is what stops the double-render: a card
    /// with a destination FLIES there and is gone the moment the board redraws, so the reveal and
    /// the real card are never both on screen. A card with <see cref="None"/> has nowhere to be —
    /// it collapses into whatever it was revealed over instead.
    /// </summary>
    public enum RevealDestination
    {
        /// <summary>Spell — resolves into the graveyard, which has no anchor and would read as
        /// nothing anyway. Collapses into what it hit.</summary>
        None,

        /// <summary>Minion — lands in its slot on the controller's board row.</summary>
        BoardSlot,

        /// <summary>Weapon — lands in the controller's weapon socket.</summary>
        WeaponSocket,
    }

    /// <summary>One school's timing after the reveal card is gone: <see cref="Lead"/> seconds to
    /// the first target, then <see cref="Stagger"/> between targets.</summary>
    public readonly struct SchoolTiming
    {
        public readonly float Lead;
        public readonly float Stagger;

        public SchoolTiming(float lead, float stagger)
        {
            Lead = lead;
            Stagger = stagger;
        }

        /// <summary>What reaching <paramref name="targets"/> characters adds to the beat.</summary>
        public float Tail(int targets) => targets <= 0 ? 0f : Lead + (targets - 1) * Stagger;
    }

    /// <summary>Which spell-school effect a played spell carries, beyond the reveal itself.
    /// There is no school field on a card; the class stands in for it (Sam, 2026-10-13:
    /// Stonewarden is earth; Tempest is lightning).</summary>
    public enum SpellEffect
    {
        None,

        /// <summary>A Stonewarden spell that deals damage: a buildup on the caster, then a rock
        /// falls on each character it hits.</summary>
        Earth,

        /// <summary>A Tempest spell that deals damage: an arc from the caster to each character
        /// it hits, then a bolt down onto it.</summary>
        Lightning,
    }

    /// <summary>
    /// What a play reveal needs to draw: the card, the instance it came from (which is how the
    /// reveal finds the hand slot it flew OUT of), who it hit (if anyone), who played it, its type,
    /// where it is headed, and how long the whole beat holds the entry.
    /// </summary>
    public readonly struct PlayReveal
    {
        public readonly CardDefinition Def;

        /// <summary>The played card's instance. VfxDirector looks this up in the PRE-play snapshot
        /// it still holds to find the exact hand slot the card flew out of — post-play the card is
        /// no longer in hand, so the classifier cannot answer this and does not try.</summary>
        public readonly InstanceId Source;

        public readonly InstanceId? Target;
        public readonly PlayerId Controller;
        public readonly CardType Type;
        public readonly float HoldSeconds;

        public readonly RevealDestination Destination;

        /// <summary>Board slot the minion landed in, and the board size AFTER the play. Both are
        /// needed because the row re-centres on its own count (BoardRowLayout): the anchor the
        /// scene currently holds is laid out for the board WITHOUT this minion, so flying at it
        /// would land the card where the row is about to stop being. Meaningless unless
        /// <see cref="Destination"/> is <see cref="RevealDestination.BoardSlot"/>.</summary>
        public readonly int DestSlot;
        public readonly int DestBoardCount;

        /// <summary>Every character the played card damaged, and every one it healed, in the
        /// order the engine resolved them — read off the entry's own events, so a multi-target
        /// spell names all of its targets before any of them is hit. Never null.</summary>
        public readonly IReadOnlyList<InstanceId> Struck;
        public readonly IReadOnlyList<InstanceId> Healed;

        public readonly SpellEffect Effect;

        /// <summary>When the reveal card itself is gone. Equal to <see cref="HoldSeconds"/> unless
        /// a spell effect runs on past the card.</summary>
        public readonly float CardSeconds;

        public PlayReveal(CardDefinition def, InstanceId source, InstanceId? target,
                          PlayerId controller, CardType type, float holdSeconds,
                          RevealDestination destination, int destSlot, int destBoardCount,
                          IReadOnlyList<InstanceId> struck = null,
                          IReadOnlyList<InstanceId> healed = null,
                          SpellEffect effect = SpellEffect.None, float cardSeconds = -1f)
        {
            Struck = struck ?? System.Array.Empty<InstanceId>();
            Healed = healed ?? System.Array.Empty<InstanceId>();
            Effect = effect;
            CardSeconds = cardSeconds < 0f ? holdSeconds : cardSeconds;
            Def = def;
            Source = source;
            Target = target;
            Controller = controller;
            Type = type;
            HoldSeconds = holdSeconds;
            Destination = destination;
            DestSlot = destSlot;
            DestBoardCount = destBoardCount;
        }
    }

    /// <summary>
    /// Presentation-only add-on: a played card gets a beat where it is held up, magnified, and
    /// readable before its consequences land.
    ///
    /// <para>This started spell-only, because a spell was the case with no visible tell at all —
    /// it resolves and is gone, while a minion at least announces itself by appearing. It now
    /// covers every playable type, with the hold scaled per type: the beat is what makes a match
    /// followable, not just what makes a spell identifiable (Sam, 2026-08-18).</para>
    ///
    /// <para>UnityEngine-free like the rest of this asmdef's session-side logic — GameState /
    /// GameEvent / ICardDatabase are all plain C#.</para>
    /// </summary>
    public static class PlayRevealClassifier
    {
        /// <summary>The first <see cref="GameEventKind.CardPlayed"/> in this entry's batch whose
        /// card type has a non-zero hold, or null if there isn't one.
        /// Only the first — a command resolves one played card at a time in this M1 slice, so a
        /// second CardPlayed fact in the same batch isn't a case that exists yet.</summary>
        public static PlayReveal? Classify(GameState snapshot, IReadOnlyList<GameEvent> events,
                                           ICardDatabase db, PlayRevealHolds holds)
        {
            if (snapshot == null || events == null || db == null) return null;

            for (int i = 0; i < events.Count; i++)
            {
                GameEvent e = events[i];
                if (e.Kind != GameEventKind.CardPlayed || !e.Source.HasValue) continue;

                InstanceId source = e.Source.Value;
                if (!TryLocate(snapshot, source, out CardInstance played,
                               out RevealDestination dest, out int slot, out int boardCount))
                    continue;
                if (!db.TryGet(played.DefinitionId, out CardDefinition def)) continue;

                float hold = holds.For(def.Type);
                if (hold <= 0f) continue;   // type tuned to no reveal — keep looking

                InstanceId? target = null;
                for (int j = i + 1; j < events.Count; j++)
                {
                    if (events[j].Target.HasValue) { target = events[j].Target; break; }
                }

                var struck = new List<InstanceId>();
                var healed = new List<InstanceId>();
                for (int j = i + 1; j < events.Count; j++)
                {
                    GameEvent f = events[j];
                    if (!f.Target.HasValue || f.Source != source) continue;
                    if (f.Kind == GameEventKind.DamageApplied && !struck.Contains(f.Target.Value))
                        struck.Add(f.Target.Value);
                    else if (f.Kind == GameEventKind.HealthRestored && !healed.Contains(f.Target.Value))
                        healed.Add(f.Target.Value);
                }

                SpellEffect effect = SpellEffect.None;
                if (def.Type == CardType.SPELL && struck.Count > 0)
                    effect = def.Class switch
                    {
                        CardClass.STONEWARDEN => SpellEffect.Earth,
                        CardClass.TEMPEST     => SpellEffect.Lightning,
                        _                     => SpellEffect.None,
                    };
                float tail = holds.For(effect).Tail(struck.Count);
                if (def.Type == CardType.SPELL)
                    tail = System.Math.Max(tail, holds.Heal.Tail(healed.Count));

                return new PlayReveal(def, source, target, played.Controller, def.Type, hold + tail,
                                      dest, slot, boardCount, struck, healed, effect, hold);
            }

            return null;
        }

        // A spell has already moved to Graveyard by the time this runs and a minion to Board
        // (CommandProcessor sets Zone before emitting CardPlayed), but this checks every public
        // zone rather than assume — the snapshot is the only source of truth for where an
        // instance actually landed, and the answer now differs by card type.
        //
        // WHERE it landed is the half that matters for the flight: a minion's board index and the
        // resulting row size are read here, off the post-command snapshot, because this is the only
        // place that has them. VfxDirector cannot re-derive either — the snapshot it is holding
        // when the reveal fires is deliberately the PRE-play one.
        private static bool TryLocate(GameState state, InstanceId id, out CardInstance card,
                                      out RevealDestination dest, out int slot, out int boardCount)
        {
            card = null;
            dest = RevealDestination.None;
            slot = -1;
            boardCount = 0;

            foreach (var ps in state.Players)
            {
                for (int i = 0; i < ps.Board.Count; i++)
                {
                    if (ps.Board[i].Id != id) continue;
                    card = ps.Board[i];
                    dest = RevealDestination.BoardSlot;
                    slot = i;
                    boardCount = ps.Board.Count;
                    return true;
                }

                if (ps.Weapon != null && ps.Weapon.Id == id)
                {
                    card = ps.Weapon;
                    dest = RevealDestination.WeaponSocket;
                    return true;
                }

                foreach (var c in ps.Graveyard)
                    if (c.Id == id) { card = c; return true; }

                foreach (var c in ps.Hand)
                    if (c.Id == id) { card = c; return true; }
            }

            return card != null;
        }
    }
}
