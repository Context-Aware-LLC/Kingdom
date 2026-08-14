using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The derived-stat layer (EF-017a). A card's live stats are a FUNCTION of its printed base and
    /// its <see cref="CardInstance.Enchantments"/> list — not an accumulator that effects add into.
    ///
    /// <para><b>Why this exists.</b> Before EF-017a, <c>BuffHandler</c> baked <c>CurrentAttack += atk</c>
    /// destructively and the <see cref="Enchantment"/> it wrote alongside was never read back. An effect
    /// that needed to UNAPPLY itself — an aura whose source leaves the board — had nothing to subtract,
    /// because the contribution had already been melted into the total. Recomputing from the list makes
    /// removal the same operation as application: drop the entries, recompute. <see cref="RemoveBySource"/>
    /// is that operation, and it is what makes <see cref="Enchantment.Source"/> load-bearing.</para>
    ///
    /// <para><b>Base stats are read from the <see cref="CardDefinition"/>, not stored on the instance.</b>
    /// <c>DefinitionId</c> already names the printed form, and every reset site in the engine already held
    /// a <c>def</c> to reset from — a stored base would be a second copy of a value the instance can
    /// always look up. That choice is what keeps this change OUT of the hash: no field enters
    /// <c>GameState</c>, the serializer is untouched, and the golden replays hold unrebaselined.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER.</b> If a verb ever changes a card's PRINTED stats in place without
    /// changing its <c>DefinitionId</c> — a "set base attack to N" effect; no such verb exists today, and
    /// <c>TRANSFORM</c> is declared in the <c>Action</c> enum with no handler — that value stops being
    /// derivable and <c>BaseAttack</c>/<c>BaseMaxHealth</c> must become real <see cref="CardInstance"/>
    /// fields. That IS a hash-canonical change: update the canonical serializer, projection, snapshot
    /// codec, field manifest, pinned digest, and affected golden fixtures together.</para>
    /// </summary>
    internal static class DerivedStats
    {
        /// <summary>Printed attack. A row with no attack reads 0.</summary>
        public static int BaseAttack(CardDefinition def) => def.Attack ?? 0;

        /// <summary>Printed health, with the engine's long-standing floor of 1 for a healthless row.</summary>
        public static int BaseMaxHealth(CardDefinition def) => def.Health ?? 1;

        /// <summary>
        /// Recompute <paramref name="c"/>'s live stats from its printed base plus its current enchantment
        /// list, PRESERVING damage already taken. Call this after adding or removing an enchantment on a
        /// minion that is already on the board.
        ///
        /// <para>Damage is carried as <c>MaxHealth - CurrentHealth</c> at entry, so a +N health buff raises
        /// both and a wounded minion stays wounded by the same amount — the behaviour the destructive
        /// <c>+=</c> pair produced, now stated once instead of re-derived at each site.</para>
        ///
        /// <para><b>Stats only — this touches neither the keyword set nor
        /// <see cref="CardInstance.HasAegisShield"/>, and that is deliberate.</b> Both carry state that is
        /// SPENT rather than derived: an Aegis is consumed by absorbing a hit, and SHROUD is stripped when
        /// the minion attacks (<c>CommandProcessor</c>). Re-seeding either from the definition here would
        /// silently refund a spent shield, or restore shroud, every time the minion was buffed. Only the
        /// full-reset path (<see cref="ResetToPrinted"/>) re-seeds, and only because a silence or a bounce
        /// genuinely does return the card to its printed form.</para>
        ///
        /// <para>No clamping. Attack and health may go negative exactly as they could before this layer
        /// existed; a non-positive <c>CurrentHealth</c> is the DeathCheck's business. A floor here would be
        /// a silent rules change and would move the hash.</para>
        /// </summary>
        public static void Recompute(CardInstance c, CardDefinition def)
        {
            int damageTaken = c.MaxHealth - c.CurrentHealth;
            if (damageTaken < 0) damageTaken = 0;
            ApplyStats(c, def, damageTaken);
        }

        /// <summary>
        /// Initialize an existing card for the weapon slot without discarding any runtime state it
        /// already carries. Printed keywords are unioned into the instance, enchantments continue to
        /// contribute to attack, and a pre-existing durability value is retained. A fresh deck/hand
        /// card has no durability yet, so that one missing value is seeded from the definition.
        /// </summary>
        public static void EnterWeapon(CardInstance c, CardDefinition def)
        {
            // Equipping starts a fresh weapon zone-life. A bounced silenced weapon may retain spent
            // durability, but it never carries the removable silence state into the new life.
            c.IsSilenced = false;
            foreach (var k in def.Keywords)
                if (KeywordSemantics.IsCarrierBehavior(k)) c.Keywords.Add(k);
            foreach (var e in c.Enchantments)
                if (e.GrantedKeyword.HasValue &&
                    KeywordSemantics.IsCarrierBehavior(e.GrantedKeyword.Value))
                    c.Keywords.Add(e.GrantedKeyword.Value);

            int attack = BaseAttack(def);
            foreach (var e in c.Enchantments) attack += e.AttackDelta;
            c.CurrentAttack = attack;

            if (!c.CurrentDurability.HasValue)
                c.CurrentDurability = def.Durability ?? 1;
        }

        /// <summary>
        /// Return <paramref name="c"/> to its printed form: enchantments dropped, keywords re-seeded from
        /// the definition, stats recomputed, Aegis restored to whatever the printed keyword set implies,
        /// frost cleared.
        ///
        /// <para><paramref name="preserveDamage"/> is the whole difference between the callers. A SILENCE
        /// keeps the wounds — a buffed body falling away can leave the printed one already dead, which is a
        /// real interaction the DeathCheck then resolves. Board ENTRY and a BOUNCE-to-hand do not: the card
        /// arrives whole, and reading damage off stats belonging to a previous life would carry a stale
        /// wound across the zone change.</para>
        /// </summary>
        public static void ResetToPrinted(CardInstance c, CardDefinition def, bool preserveDamage)
        {
            int damageTaken = 0;
            if (preserveDamage)
            {
                damageTaken = c.MaxHealth - c.CurrentHealth;
                if (damageTaken < 0) damageTaken = 0;
            }

            c.Enchantments.Clear();
            c.Keywords.Clear();
            foreach (var k in def.Keywords)
                if (KeywordSemantics.IsCarrierBehavior(k)) c.Keywords.Add(k);

            ApplyStats(c, def, damageTaken);

            c.HasAegisShield = c.Keywords.Contains(Keyword.AEGIS);
            c.IsFrosted = false;
            c.FrostThawPending = false; // NOR-367: pending is only meaningful while frosted
            c.IsSilenced = false;
        }

        /// <summary>
        /// Apply the full SILENCE reset. Stats return to printed values while a minion's wounds survive;
        /// all ability-bearing state is removed, including printed keywords. Weapon durability is
        /// intentionally untouched: silence must not refund spent durability.
        /// </summary>
        public static void ResetToSilenced(CardInstance c, CardDefinition def, bool preserveDamage)
        {
            int damageTaken = 0;
            if (preserveDamage)
            {
                damageTaken = c.MaxHealth - c.CurrentHealth;
                if (damageTaken < 0) damageTaken = 0;
            }

            c.Enchantments.Clear();
            c.Keywords.Clear();
            ApplyStats(c, def, damageTaken);
            c.HasAegisShield = false;
            c.IsFrosted = false;
            c.FrostThawPending = false; // NOR-367: pending is only meaningful while frosted
            c.IsSilenced = true;
        }

        /// <summary>
        /// Drop every enchantment written by <paramref name="source"/> and recompute. This is the
        /// unapply half of the layer and the reason <see cref="Enchantment.Source"/> is stored: an aura
        /// whose source leaves the board (EF-017b) calls this on each minion it was touching, and the
        /// contribution comes back off cleanly because it was never melted into the total.
        ///
        /// <para>Keywords are SUBTRACTED, never re-seeded: a keyword is removed only if the departing
        /// source granted it and no surviving enchantment grants it — and never if the printed definition
        /// carries it, because that one was never the source's to give. Subtracting rather than rebuilding
        /// is what keeps this from restoring shroud that combat has already broken.</para>
        ///
        /// <para>Returns true if anything was removed, so a caller sweeping a board can skip the recompute
        /// bookkeeping on minions the source never touched.</para>
        /// </summary>
        public static bool RemoveBySource(CardInstance c, CardDefinition def, InstanceId source)
        {
            List<Keyword>? granted = null;
            int removed = 0;

            for (int i = c.Enchantments.Count - 1; i >= 0; i--)
            {
                var e = c.Enchantments[i];
                if (!e.Source.Equals(source)) continue;
                if (e.GrantedKeyword != null) (granted ??= new List<Keyword>()).Add(e.GrantedKeyword.Value);
                c.Enchantments.RemoveAt(i);
                removed++;
            }

            if (removed == 0) return false;

            if (granted != null)
            {
                foreach (var k in granted)
                {
                    if (IsPrinted(def, k)) continue;                     // printed: not the source's to take
                    if (StillGranted(c, k)) continue;                    // another enchantment holds it up
                    c.Keywords.Remove(k);
                    if (k == Keyword.AEGIS) c.HasAegisShield = false;
                }
            }

            Recompute(c, def);
            return true;
        }

        /// <summary>Remove only continuous aura contributions, preserving ordinary effects even from the same source.</summary>
        public static bool RemoveAuras(CardInstance c, CardDefinition def)
        {
            int removed = 0;
            for (int i = c.Enchantments.Count - 1; i >= 0; i--)
                if (c.Enchantments[i].IsAura)
                {
                    c.Enchantments.RemoveAt(i);
                    removed++;
                }
            if (removed == 0) return false;
            Recompute(c, def);
            return true;
        }

        private static bool IsPrinted(CardDefinition def, Keyword k)
        {
            if (!KeywordSemantics.IsCarrierBehavior(k)) return false;
            // def.Keywords is IReadOnlyList<Keyword>; a hand loop keeps this off LINQ, which the core
            // avoids so the netstandard2.1 surface Unity consumes stays allocation-predictable.
            for (int i = 0; i < def.Keywords.Count; i++)
                if (def.Keywords[i] == k) return true;
            return false;
        }

        private static bool StillGranted(CardInstance c, Keyword k)
        {
            foreach (var e in c.Enchantments)
                if (e.GrantedKeyword != null && e.GrantedKeyword.Value == k) return true;
            return false;
        }

        /// <summary>
        /// The single fold: printed base + every enchantment delta. List order does not matter to the
        /// result — integer addition is commutative — which is what lets an aura entry be pulled out of
        /// the middle without a re-sort, and why the canonical walk can keep writing the list as-is.
        /// </summary>
        private static void ApplyStats(CardInstance c, CardDefinition def, int damageTaken)
        {
            int attack = BaseAttack(def);
            int maxHealth = BaseMaxHealth(def);

            foreach (var e in c.Enchantments)
            {
                attack += e.AttackDelta;
                maxHealth += e.HealthDelta;
            }

            c.CurrentAttack = attack;
            c.MaxHealth = maxHealth;
            c.CurrentHealth = maxHealth - damageTaken;
        }
    }
}
