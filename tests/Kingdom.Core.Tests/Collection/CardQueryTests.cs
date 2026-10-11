using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="CardQuery"/> — the deckbuilder's collection-grid search (EF-035), the layer that
    /// gates the deckbuilder screen (EF-046). Exercised against the <b>live</b> M1 pool so a facet that
    /// stops matching a real card fails here, not in the UI.
    ///
    /// <para>Nothing hardcodes a card id. Every expectation is a predicate over the same pool the query
    /// reads, so a balance patch that renames or recosts a card cannot turn these red for the wrong
    /// reason — a genuine query regression still can.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class CardQueryTests
    {
        private static List<CardDefinition> Pool()
            => LiveCardSet.Cards.Where(c => c.PoolEligible).ToList();

        private static ICardDatabase Db => LiveCardSet.Db;

        // ── the pool / empty filter ────────────────────────────────────────────────

        [Test]
        public void Empty_Filter_Returns_The_Whole_Collectible_Pool()
        {
            var result = CardQuery.Search(Db, new CardFilter());
            Assert.That(result.Count, Is.EqualTo(Pool().Count));
            Assert.That(result.All(c => c.Collectible), Is.True, "the grid never offers a non-collectible token.");
        }

        [Test]
        public void A_Null_Filter_And_A_Null_Pool_Are_Both_Total_And_Empty()
        {
            Assert.That(CardQuery.Search(Db, null).Count, Is.EqualTo(Pool().Count), "null filter = no constraint.");
            Assert.That(CardQuery.Search((IEnumerable<CardDefinition>)null!).Count, Is.EqualTo(0));
        }

        // ── per-facet ──────────────────────────────────────────────────────────────

        [Test]
        public void Class_Facet_Returns_Exactly_That_Class()
        {
            var cls = Pool().Select(c => c.Class).First(c => c != CardClass.NEUTRAL);
            var result = CardQuery.Search(Db, new CardFilter { Classes = new[] { cls } });

            Assert.That(result.All(c => c.Class == cls), Is.True);
            Assert.That(result.Select(c => c.Id).ToHashSet(),
                Is.EquivalentTo(Pool().Where(c => c.Class == cls).Select(c => c.Id)));
        }

        [Test]
        public void A_Multi_Value_Facet_Is_A_Union_Within_The_Facet()
        {
            var classes = Pool().Select(c => c.Class).Distinct().Take(2).ToArray();
            Assume.That(classes.Length, Is.EqualTo(2), "need two distinct classes in the pool.");

            var result = CardQuery.Search(Db, new CardFilter { Classes = classes });
            Assert.That(result.All(c => classes.Contains(c.Class)), Is.True);
            Assert.That(result.Select(c => c.Id).ToHashSet(),
                Is.EquivalentTo(Pool().Where(c => classes.Contains(c.Class)).Select(c => c.Id)));
        }

        [Test]
        public void Rarity_Facet_Returns_Exactly_That_Rarity()
        {
            var rarity = Pool()[0].Rarity;
            var result = CardQuery.Search(Db, new CardFilter { Rarities = new[] { rarity } });

            Assert.That(result.All(c => c.Rarity == rarity), Is.True);
            Assert.That(result.Count, Is.EqualTo(Pool().Count(c => c.Rarity == rarity)));
        }

        [Test]
        public void Type_Facet_Returns_Exactly_That_Type()
        {
            var type = Pool()[0].Type;
            var result = CardQuery.Search(Db, new CardFilter { Types = new[] { type } });

            Assert.That(result.All(c => c.Type == type), Is.True);
            Assert.That(result.Count, Is.EqualTo(Pool().Count(c => c.Type == type)));
        }

        [Test]
        public void Tribe_Facet_Treats_A_Missing_Tribe_As_None()
        {
            var withTribe = Pool().Where(c => (c.Tribe ?? Tribe.NONE) != Tribe.NONE).ToList();
            Assume.That(withTribe, Is.Not.Empty, "live pool has no tribal card to filter on.");
            var tribe = withTribe[0].Tribe!.Value;

            var result = CardQuery.Search(Db, new CardFilter { Tribes = new[] { tribe } });
            Assert.That(result.All(c => (c.Tribe ?? Tribe.NONE) == tribe), Is.True);
            Assert.That(result.Count, Is.EqualTo(Pool().Count(c => (c.Tribe ?? Tribe.NONE) == tribe)));
        }

        [Test]
        public void Keyword_Facet_Returns_Cards_Carrying_Any_Selected_Keyword()
        {
            var kw = Pool().SelectMany(c => c.Keywords).FirstOrDefault();
            Assume.That(Pool().Any(c => c.Keywords.Count > 0), Is.True, "live pool has no keyworded card.");

            var result = CardQuery.Search(Db, new CardFilter { Keywords = new[] { kw } });
            Assert.That(result.All(c => c.Keywords.Contains(kw)), Is.True);
            Assert.That(result.Count, Is.EqualTo(Pool().Count(c => c.Keywords.Contains(kw))));
        }

        [Test]
        public void Cost_Range_Is_Inclusive_On_Both_Ends()
        {
            var byCost = CardQuery.Search(Db, new CardFilter { MinCost = 2, MaxCost = 4 });
            Assert.That(byCost.All(c => c.Cost >= 2 && c.Cost <= 4), Is.True);
            Assert.That(byCost.Count, Is.EqualTo(Pool().Count(c => c.Cost >= 2 && c.Cost <= 4)));

            // The "7+" bucket the mana rail's last button maps to.
            var topEnd = CardQuery.Search(Db, new CardFilter { MinCost = 7 });
            Assert.That(topEnd.Count, Is.EqualTo(Pool().Count(c => c.Cost >= 7)));
        }

        [Test]
        public void Facets_Combine_As_And_Across_Facets()
        {
            var cls = Pool().Select(c => c.Class).First(c => c != CardClass.NEUTRAL);
            var result = CardQuery.Search(Db, new CardFilter { Classes = new[] { cls }, MaxCost = 3 });
            Assert.That(result.All(c => c.Class == cls && c.Cost <= 3), Is.True);
            Assert.That(result.Count, Is.EqualTo(Pool().Count(c => c.Class == cls && c.Cost <= 3)));
        }

        // ── free text: the five-field union ─────────────────────────────────────────

        [Test]
        public void Free_Text_Matches_By_Name()
        {
            var card = Pool().First(c => Tokens(c.Name).Any());
            string token = Tokens(card.Name).OrderByDescending(t => t.Length).First();

            var ids = CardQuery.Search(Db, new CardFilter { Text = token }).Select(c => c.Id);
            Assert.That(ids, Does.Contain(card.Id), $"'{token}' is a word in {card.Name}.");
        }

        [Test]
        public void Free_Text_Searches_Rules_Text_Not_Just_The_Name()
        {
            // A word that lives in some card's rules and NOT in its name — so a query that returns it
            // proves the rules field is in the union, not the name.
            CardDefinition? found = null;
            string chosen = "";
            foreach (var c in Pool())
            {
                var nameWords = Tokens(c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rulesOnly = Tokens(CardTextGenerator.Generate(c, CardTextGenerator.NoPool.Instance)).FirstOrDefault(
                    w => w.Length >= 4 && !nameWords.Contains(w));
                if (rulesOnly != null) { found = c; chosen = rulesOnly; break; }
            }
            Assume.That(found, Is.Not.Null, "no card has a >=4-letter rules word absent from its name.");

            var ids = CardQuery.Search(Db, new CardFilter { Text = chosen }).Select(c => c.Id).ToHashSet();
            Assert.That(ids, Does.Contain(found!.Id),
                $"'{chosen}' is in {found.Name}'s rules but not its name — rules must be searched.");
        }

        [Test]
        public void Free_Text_Searches_The_Type_Name()
        {
            // Every SPELL's type name contains "spell"; searching it must return all of them (union may
            // add minions whose rules say "spell", which is correct, so this is a superset assertion).
            var spells = Pool().Where(c => c.Type == CardType.SPELL).Select(c => c.Id).ToList();
            Assume.That(spells, Is.Not.Empty, "live pool has no spell.");

            var ids = CardQuery.Search(Db, new CardFilter { Text = "spell" }).Select(c => c.Id).ToHashSet();
            Assert.That(spells.All(ids.Contains), Is.True, "type-name token must reach every spell.");
        }

        [Test]
        public void Free_Text_Searches_The_Tribe_Name()
        {
            var tribal = Pool().Where(c => (c.Tribe ?? Tribe.NONE) != Tribe.NONE).ToList();
            Assume.That(tribal, Is.Not.Empty, "live pool has no tribal card.");
            var tribe = tribal[0].Tribe!.Value;

            var ids = CardQuery.Search(Db, new CardFilter { Text = tribe.ToString() }).Select(c => c.Id).ToHashSet();
            Assert.That(Pool().Where(c => (c.Tribe ?? Tribe.NONE) == tribe).All(c => ids.Contains(c.Id)), Is.True,
                $"tribe-name token '{tribe}' must reach every {tribe}.");
        }

        [Test]
        public void Free_Text_Searches_Keyword_Names()
        {
            // A card that carries a keyword whose name is in NEITHER its card name nor its rules text —
            // so only the keyword field can produce the match.
            CardDefinition? found = null;
            Keyword chosen = default;
            foreach (var c in Pool())
                foreach (var kw in c.Keywords)
                {
                    string name = kw.ToString();
                    bool inName = c.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                    // c.Text -- the AUTHORED field -- because that is the one the haystack searches.
                    // This used to render the rules through CardTextGenerator against NoPool, which
                    // measured a string the search never reads and threw outright on the first live
                    // card whose rule NAMES another card: NoPool refuses to resolve one, on purpose.
                    bool inRules = c.Text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!inName && !inRules) { found = c; chosen = kw; break; }
                }
            Assume.That(found, Is.Not.Null, "every keyworded card also spells the keyword in its name/rules.");

            var ids = CardQuery.Search(Db, new CardFilter { Text = chosen.ToString() }).Select(c => c.Id).ToHashSet();
            Assert.That(ids, Does.Contain(found!.Id), $"keyword '{chosen}' must be searchable on {found.Name}.");
        }

        [Test]
        public void Free_Text_Is_An_And_Of_Tokens()
        {
            var card = Pool().First(c => Tokens(c.Name).Any());
            string real = Tokens(card.Name).OrderByDescending(t => t.Length).First();

            // Real token alone hits; real token AND a nonsense token hits nothing.
            Assert.That(CardQuery.Search(Db, new CardFilter { Text = real }).Count, Is.GreaterThan(0));
            Assert.That(CardQuery.Search(Db, new CardFilter { Text = real + " zzqqxx" }).Count, Is.EqualTo(0),
                "a second, unmatchable token must remove every result — tokens are ANDed.");
        }

        [Test]
        public void Free_Text_Ignores_Flavor_And_Id()
        {
            // The union is the five documented fields; a token that lives only in flavour or the id must
            // not conjure a match, or the grid searches surfaces the player never reads. Built
            // synthetically so the proof does not depend on the live set happening to contain a
            // flavour-only word.
            var card = Card(FixtureCard.Id("zzid-quibbleflux"), "Cinder Sprite",
                            flavorText: "The wispwort blooms at dusk.");
            var pool = new[] { card };

            Assert.That(CardQuery.Search(pool, new CardFilter { Text = "wispwort" }).Count, Is.EqualTo(0),
                "'wispwort' lives only in flavour — flavour is not searched.");
            Assert.That(CardQuery.Search(pool, new CardFilter { Text = "quibbleflux" }).Count, Is.EqualTo(0),
                "'quibbleflux' lives only in the id — the id is not searched.");
            Assert.That(CardQuery.Search(pool, new CardFilter { Text = "cinder" }).Count, Is.EqualTo(1),
                "the name IS searched, so the same card is reachable by a name token.");
            Assert.That(CardQuery.Search(pool, new CardFilter { Text = "damage" }).Count, Is.EqualTo(1),
                "the authored rules text IS searched.");
        }

        /// <summary>A minimal spell for the synthetic exclusion test — only the searched fields vary.</summary>
        /// <summary>
        /// Authors <c>text</c> as well as the rule. The haystack searches the AUTHORED text field,
        /// not text generated from the rules, so a card carrying a DamageAction and an empty `text`
        /// is unreachable by "damage" -- and is also a card that cannot ship, since C26-TEXT-MISSING
        /// rejects rules with no authored text. Leaving it blank modelled a state the corpus forbids.
        /// </summary>
        private static CardDefinition Card(int id, string name, string flavorText,
            string text = "Deal 2 damage to the enemy hero.")
            => new CardDefinition(id, FixtureCard.Handle(id), name, CardType.SPELL, cost: 1, attack: null, health: null, durability: null,
                @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
                keywords: Array.Empty<Keyword>(), rules: new[]
                {
                    new RuleDefinition(new RuleId("damage"), new TriggeredActivation(Trigger.ON_CAST),
                        AlwaysCondition.Instance, OneShotLifetime.Instance,
                        new DamageAction(2, new TargetSpec("ENEMY_HERO", TargetSelection.Of(1))))
                },
                flavorText: flavorText, artId: "", setId: SetId.CORE,
                collectible: true, tribe: null, factionId: "", tooltip: "", text: text);

        // ── sort ──────────────────────────────────────────────────────────────────

        [Test]
        public void Default_Sort_Is_Cost_Ascending()
        {
            var costs = CardQuery.Search(Db).Select(c => c.Cost).ToList();
            Assert.That(costs, Is.Ordered.Ascending);
        }

        [Test]
        public void Descending_Sort_Reverses_The_Primary_Key()
        {
            var costs = CardQuery.Search(Db, null, new CardSort(CardSortKey.Cost, descending: true))
                .Select(c => c.Cost).ToList();
            Assert.That(costs, Is.Ordered.Descending);
        }

        [Test]
        public void Sort_Is_A_Deterministic_Total_Order_Independent_Of_Input_Order()
        {
            var pool = Pool();
            var forward = CardQuery.Search(pool).Select(c => c.Id).ToList();
            var reversed = CardQuery.Search(Enumerable.Reverse(pool)).Select(c => c.Id).ToList();

            Assert.That(reversed, Is.EqualTo(forward),
                "the sort's Name/Id tiebreak must make output order independent of enumeration order.");

            // No two adjacent same-cost cards are out of (Name, Id) order — the tiebreak is real.
            var sorted = CardQuery.Search(pool);
            for (int i = 1; i < sorted.Count; i++)
                if (sorted[i - 1].Cost == sorted[i].Cost)
                {
                    int byName = string.CompareOrdinal(sorted[i - 1].Name, sorted[i].Name);
                    int cmp = byName != 0 ? byName : sorted[i - 1].Id.CompareTo(sorted[i].Id);
                    Assert.That(cmp, Is.LessThanOrEqualTo(0), $"tiebreak broken at index {i}.");
                }
        }

        [Test]
        public void Sort_By_Name_Orders_Alphabetically()
        {
            var names = CardQuery.Search(Db, null, new CardSort(CardSortKey.Name)).Select(c => c.Name).ToList();
            Assert.That(names, Is.Ordered.Ascending.Using((System.Collections.IComparer)StringComparer.Ordinal));
        }

        // ── helpers ─────────────────────────────────────────────────────────────────

        private static IEnumerable<string> Tokens(string s)
            => (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => new string(w.Where(char.IsLetter).ToArray()))
                        .Where(w => w.Length > 0);
    }
}
