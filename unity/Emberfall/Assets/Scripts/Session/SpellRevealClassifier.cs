using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Session
{
    /// <summary>
    /// What a spell-cast reveal needs to draw: the card that was cast, who it hit (if anyone),
    /// who cast it (fallback anchor when there's no single target), and how long to hold the
    /// whole entry so the reveal has time to read before the spell's effect becomes visible.
    /// </summary>
    public readonly struct SpellReveal
    {
        public readonly CardDefinition Def;
        public readonly InstanceId? Target;
        public readonly PlayerId Controller;
        public readonly float HoldSeconds;

        public SpellReveal(CardDefinition def, InstanceId? target, PlayerId controller, float holdSeconds)
        {
            Def = def;
            Target = target;
            Controller = controller;
            HoldSeconds = holdSeconds;
        }
    }

    /// <summary>
    /// Presentation-only add-on (not part of the D7 vocabulary): a played card the opponent casts
    /// has no visible tell today — VfxEvent.onPlay fires identically for a minion (which announces
    /// itself by appearing on the board) and a spell (which just resolves). This distinguishes the
    /// two by resolving the played card's CardDefinition and checking Type == SPELL, so
    /// MatchRunner can hold the whole entry for a reveal beat without needing to know card types
    /// itself.
    ///
    /// UnityEngine-free like the rest of this asmdef's session-side logic — GameState/GameEvent/
    /// ICardDatabase are all plain C#.
    /// </summary>
    public static class SpellRevealClassifier
    {
        /// <summary>The first spell-cast onPlay in this entry's batch, or null if there isn't one.
        /// Only the first — a command resolves one played card at a time in this M1 slice, so a
        /// second onPlay in the same batch isn't a case that exists yet.</summary>
        public static SpellReveal? Classify(GameState snapshot, IReadOnlyList<GameEvent> events,
                                            ICardDatabase db, float holdSeconds)
        {
            if (snapshot == null || events == null || db == null) return null;

            for (int i = 0; i < events.Count; i++)
            {
                GameEvent e = events[i];
                if (e.Kind != VfxEvent.onPlay || !e.Source.HasValue) continue;

                CardInstance played = FindInstance(snapshot, e.Source.Value);
                if (played == null) continue;
                if (!db.TryGet(played.DefinitionId, out CardDefinition def) || def.Type != CardType.SPELL)
                    continue;

                InstanceId? target = null;
                for (int j = i + 1; j < events.Count; j++)
                {
                    if (events[j].Target.HasValue) { target = events[j].Target; break; }
                }

                return new SpellReveal(def, target, played.Controller, holdSeconds);
            }

            return null;
        }

        // The spell has already moved to Graveyard by the time this runs (CommandProcessor sets
        // Zone before emitting onPlay), but this checks Board/Hand too rather than assume — the
        // snapshot is the only source of truth for where an instance actually landed.
        private static CardInstance FindInstance(GameState state, InstanceId id)
        {
            foreach (var ps in state.Players)
            {
                foreach (var c in ps.Graveyard) if (c.Id == id) return c;
                foreach (var c in ps.Board)     if (c.Id == id) return c;
                foreach (var c in ps.Hand)      if (c.Id == id) return c;
            }
            return null;
        }
    }
}
