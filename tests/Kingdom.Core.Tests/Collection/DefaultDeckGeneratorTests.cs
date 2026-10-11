using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="DefaultDeckGenerator"/> — the placeholder starter deck a new Character opens on
    /// (Sam, 2026-07-26), against the live M1 pool.
    ///
    /// <para>Three properties are the whole contract and each is asserted rather than assumed: the
    /// output passes L1-L5, the curve is actually biased cheap, and the same seed produces the same
    /// deck. The fourth — a thin pool fails loudly — is the one that cannot be provoked from the live
    /// set today, so it gets a deliberately starved database.</para>
    ///
    /// <para><b>The fifth arrived from a live failure (2026-09-03): the deck has to be one the
    /// player can QUEUE with.</b> Passing L1-L5 is not that. The generator drew from the whole
    /// collectible pool while a new account is granted only the CORE+PENDINGHAM commons and rares,
    /// so 31-46% of every pairing's draw weight was a card the player had never been given — and the
    /// deck it handed them died at the socket with "deck rejected: You don't own Repair Drone."
    /// <see cref="Every_Pairing_Generates_A_Deck_The_Signup_Grant_Can_Queue_With"/> is that failure
    /// mode, asserted against the real grant rather than a literal.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class DefaultDeckGeneratorTests
    {
        /// <summary>
        /// Two classes and a label. It was a <c>Character</c> until ADR-0009, which is exactly the
        /// point: the container held nothing else the generator ever read, and this struct is what
        /// was left when it was removed. The <c>Name</c> is here only so a sweep's assertion message
        /// can say which pairing failed.
        /// </summary>
        private readonly struct Pairing
        {
            public readonly CardClass Primary;
            public readonly CardClass Secondary;

            public Pairing(CardClass primary, CardClass secondary)
            {
                Primary = primary;
                Secondary = secondary;
            }

            public string Name => $"{Primary}/{Secondary}";

            public bool AllowsClass(CardClass c) => DeckList.AllowsClass(Primary, Secondary, c);
        }

        private static Pairing Rotbloom() => new Pairing(CardClass.GRAVEKEEP, CardClass.VERDANT);
        private static DeterministicRandom Rng(ulong seed = 1234) => new DeterministicRandom(seed);

        /// <summary>Every pairing the live set can express, so no class combination is silently untested.</summary>
        private static IEnumerable<Pairing> AllPairings()
        {
            var classes = Enum.GetValues(typeof(CardClass)).Cast<CardClass>().Where(c => c != CardClass.NEUTRAL);
            foreach (var p in classes)
                foreach (var s in classes)
                    if (p != s) yield return new Pairing(p, s);
        }

        [Test]
        public void Every_Pairing_Generates_A_Deck_That_Passes_L1_Through_L5()
        {
            foreach (var character in AllPairings())
            {
                var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(), "deck-1");
                var result = DeckValidator.Validate(deck, LiveCardSet.Db);

                Assert.That(deck.TotalCount, Is.EqualTo(DeckValidator.DeckSize), character.Name);
                Assert.That(result.IsValid, Is.True,
                    $"{character.Name}: " + string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Message}")));
                Assert.That((deck.PrimaryClass, deck.SecondaryClass),
                    Is.EqualTo((character.Primary, character.Secondary)),
                    "The generated deck carries the pairing it was generated for. This was an L5 " +
                    "BINDING assertion until ADR-0009 — deck.CharacterId pointing back at the " +
                    "character. The deck holds the pairing itself now, so there is nothing to bind.");
            }
        }

        [Test]
        public void Generated_Cards_Are_Collectible_And_Inside_The_Pairing()
        {
            // L4 passing is not quite enough: tokens and hero-power rows are NEUTRAL, so a generator
            // that ignored Collectible would satisfy L4 while dealing the player a Spark Coin.
            var character = Rotbloom();
            var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(), "deck-1");

            foreach (var entry in deck.Entries)
            {
                var def = LiveCardSet.Db.Get(entry.CardId);
                Assert.That(def.Collectible, Is.True, $"{def.Id} is not a collectible card.");
                Assert.That(character.AllowsClass(def.Class), Is.True, $"{def.Id} is {def.Class}.");
                Assert.That(entry.Count, Is.InRange(1, DeckValidator.MaxCopies(def.Rarity)), def.Id.ToString());
            }
        }

        [Test]
        public void The_Same_Seed_Produces_The_Same_Deck()
        {
            var character = Rotbloom();

            var a = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(99), "deck-1");
            var b = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(99), "deck-1");

            Assert.That(b.Entries.Select(e => (e.CardId, e.Count)),
                Is.EqualTo(a.Entries.Select(e => (e.CardId, e.Count))),
                "Entry order is candidate order, not draw order, so the same seed writes an identical file.");
        }

        [Test]
        public void Different_Seeds_Produce_Different_Decks()
        {
            // Guards the boring failure where the weighting collapses and every deck is the same 30.
            var character = Rotbloom();

            var a = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(1), "deck-1");
            var b = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(2), "deck-1");

            Assert.That(b.Entries.Select(e => (e.CardId, e.Count)),
                Is.Not.EqualTo(a.Entries.Select(e => (e.CardId, e.Count))));
        }

        [Test]
        public void Entries_Are_Ordered_And_Never_Duplicated()
        {
            var deck = DefaultDeckGenerator.Generate(Rotbloom().Primary, Rotbloom().Secondary, LiveCardSet.Db, Rng(), "deck-1");
            var ids = deck.Entries.Select(e => e.CardId).ToList();

            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count),
                "Copies live in DeckEntry.Count — a second row for the same id would dodge the L2/L3 cap.");
            Assert.That(ids, Is.Ordered.Using((System.Collections.IComparer)StringComparer.Ordinal));
        }

        // ---- the curve -----------------------------------------------------------------

        [Test]
        public void The_Draw_Is_Weighted_Toward_Cheap_Cards()
        {
            // The property that makes the deck playable. Measured against the pool it was drawn from,
            // because "average cost under N" would just be a fact about M1's costs, not about the
            // weighting. Averages are scaled by 100 to stay integer — this assembly is float-free.
            var character = Rotbloom();
            var pool = LiveCardSet.Cards.Where(c => c.Collectible && character.AllowsClass(c.Class)).ToList();
            int poolAvg100 = pool.Sum(c => c.Cost) * 100 / pool.Count;

            var costs = new List<int>();
            for (ulong seed = 0; seed < 25; seed++)
            {
                var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, new DeterministicRandom(seed), "d");
                foreach (var e in deck.Entries)
                    for (int i = 0; i < e.Count; i++) costs.Add(LiveCardSet.Db.Get(e.CardId).Cost);
            }
            int deckAvg100 = costs.Sum() * 100 / costs.Count;

            Assert.That(deckAvg100, Is.LessThan(poolAvg100),
                $"Deck average {deckAvg100 / 100.0} is not cheaper than the pool's {poolAvg100 / 100.0} — " +
                "a uniform draw would produce a curve of sevens.");
            Assert.That(costs.Count(c => c <= 3) * 100 / costs.Count, Is.GreaterThan(40),
                "A playable opening needs early plays, not just a lower mean.");
        }

        [Test]
        public void Weight_Falls_With_Cost_And_Never_Reaches_Zero()
        {
            // A zero weight would make a card unreachable, and a pool that needs its expensive cards
            // to reach 30 would then loop forever rather than fail.
            for (int cost = 0; cost < 12; cost++)
            {
                Assert.That(DefaultDeckGenerator.Weight(cost), Is.GreaterThan(0), $"cost {cost}");
                if (cost > 0 && cost < 8)
                    Assert.That(DefaultDeckGenerator.Weight(cost), Is.LessThan(DefaultDeckGenerator.Weight(cost - 1)));
            }
        }

        // ---- the thin pool -------------------------------------------------------------

        [Test]
        public void A_Pool_That_Cannot_Reach_Thirty_Fails_Loudly()
        {
            // Sam's constraint: never hand back a 27-card deck that dies on save. Not provokable from
            // the live set — the thinnest real pairing is EMBERWARD/TIDECALLER, and it still clears
            // 30 because every character also gets NEUTRAL — so the pool is starved deliberately.
            var character = Rotbloom();
            var starved = new FixedCardDatabase(
                LiveCardSet.Collectible(CardClass.GRAVEKEEP).Take(5));   // 5 rows, at most 10 copies

            Assert.That(DefaultDeckGenerator.CanGenerate(character.Primary, character.Secondary, starved), Is.False);

            var ex = Assert.Throws<DeckGenerationException>(
                () => DefaultDeckGenerator.Generate(character.Primary, character.Secondary, starved, Rng(), "deck-1"));
            Assert.That(ex!.Message, Does.Contain("30"));
        }

        [Test]
        public void CanGenerate_Is_True_For_Every_Real_Pairing_Today()
        {
            // Doubles as a content tripwire: if a future set edit drops a class below a buildable
            // pool, this fails here rather than at a player's Create button.
            foreach (var character in AllPairings())
                Assert.That(DefaultDeckGenerator.CanGenerate(character.Primary, character.Secondary, LiveCardSet.Db), Is.True, character.Name);
        }

        // ---- ownership: the deck has to be one this account can actually queue -----------

        /// <summary>What a freshly signed-up account holds — one copy of each card the grant lists.</summary>
        private static OwnedCardCounts SignupGrant()
        {
            var owned = new OwnedCardCounts();
            OnboardingGrant.ApplyGrantedCards(OnboardingGrant.Resolve(LiveCardSet.Db), owned);
            return owned;
        }

        [Test]
        public void Every_Pairing_Generates_A_Deck_The_Signup_Grant_Can_Queue_With()
        {
            // THE failure mode, not a property. A starter deck that is legal but unowned is rejected
            // by the server at match start, which is the furthest possible point from where it was
            // built and the one place the player can do nothing about it.
            var owned = SignupGrant();

            foreach (var character in AllPairings())
            {
                var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(), "deck-1", null, owned);
                var queue = QueueEligibility.Check(deck, LiveCardSet.Db, owned);

                Assert.That(queue.IsEligible, Is.True, $"{character.Name}: {queue.Reason}");
            }
        }

        [Test]
        public void No_Entry_Exceeds_The_Copies_The_Account_Owns()
        {
            // The cap is min(format cap, copies held), so a common owned once may appear once even
            // though L2 would allow two. Asserted per entry rather than trusting the validator, since
            // the validator is the thing this generator has to satisfy.
            var owned = SignupGrant();
            var character = Rotbloom();

            var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(7), "deck-1", null, owned);

            foreach (var entry in deck.Entries)
                Assert.That(entry.Count, Is.LessThanOrEqualTo(owned.CopiesOwned(entry.CardId)),
                    $"card {entry.CardId}: drew {entry.Count}, owns {owned.CopiesOwned(entry.CardId)}");
        }

        [Test]
        public void A_Null_Ownership_Record_Still_Means_Do_Not_Constrain()
        {
            // The reading IOwnedCardStore.Load's null carries everywhere else: unknown, not empty.
            // Soak runs, bots and tests generate against no record and must keep the whole pool —
            // and the two calls must agree, or "unknown" would quietly mean two different pools.
            var character = Rotbloom();

            var implicitNull = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(5), "deck-1");
            var explicitNull = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(5), "deck-1", null, null);

            Assert.That(explicitNull.Entries.Select(e => (e.CardId, e.Count)),
                Is.EqualTo(implicitNull.Entries.Select(e => (e.CardId, e.Count))));
            var grant = SignupGrant();
            Assert.That(implicitNull.Entries.Any(e => grant.CopiesOwned(e.CardId) == 0), Is.True,
                "The unconstrained draw should still reach cards the signup grant does not include — " +
                "if it no longer can, this test has stopped guarding anything.");
        }

        [Test]
        public void An_Account_That_Owns_Too_Little_Is_Refused_Up_Front_And_Says_Why()
        {
            // The create affordance asks CanGenerate before offering "Generated", so an ownership
            // shortfall has to answer false there rather than throwing halfway through creation —
            // and the message has to name ownership, or the reader goes hunting in the card data.
            var character = Rotbloom();
            var thin = new OwnedCardCounts();
            foreach (var def in LiveCardSet.Collectible(CardClass.GRAVEKEEP).Take(5))
                thin.Set(def.Id, 1);

            Assert.That(DefaultDeckGenerator.CanGenerate(character.Primary, character.Secondary, LiveCardSet.Db, thin), Is.False);
            Assert.That(DefaultDeckGenerator.CanGenerate(character.Primary, character.Secondary, LiveCardSet.Db), Is.True,
                "The same pairing generates fine unconstrained — the shortfall is ownership, not the pool.");

            var ex = Assert.Throws<DeckGenerationException>(
                () => DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db, Rng(), "deck-1", null, thin));
            Assert.That(ex!.Message, Does.Contain("OWNERSHIP"));
        }

        [Test]
        public void An_Empty_Ownership_Record_Draws_Nothing_Rather_Than_Everything()
        {
            // Present-and-empty means "owns nothing", which is a different statement from null. A
            // generator that folded the two together would hand the full pool to the one account that
            // provably cannot play any of it.
            Assert.That(DefaultDeckGenerator.CanGenerate(Rotbloom().Primary, Rotbloom().Secondary, LiveCardSet.Db, new OwnedCardCounts()),
                Is.False);
        }

        // ---- maxSecondaryCopies: NetSoak's class-pool measurement knob --------------------

        [Test]
        public void MaxSecondaryCopies_Zero_Excludes_The_Secondary_Class_Entirely()
        {
            var character = Rotbloom();
            var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db,
                Rng(11), "deck-1", null, null, maxSecondaryCopies: 0);

            Assert.That(deck.Entries.Any(e => LiveCardSet.Db.Get(e.CardId).Class == character.Secondary), Is.False);
        }

        [Test]
        public void MaxSecondaryCopies_Caps_The_Secondary_Classs_Total_Contribution()
        {
            var character = Rotbloom();
            var deck = DefaultDeckGenerator.Generate(character.Primary, character.Secondary, LiveCardSet.Db,
                Rng(11), "deck-1", null, null, maxSecondaryCopies: 5);

            int secondaryCopies = deck.Entries
                .Where(e => LiveCardSet.Db.Get(e.CardId).Class == character.Secondary)
                .Sum(e => e.Count);

            Assert.That(secondaryCopies, Is.LessThanOrEqualTo(5));
            Assert.That(deck.TotalCount, Is.EqualTo(DeckValidator.DeckSize), "still a full, legal 30");
        }

        [Test]
        public void Generate_Refuses_Arguments_It_Cannot_Honour()
        {
            var c = Rotbloom();
            Assert.Throws<ArgumentNullException>(
                () => DefaultDeckGenerator.Generate(c.Primary, c.Secondary, null!, Rng(), "d"));
            Assert.Throws<ArgumentNullException>(
                () => DefaultDeckGenerator.Generate(c.Primary, c.Secondary, LiveCardSet.Db, null!, "d"));
            Assert.Throws<ArgumentException>(
                () => DefaultDeckGenerator.Generate(c.Primary, c.Secondary, LiveCardSet.Db, Rng(), ""));

            // The pairing replaced the null-Character case (ADR-0009): a class cannot be null, so
            // what can now be wrong about it is that it is not two distinct real classes.
            Assert.Throws<ArgumentException>(
                () => DefaultDeckGenerator.Generate(CardClass.NEUTRAL, CardClass.NEUTRAL,
                                                    LiveCardSet.Db, Rng(), "d"));
            Assert.Throws<ArgumentException>(
                () => DefaultDeckGenerator.Generate(c.Primary, c.Primary, LiveCardSet.Db, Rng(), "d"));
        }

        /// <summary>An <see cref="ICardDatabase"/> over an explicit row list, for starving the pool on purpose.</summary>
        private sealed class FixedCardDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _byId = new();
            public FixedCardDatabase(IEnumerable<CardDefinition> defs) { foreach (var d in defs) _byId[d.Id] = d; }
            public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(int cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible()
                => _byId.Values.Where(d => d.PoolEligible).ToList();
        }
    }
}
