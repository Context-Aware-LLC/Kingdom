using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Serialization
{
    /// <summary>
    /// THE canonical GameState walk — the single normative statement of canonical field order
    /// (A2 ruling, dev-docs/architecture/determinism-and-projection.md:13). Promoted verbatim from the test-layer
    /// GameStateHasher; byte-for-byte identical output is the A2a go/no-go.
    ///
    /// Ordering rules (dev-docs/architecture/determinism-and-projection.md:13):
    ///  • FIXED, hand-specified field order — never reflection. GameState declaration order is
    ///    advisory; THIS walk is authoritative.
    ///  • ordered lists serialize in list order; the one unordered collection (keywords) gets a
    ///    total order imposed (sorted by enum int) so it cannot perturb the digest;
    ///  • nullable objects write a present-flag byte then the body (see WalkWeapon);
    ///  • RNG RawState is folded in (§4.3) — same board, different RNG cursor ≠ equal states.
    ///
    /// Divergence guards (Risk 3): the field-inventory lock + per-field mutation tests in the
    /// Firewall CI stage go red when a state field is added/removed/renamed or fails to reach the
    /// digest. Touching this walk on a live wire format is a hash-canonical change — §7 review.
    /// </summary>
    public static class GameStateSerializer
    {
        /// <summary>Serialize the full authoritative state into the sink in canonical order.</summary>
        public static void Serialize(GameState s, ICanonicalWriter w)
        {
            w.Str(s.Id.Value);
            w.U64(s.StateVersion);
            w.U64(s.Rng.RawState);                  // §4.3 — RNG cursor is part of the canonical bytes
            w.Str(s.ActivePlayer.Value);
            w.Enum(s.Phase);
            w.I32(s.TurnNumber);
            WalkPlayer(w, s.Players[0]);            // [0] then [1] — fixed seat order
            WalkPlayer(w, s.Players[1]);
            w.I32(s.FatigueCounter0);
            w.I32(s.FatigueCounter1);
            w.Enum(s.Outcome);
            w.I32(s.ActionResolutionsThisTurn);
            w.I32(s.NextInstanceCounter);
            w.U64(s.NextEventSequence);
            w.Bool(s.Mulligan0Done);
            w.Bool(s.Mulligan1Done);
            WalkPendingChoice(w, s.PendingChoice);  // A2b tail seam — always null in M2 (§7 2026-07-15)
        }

        private static void WalkPlayer(ICanonicalWriter w, PlayerState p)
        {
            w.Str(p.Id.Value);
            WalkHero(w, p.Hero);
            WalkZone(w, p.Deck);                    // ordered lists — serialized in list order (§4.1)
            WalkZone(w, p.Hand);
            WalkZone(w, p.Board);
            WalkWeapon(w, p.Weapon);                // nullable
            WalkZone(w, p.Graveyard);
            WalkMana(w, p.Mana);
            w.Bool(p.HeroPowerUsedThisTurn);
            w.I32(p.HeroAttacksUsedThisTurn);
            w.I32(p.NextTurnBackdraftLock);
        }

        private static void WalkHero(ICanonicalWriter w, HeroInstance h)
        {
            w.I32(h.Id.Value);
            w.Str(h.Controller.Value);
            w.Enum(h.Class);
            w.I32(h.CurrentHealth);
            w.I32(h.MaxHealth);
            w.I32(h.Armor);
            w.Str(h.HeroPowerId);
            w.Bool(h.IsFrosted); // added step 4 — hero frost state is determinism-relevant
        }

        private static void WalkZone(ICanonicalWriter w, List<CardInstance> zone)
        {
            w.I32(zone.Count);
            foreach (var c in zone) WalkCard(w, c);
        }

        private static void WalkCard(ICanonicalWriter w, CardInstance c)
        {
            w.I32(c.Id.Value);
            w.Str(c.DefinitionId);
            w.Str(c.Controller.Value);
            w.Enum(c.Zone);
            w.I32(c.CurrentAttack);
            w.I32(c.CurrentHealth);
            w.I32(c.MaxHealth);
            w.NullableI32(c.CurrentDurability);

            // The one unordered collection in state: impose a total order (sorted by enum int) so it
            // cannot perturb the digest (§4.1).
            var kws = new List<Keyword>(c.Keywords);
            kws.Sort((a, b) => ((int)a).CompareTo((int)b));
            w.I32(kws.Count);
            foreach (var k in kws) w.Enum(k);

            w.I32(c.Enchantments.Count);            // list order (§4.1)
            foreach (var e in c.Enchantments)
            {
                w.I32(e.Source.Value);
                w.I32(e.AttackDelta);
                w.I32(e.HealthDelta);
                w.NullableEnum(e.GrantedKeyword);
                w.Bool(e.IsAura);
            }

            w.Bool(c.HasAegisShield);
            w.Bool(c.IsFrosted);
            w.Bool(c.IsSilenced);
            w.Bool(c.SummonedThisTurn);
            w.I32(c.AttacksUsedThisTurn);
            w.Bool(c.HasUsedRekindle);
            w.I32(c.Position);
        }

        private static void WalkWeapon(ICanonicalWriter w, CardInstance? weapon)
        {
            if (weapon is null) { w.Bool(false); return; }
            w.Bool(true);
            WalkCard(w, weapon);
        }

        private static void WalkPendingChoice(ICanonicalWriter w, PendingChoice? pc)
        {
            if (pc is null) { w.Bool(false); return; }  // M2 path: single 0x00 present-flag byte
            w.Bool(true);
            w.Str(pc.Chooser.Value);
            w.Enum(pc.Kind);
            w.I32(pc.Source.Value);
            w.I32(pc.Options.Count);                    // count prefix, then list order (§4.1).
            // Phase-2 ringfence (OBL-3): list order MUST be the deterministic RNG roll order of
            // the Unearth pool — the walk is order-PRESERVING only, never an ordering source.
            foreach (var id in pc.Options) w.Str(id);
            w.U64(pc.ChoiceSequence);
        }

        private static void WalkMana(ICanonicalWriter w, ManaPool m)
        {
            w.I32(m.Max);
            w.I32(m.Available);
            w.I32(m.Spent);
            w.I32(m.Locked);
            w.I32(m.Temporary);
        }
    }
}
