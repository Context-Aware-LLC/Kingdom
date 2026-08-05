using System;
using System.Collections.Generic;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Interpreter
{
    /// <summary>
    /// L1 — the derived-stat layer (EF-017a). Live stats are a fold over the printed base plus the
    /// enchantment list, so applying and unapplying a contribution are the same operation run twice.
    ///
    /// <para>These reach <c>DerivedStats</c> directly through <c>InternalsVisibleTo</c> because
    /// <see cref="DerivedStats.RemoveBySource"/> has no in-engine caller until AURA lands (EF-017b).
    /// The behaviour-level consequences — BUFF, SILENCE, bounce, board entry — are covered end-to-end
    /// by CardBehaviorTests and the golden replays, which is why this file sticks to the primitive.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DerivedStatsTests
    {
        private static readonly InstanceId SourceA = new InstanceId(101);
        private static readonly InstanceId SourceB = new InstanceId(102);

        private static CardDefinition Def(int attack, int health, params Keyword[] keywords) =>
            new CardDefinition(
                id: "test_minion", name: "Test Minion", type: CardType.MINION, cost: 1,
                attack: attack, health: health, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: keywords, rules: Array.Empty<RuleDefinition>(),
                cardText: "", flavorText: "", artId: "art/test_minion", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", requiresEngineeringPrimitive: false);

        /// <summary>A minion already on the board, at printed stats, undamaged.</summary>
        private static CardInstance Minion(CardDefinition def)
        {
            var c = new CardInstance(new InstanceId(1), def.Id, new PlayerId("P0")) { Zone = Zone.Board };
            DerivedStats.ResetToPrinted(c, def, preserveDamage: false);
            return c;
        }

        private static void Enchant(CardInstance c, InstanceId source, int atk, int hp) =>
            c.Enchantments.Add(new Enchantment { Source = source, AttackDelta = atk, HealthDelta = hp });

        private static void Grant(CardInstance c, InstanceId source, Keyword kw)
        {
            c.Enchantments.Add(new Enchantment { Source = source, GrantedKeyword = kw });
            c.Keywords.Add(kw);
        }

        // ── the fold ────────────────────────────────────────────────────────────────

        [Test]
        public void Printed_Stats_Are_The_Base_With_No_Enchantments()
        {
            var def = Def(2, 3);
            var c = Minion(def);

            Assert.That(c.CurrentAttack, Is.EqualTo(2));
            Assert.That(c.MaxHealth, Is.EqualTo(3));
            Assert.That(c.CurrentHealth, Is.EqualTo(3));
        }

        [Test]
        public void Recompute_Sums_Every_Enchantment_Regardless_Of_Source()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Enchant(c, SourceA, 1, 1);
            Enchant(c, SourceB, 3, 0);
            DerivedStats.Recompute(c, def);

            Assert.That(c.CurrentAttack, Is.EqualTo(6));   // 2 + 1 + 3
            Assert.That(c.MaxHealth, Is.EqualTo(4));       // 3 + 1
            Assert.That(c.CurrentHealth, Is.EqualTo(4));
        }

        [Test]
        public void Recompute_Preserves_Damage_Already_Taken()
        {
            var def = Def(2, 5);
            var c = Minion(def);
            c.CurrentHealth = 2;                            // took 3

            Enchant(c, SourceA, 0, 4);
            DerivedStats.Recompute(c, def);

            Assert.That(c.MaxHealth, Is.EqualTo(9));
            Assert.That(c.CurrentHealth, Is.EqualTo(6), "the 3 damage must survive the health buff");
        }

        [Test]
        public void Recompute_Does_Not_Clamp_A_Negative_Result()
        {
            // Attack and health were free to go negative before this layer existed; adding a floor here
            // would be a silent rules change and would move the state hash.
            var def = Def(1, 1);
            var c = Minion(def);
            Enchant(c, SourceA, -4, -3);
            DerivedStats.Recompute(c, def);

            Assert.That(c.CurrentAttack, Is.EqualTo(-3));
            Assert.That(c.MaxHealth, Is.EqualTo(-2));
            Assert.That(c.CurrentHealth, Is.EqualTo(-2), "≤0 is the DeathCheck's business, not the fold's");
        }

        [Test]
        public void Enchantment_Order_Does_Not_Change_The_Result()
        {
            // What lets an aura entry be pulled out of the middle of the list without a re-sort.
            var def = Def(2, 3);

            var a = Minion(def);
            Enchant(a, SourceA, 1, 4);
            Enchant(a, SourceB, 5, 2);
            DerivedStats.Recompute(a, def);

            var b = Minion(def);
            Enchant(b, SourceB, 5, 2);
            Enchant(b, SourceA, 1, 4);
            DerivedStats.Recompute(b, def);

            Assert.That(b.CurrentAttack, Is.EqualTo(a.CurrentAttack));
            Assert.That(b.MaxHealth, Is.EqualTo(a.MaxHealth));
        }

        // ── unapply: the reason Enchantment.Source is stored ─────────────────────────

        [Test]
        public void RemoveBySource_Takes_Off_Only_That_Sources_Contribution()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Enchant(c, SourceA, 1, 1);
            Enchant(c, SourceB, 3, 2);
            DerivedStats.Recompute(c, def);
            Assert.That(c.CurrentAttack, Is.EqualTo(6));

            Assert.That(DerivedStats.RemoveBySource(c, def, SourceA), Is.True);

            Assert.That(c.CurrentAttack, Is.EqualTo(5));   // 2 + 3
            Assert.That(c.MaxHealth, Is.EqualTo(5));       // 3 + 2
            Assert.That(c.Enchantments.Count, Is.EqualTo(1));
            Assert.That(c.Enchantments[0].Source, Is.EqualTo(SourceB));
        }

        [Test]
        public void RemoveBySource_Removes_Every_Entry_From_That_Source()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Enchant(c, SourceA, 1, 0);
            Enchant(c, SourceA, 1, 0);
            Enchant(c, SourceB, 1, 0);
            DerivedStats.Recompute(c, def);

            DerivedStats.RemoveBySource(c, def, SourceA);

            Assert.That(c.Enchantments.Count, Is.EqualTo(1));
            Assert.That(c.CurrentAttack, Is.EqualTo(3));
        }

        [Test]
        public void RemoveBySource_Reports_False_And_Changes_Nothing_When_The_Source_Never_Touched_It()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Enchant(c, SourceB, 4, 4);
            DerivedStats.Recompute(c, def);

            Assert.That(DerivedStats.RemoveBySource(c, def, SourceA), Is.False);
            Assert.That(c.CurrentAttack, Is.EqualTo(6));
            Assert.That(c.Enchantments.Count, Is.EqualTo(1));
        }

        [Test]
        public void RemoveBySource_Preserves_Damage()
        {
            var def = Def(2, 5);
            var c = Minion(def);
            Enchant(c, SourceA, 0, 4);
            DerivedStats.Recompute(c, def);
            c.CurrentHealth = 6;                            // took 3 off a 9-health body

            DerivedStats.RemoveBySource(c, def, SourceA);

            Assert.That(c.MaxHealth, Is.EqualTo(5));
            Assert.That(c.CurrentHealth, Is.EqualTo(2), "still wounded by 3 after the buff falls away");
        }

        // ── keywords: subtracted, never re-seeded ────────────────────────────────────

        [Test]
        public void RemoveBySource_Drops_A_Keyword_That_Source_Granted()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Grant(c, SourceA, Keyword.BULWARK);

            DerivedStats.RemoveBySource(c, def, SourceA);

            Assert.That(c.Keywords, Does.Not.Contain(Keyword.BULWARK));
        }

        [Test]
        public void RemoveBySource_Keeps_A_Keyword_Another_Enchantment_Still_Grants()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Grant(c, SourceA, Keyword.BULWARK);
            Grant(c, SourceB, Keyword.BULWARK);

            DerivedStats.RemoveBySource(c, def, SourceA);

            Assert.That(c.Keywords, Does.Contain(Keyword.BULWARK), "SourceB is still holding it up");
        }

        [Test]
        public void RemoveBySource_Never_Strips_A_Printed_Keyword()
        {
            // The source granted BULWARK redundantly; the card was printed with it. Taking the grant away
            // must not take away what was never the source's to give.
            var def = Def(2, 3, Keyword.BULWARK);
            var c = Minion(def);
            Grant(c, SourceA, Keyword.BULWARK);

            DerivedStats.RemoveBySource(c, def, SourceA);

            Assert.That(c.Keywords, Does.Contain(Keyword.BULWARK));
        }

        [Test]
        public void Recompute_Does_Not_Refund_A_Spent_Aegis()
        {
            // HasAegisShield is SPENT state, not derived. Re-seeding it on every buff would hand the
            // minion a fresh shield each time it was pumped.
            var def = Def(2, 3, Keyword.AEGIS);
            var c = Minion(def);
            Assert.That(c.HasAegisShield, Is.True);

            c.HasAegisShield = false;                       // absorbed a hit
            Enchant(c, SourceA, 2, 2);
            DerivedStats.Recompute(c, def);

            Assert.That(c.HasAegisShield, Is.False);
        }

        [Test]
        public void Recompute_Does_Not_Restore_Shroud_Broken_By_Attacking()
        {
            // Same shape as the Aegis case: combat strips SHROUD, and a later buff must not put it back.
            var def = Def(2, 3, Keyword.SHROUD);
            var c = Minion(def);
            c.Keywords.Remove(Keyword.SHROUD);             // attacked

            Enchant(c, SourceA, 1, 1);
            DerivedStats.Recompute(c, def);

            Assert.That(c.Keywords, Does.Not.Contain(Keyword.SHROUD));
        }

        // ── full reset ───────────────────────────────────────────────────────────────

        [Test]
        public void ResetToPrinted_With_PreserveDamage_Is_The_Silence_Shape()
        {
            var def = Def(1, 1);
            var c = Minion(def);
            Enchant(c, SourceA, 4, 4);
            Grant(c, SourceB, Keyword.BULWARK);
            DerivedStats.Recompute(c, def);
            c.CurrentHealth = 2;                            // 5 health, took 3

            DerivedStats.ResetToPrinted(c, def, preserveDamage: true);

            Assert.That(c.Enchantments, Is.Empty);
            Assert.That(c.Keywords, Does.Not.Contain(Keyword.BULWARK));
            Assert.That(c.CurrentAttack, Is.EqualTo(1));
            Assert.That(c.MaxHealth, Is.EqualTo(1));
            Assert.That(c.CurrentHealth, Is.EqualTo(-2), "printed body is already dead — DeathCheck's call");
        }

        [Test]
        public void ResetToPrinted_Without_PreserveDamage_Is_The_Bounce_Shape()
        {
            var def = Def(2, 5);
            var c = Minion(def);
            c.CurrentHealth = 1;
            c.IsFrosted = true;

            DerivedStats.ResetToPrinted(c, def, preserveDamage: false);

            Assert.That(c.CurrentHealth, Is.EqualTo(5), "the card arrives whole; no wound crosses the zone change");
            Assert.That(c.IsFrosted, Is.False);
        }

        [Test]
        public void ResetToPrinted_Restores_Aegis_From_The_Printed_Keyword_Set()
        {
            // Unlike Recompute, a reset DOES re-seed the shield — a silence or a bounce genuinely does
            // return the card to its printed form.
            var def = Def(2, 3, Keyword.AEGIS);
            var c = Minion(def);
            c.HasAegisShield = false;

            DerivedStats.ResetToPrinted(c, def, preserveDamage: true);

            Assert.That(c.HasAegisShield, Is.True);
        }

        [Test]
        public void ResetToPrinted_Clears_Aegis_When_It_Was_Only_Granted()
        {
            var def = Def(2, 3);
            var c = Minion(def);
            Grant(c, SourceA, Keyword.AEGIS);
            c.HasAegisShield = true;

            DerivedStats.ResetToPrinted(c, def, preserveDamage: true);

            Assert.That(c.HasAegisShield, Is.False);
            Assert.That(c.Keywords, Does.Not.Contain(Keyword.AEGIS));
        }

        [Test]
        public void ResetToSilenced_Removes_Printed_And_Granted_Keywords_But_Keeps_Damage()
        {
            var def = Def(2, 5, Keyword.AEGIS, Keyword.LIFEDRAIN);
            var c = Minion(def);
            Enchant(c, SourceA, 3, 4);
            Grant(c, SourceB, Keyword.BULWARK);
            DerivedStats.Recompute(c, def);
            c.CurrentHealth = 6; // seven-health body has taken three damage.
            c.IsFrosted = true;

            DerivedStats.ResetToSilenced(c, def, preserveDamage: true);

            Assert.That(c.Enchantments, Is.Empty);
            Assert.That(c.Keywords, Is.Empty, "silence removes printed and granted riders alike");
            Assert.That(c.CurrentAttack, Is.EqualTo(2));
            Assert.That((c.MaxHealth, c.CurrentHealth), Is.EqualTo((5, 2)));
            Assert.That(c.HasAegisShield, Is.False);
            Assert.That(c.IsFrosted, Is.False);
        }

        // ── the invariant the layer exists to make true ──────────────────────────────

        [Test]
        public void Apply_Then_Unapply_Returns_Exactly_To_The_Prior_State()
        {
            // This is the whole prereq for AURA: a contribution that can come back off cleanly, which
            // the destructive `CurrentAttack += atk` bake could never offer.
            var def = Def(3, 4);
            var c = Minion(def);
            Enchant(c, SourceB, 2, 2);
            DerivedStats.Recompute(c, def);
            c.CurrentHealth = 4;                            // took 2

            int atkBefore = c.CurrentAttack, maxBefore = c.MaxHealth, curBefore = c.CurrentHealth;
            var listBefore = new List<InstanceId>();
            foreach (var e in c.Enchantments) listBefore.Add(e.Source);

            Enchant(c, SourceA, 5, 5);
            DerivedStats.Recompute(c, def);
            Assert.That(c.CurrentAttack, Is.Not.EqualTo(atkBefore), "guard: the aura actually did something");

            DerivedStats.RemoveBySource(c, def, SourceA);

            Assert.That(c.CurrentAttack, Is.EqualTo(atkBefore));
            Assert.That(c.MaxHealth, Is.EqualTo(maxBefore));
            Assert.That(c.CurrentHealth, Is.EqualTo(curBefore));
            Assert.That(c.Enchantments.Count, Is.EqualTo(listBefore.Count));
            for (int i = 0; i < listBefore.Count; i++)
                Assert.That(c.Enchantments[i].Source, Is.EqualTo(listBefore[i]));
        }
    }
}
