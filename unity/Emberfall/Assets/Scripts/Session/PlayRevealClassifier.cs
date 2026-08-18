using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>
    /// How long a played card of each type holds the playback queue for its reveal beat, in
    /// seconds. A spell reads longest because it has no other tell — it resolves and is gone.
    /// A minion gets a shorter beat because the board itself announces it a moment later, so the
    /// reveal is a lead-in rather than the whole story (Sam, 2026-08-18).
    ///
    /// UnityEngine-free: VfxDirector owns the authored numbers and hands a copy down, same as it
    /// already did for the single spell hold.
    /// </summary>
    public readonly struct PlayRevealHolds
    {
        public readonly float Minion;
        public readonly float Spell;
        public readonly float Weapon;

        public PlayRevealHolds(float minion, float spell, float weapon)
        {
            Minion = minion;
            Spell  = spell;
            Weapon = weapon;
        }

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
    /// What a play reveal needs to draw: the card that was played, who it hit (if anyone), who
    /// played it (fallback anchor when there is no single target), its type (the reveal's timing
    /// and placement both key off it), and how long to hold the whole entry so the reveal has
    /// time to read before the card's effect becomes visible.
    /// </summary>
    public readonly struct PlayReveal
    {
        public readonly CardDefinition Def;
        public readonly InstanceId? Target;
        public readonly PlayerId Controller;
        public readonly CardType Type;
        public readonly float HoldSeconds;

        public PlayReveal(CardDefinition def, InstanceId? target, PlayerId controller,
                          CardType type, float holdSeconds)
        {
            Def = def;
            Target = target;
            Controller = controller;
            Type = type;
            HoldSeconds = holdSeconds;
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

                CardInstance played = FindInstance(snapshot, e.Source.Value);
                if (played == null) continue;
                if (!db.TryGet(played.DefinitionId, out CardDefinition def)) continue;

                float hold = holds.For(def.Type);
                if (hold <= 0f) continue;   // type tuned to no reveal — keep looking

                InstanceId? target = null;
                for (int j = i + 1; j < events.Count; j++)
                {
                    if (events[j].Target.HasValue) { target = events[j].Target; break; }
                }

                return new PlayReveal(def, target, played.Controller, def.Type, hold);
            }

            return null;
        }

        // A spell has already moved to Graveyard by the time this runs and a minion to Board
        // (CommandProcessor sets Zone before emitting CardPlayed), but this checks every public
        // zone rather than assume — the snapshot is the only source of truth for where an
        // instance actually landed, and the answer now differs by card type.
        private static CardInstance FindInstance(GameState state, InstanceId id)
        {
            foreach (var ps in state.Players)
            {
                foreach (var c in ps.Graveyard) if (c.Id == id) return c;
                foreach (var c in ps.Board)     if (c.Id == id) return c;
                foreach (var c in ps.Hand)      if (c.Id == id) return c;
                if (ps.Weapon != null && ps.Weapon.Id == id) return ps.Weapon;
            }
            return null;
        }
    }
}
