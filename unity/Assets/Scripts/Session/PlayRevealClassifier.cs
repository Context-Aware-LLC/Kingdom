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
        public readonly SchoolTiming Fire;
        public readonly SchoolTiming Shadow;
        public readonly SchoolTiming Clockwright;
        public readonly SchoolTiming Water;
        public readonly SchoolTiming Frost;
        public readonly SchoolTiming Gravekeep;
        public readonly SchoolTiming Verdant;
        public readonly SchoolTiming Heal;

        public PlayRevealHolds(float minion, float spell, float weapon,
                               SchoolTiming earth = default, SchoolTiming lightning = default,
                               SchoolTiming fire = default, SchoolTiming shadow = default,
                               SchoolTiming clockwright = default, SchoolTiming water = default,
                               SchoolTiming frost = default, SchoolTiming gravekeep = default,
                               SchoolTiming verdant = default, SchoolTiming heal = default)
        {
            Minion = minion;
            Spell  = spell;
            Weapon = weapon;
            Earth = earth;
            Lightning = lightning;
            Fire = fire;
            Shadow = shadow;
            Clockwright = clockwright;
            Water = water;
            Frost = frost;
            Gravekeep = gravekeep;
            Verdant = verdant;
            Heal = heal;
        }

        public SchoolTiming For(SpellEffect effect) => effect switch
        {
            SpellEffect.Earth     => Earth,
            SpellEffect.Lightning => Lightning,
            SpellEffect.Fire      => Fire,
            SpellEffect.Shadow    => Shadow,
            SpellEffect.Clockwright => Clockwright,
            SpellEffect.Water     => Water,
            SpellEffect.Frost     => Frost,
            SpellEffect.Gravekeep => Gravekeep,
            SpellEffect.Verdant   => Verdant,
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
    /// Stonewarden is earth; Tempest is lightning; 2026-10-14: Emberward is fire, Nightveil is
    /// shadow, Clockwright is constructs, Tidecaller is water or frost, Gravekeep is the grave, Verdant is vines).
    ///
    /// <para>Every spell of a school's class carries it, whatever the spell does: the caster
    /// effect always plays, the school's missile goes to each character the spell damages, and
    /// its target effect lands on every other character the spell touches. Over-applied on
    /// purpose, to be trimmed where it reads wrong (Sam, 2026-10-14).</para></summary>
    public enum SpellEffect
    {
        None,

        /// <summary>A Stonewarden spell: a buildup on the caster, then a rock falls on each
        /// character it damages.</summary>
        Earth,

        /// <summary>A Tempest spell: a charge under the caster, then an arc or a bolt to each
        /// character it damages.</summary>
        Lightning,

        /// <summary>An Emberward spell: a flare on the caster, then a fireball flies to each
        /// character it damages and bursts there.</summary>
        Fire,

        /// <summary>A Nightveil spell: a black wipe across the caster's portrait, then a wisp of
        /// shadow drifts to each character it damages and blooms into a ring there.</summary>
        Shadow,

        /// <summary>A Clockwright spell: gears gather on the caster, then a spray of parts flies
        /// to each character it damages and bursts there.</summary>
        Clockwright,

        /// <summary>A Tidecaller spell that is not frost: water gathers on the caster, then a
        /// wave goes to each character it damages and splashes there.</summary>
        Water,

        /// <summary>A Tidecaller spell that freezes, or is named for frost: frost gathers on the
        /// caster, then an ice shard goes to each character it damages. No target effect: the
        /// freeze's crystals are it.</summary>
        Frost,

        /// <summary>A Gravekeep spell: grave mist and bone rise on the caster, then a skull goes
        /// to each character it damages and bursts into soulfire there.</summary>
        Gravekeep,

        /// <summary>A Verdant spell: vines twirl on the caster, then a vine reaches each
        /// character it damages and a wreath closes on it. Its heals keep the healing shimmer.</summary>
        Verdant,
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

        /// <summary>Every other character the spell acted on: buffed, bounced, destroyed,
        /// silenced, frozen, transformed, taken, or given a keyword or armour. Never null.</summary>
        public readonly IReadOnlyList<InstanceId> Touched;

        /// <summary>The played card can drown, so its damaged characters get the drown wash.</summary>
        public readonly bool Drowns;

        public readonly SpellEffect Effect;

        /// <summary>When the reveal card itself is gone. Equal to <see cref="HoldSeconds"/> unless
        /// a spell effect runs on past the card.</summary>
        public readonly float CardSeconds;

        public PlayReveal(CardDefinition def, InstanceId source, InstanceId? target,
                          PlayerId controller, CardType type, float holdSeconds,
                          RevealDestination destination, int destSlot, int destBoardCount,
                          IReadOnlyList<InstanceId> struck = null,
                          IReadOnlyList<InstanceId> healed = null,
                          SpellEffect effect = SpellEffect.None, float cardSeconds = -1f,
                          IReadOnlyList<InstanceId> touched = null, bool drowns = false)
        {
            Drowns = drowns;
            Touched = touched ?? System.Array.Empty<InstanceId>();
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
                var touched = new List<InstanceId>();
                for (int j = i + 1; j < events.Count; j++)
                {
                    GameEvent f = events[j];
                    if (!f.Target.HasValue || f.Source != source) continue;
                    if (f.Kind == GameEventKind.DamageApplied && !struck.Contains(f.Target.Value))
                        struck.Add(f.Target.Value);
                    else if (f.Kind == GameEventKind.HealthRestored && !healed.Contains(f.Target.Value))
                        healed.Add(f.Target.Value);
                    else if (Touches(f.Kind) && !touched.Contains(f.Target.Value))
                        touched.Add(f.Target.Value);
                }
                // A character damaged or healed already has its effect.
                touched.RemoveAll(t => struck.Contains(t) || healed.Contains(t));

                SpellEffect effect = SpellEffect.None;
                if (def.Type == CardType.SPELL)
                    effect = def.Class switch
                    {
                        CardClass.STONEWARDEN => SpellEffect.Earth,
                        CardClass.TEMPEST     => SpellEffect.Lightning,
                        CardClass.EMBERWARD   => SpellEffect.Fire,
                        CardClass.NIGHTVEIL   => SpellEffect.Shadow,
                        CardClass.CLOCKWRIGHT => SpellEffect.Clockwright,
                        CardClass.TIDECALLER  => IsFrost(def) ? SpellEffect.Frost : SpellEffect.Water,
                        CardClass.GRAVEKEEP   => SpellEffect.Gravekeep,
                        CardClass.VERDANT     => SpellEffect.Verdant,
                        _                     => SpellEffect.None,
                    };
                float tail = holds.For(effect).Tail(struck.Count + touched.Count);
                if (def.Type == CardType.SPELL)
                    tail = System.Math.Max(tail, holds.Heal.Tail(healed.Count));

                return new PlayReveal(def, source, target, played.Controller, def.Type, hold + tail,
                                      dest, slot, boardCount, struck, healed, effect, hold,
                                      touched, Has<DrownAction>(def));
            }

            return null;
        }

        /// <summary>Which Tidecaller spells are frost rather than water: any that freezes, plus
        /// the two damage spells named for frost. Keyed by card id for those two because nothing
        /// on the card says so.</summary>
        private static bool IsFrost(CardDefinition def) =>
            Has<FrostAction>(def) || def.Id == 152 /* Frost Ripple */ || def.Id == 169 /* Hoarfrost Lash */;

        private static bool Has<T>(CardDefinition def) where T : RuleAction
        {
            foreach (RuleDefinition rule in def.Rules)
                if (rule.Action is T) return true;
            return false;
        }

        // What a spell does to a character, other than damage or healing, that its school's
        // target effect lands on. A summon is not here: the minion is not yet on the board the
        // effect is drawn against.
        private static bool Touches(GameEventKind kind) => kind switch
        {
            GameEventKind.StatsModified   => true,
            GameEventKind.KeywordApplied  => true,
            GameEventKind.CardDestroyed   => true,
            GameEventKind.CardSilenced    => true,
            GameEventKind.FrostApplied    => true,
            GameEventKind.CardTransformed => true,
            GameEventKind.CardBounced     => true,
            GameEventKind.ControlChanged  => true,
            GameEventKind.ArmorGained     => true,
            _                             => false,
        };

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
