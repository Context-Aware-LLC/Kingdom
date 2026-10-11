using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Cards
{
    /// <summary>
    /// The ZONE half of a target query, as a set. Its partner is <see cref="TargetTypes"/>; the two
    /// are independent axes and a query is their product.
    ///
    /// <para><b>These replace the fused <c>TargetCandidateKind</c></b>, which spelled (zone x type)
    /// as six flat names: <c>Weapon</c> was a zone, <c>Hero</c> and <c>Minion</c> were Board-zone
    /// types, and <c>HandCard</c>/<c>DeckCard</c>/<c>GraveyardCard</c> were one type wearing three
    /// zone names. The fusion is the reason type was unavailable off the board -- the resolver
    /// assigned the kind from the zone alone, so an equipped weapon and the identical object in
    /// hand were different kinds, and "a weapon in your hand" was inexpressible no matter what an
    /// author wrote (docs/effect-model-fork.md:1568, "kind x zone fused in TargetCandidateKind").</para>
    ///
    /// <para>Splitting them also let each reader drop to the axis it already meant: the action
    /// handlers were asking a pure zone question through the fused name, the capability relation
    /// below a pure type one, and <c>CharacterRef</c> already carried hero-vs-minion a third time.</para>
    /// </summary>
    [System.Flags]
    public enum TargetZones
    {
        None = 0,
        Deck = 1 << 0,
        Hand = 1 << 1,
        Board = 1 << 2,
        Graveyard = 1 << 3,
        Weapon = 1 << 4,
        Any = Deck | Hand | Board | Graveyard | Weapon,
    }

    /// <summary>
    /// The TYPE half of a target query, as a set. See <see cref="TargetZones"/> for why the two are
    /// separate.
    ///
    /// <para><b><see cref="Hero"/> is the one non-card</b> (Sam, 2026-08-20). A weapon is a card
    /// that additionally has its own zone; a hero power is a card with no zone at all, which is not
    /// a counterexample to the model but the cleanest demonstration of it -- cardness and
    /// zone-occupancy are independent questions. Every other member is exactly a
    /// <see cref="Kingdom.Core.Contracts.CardType"/>; <see cref="OfCard"/> is the only conversion.</para>
    /// </summary>
    [System.Flags]
    public enum TargetTypes
    {
        None = 0,
        Hero = 1 << 0,
        Minion = 1 << 1,
        Spell = 1 << 2,
        Weapon = 1 << 3,
        HeroPower = 1 << 4,
        AnyCard = Minion | Spell | Weapon | HeroPower,
        Any = Hero | AnyCard,
    }

    /// <summary>Which player's things a query reaches. The third axis.</summary>
    public enum TargetSide
    {
        /// <summary>The controller of the effect, whoever that is. Never a seat index -- a
        /// seat-absolute reading is right from seat 0 and wrong from seat 1.</summary>
        Friendly,
        Enemy,
        Both,
    }

    /// <summary>
    /// A candidate's relation to the effect's source. The FOURTH axis (Sam, 2026-08-18), reached by
    /// reduction rather than design: the five names sharing "friendly minion" are one noun plus one
    /// modifier, and once side is declared what is left is a relation to the source.
    ///
    /// <para>The prose already carries every value as a single word -- "a friendly minion", "this",
    /// "another friendly minion", "an adjacent minion" -- which is what makes the axis 1:1 with what
    /// an author writes.</para>
    ///
    /// <para><b><see cref="Beside"/> is DERIVED, never stored.</b> An attribute on a minion answers
    /// "does this have a left neighbour"; the query asks "is this a neighbour of the SOURCE", and on
    /// a four-minion board with the source at index 1 the attribute selects three where the answer
    /// is two. Adjacency is a relation between two minions and has no value until both ends are
    /// named, so it cannot sit on one of them.</para>
    /// </summary>
    public enum SourceRelation
    {
        /// <summary>No relation required. The source is irrelevant to membership.</summary>
        Any,
        /// <summary>The source itself.</summary>
        Self,
        /// <summary>Everything the query reaches except the source -- "another".</summary>
        NotSelf,
        /// <summary>Immediately left or right of the source, in the source's OWN row. Adjacency has
        /// no cross-row geometry in this game, which is why this over-determines side.</summary>
        Beside,
        /// <summary>Whatever raised the trigger. A reference rather than a description: its side,
        /// zone and type are whatever the source happens to be.</summary>
        TriggerSource,
    }

    /// <summary>
    /// WHICH candidates an effect reaches, as the four independent axes rather than a name per
    /// combination: <b>side x zone x type x relation-to-source</b>. Traits (<see cref="TargetFilterDef"/>)
    /// narrow the result afterwards; HOW MANY come out is <see cref="TargetSelection"/>, a separate
    /// term, and neither belongs in here.
    ///
    /// <para><b>Why this exists.</b> 26 selector names were enumerated three separate times -- once
    /// to know them, once for their kinds, once for their cardinality -- to re-derive what a
    /// criteria object simply states. All three now read off this one value, and the redundancy the
    /// names were hiding becomes visible rather than argued: two names with the same query ARE the
    /// same selector.</para>
    ///
    /// <para><see cref="SourceRelation.Self"/> and <see cref="SourceRelation.TriggerSource"/> are
    /// environment reads wearing selector names -- they describe no criteria at all, which is why
    /// their other three axes are wide open and the relation does all the work.</para>
    /// </summary>
    public readonly struct TargetQuery
    {
        public TargetSide Side { get; }
        public TargetZones Zones { get; }
        public TargetTypes Types { get; }
        public SourceRelation Relation { get; }

        public TargetQuery(TargetSide side, TargetZones zones, TargetTypes types,
            SourceRelation relation = SourceRelation.Any)
        {
            Side = side;
            Zones = zones;
            Types = types;
            Relation = relation;
        }

        /// <summary>
        /// Whether this query can ever produce more than one candidate. <b>The cardinality axis,
        /// derived rather than listed</b> -- it used to be a fourth enumeration of the selector
        /// names. A relation that names one entity yields one; otherwise a query is unique only
        /// when it is pinned to a single side AND a zone that holds at most one matching thing,
        /// which is the hero (one per side) and the weapon slot (one per side).
        ///
        /// <para>WHAT IT IS FOR: a <c>distinctFrom</c> asks the resolver to exclude a target already
        /// chosen, and against a unique query it can never exclude anything -- so a card authored
        /// that way asks for something the game cannot express. Sam ruled it out on 2026-08-12
        /// (issue #143): "there is no other weapon. There isn't another one." Every phrasing the
        /// renderer could offer asserts a second one exists, so the data shape is what gets
        /// rejected instead.</para>
        ///
        /// <para>RETIREMENT TRIGGER: a DUAL-WIELD system makes the weapon zone hold more than one,
        /// and this method plus the card text generator's matching throw stop being true together.
        /// Change the clause below, not a list of names -- there is no longer a list.</para>
        /// </summary>
        public bool IsUnique
        {
            get
            {
                if (Relation == SourceRelation.Self || Relation == SourceRelation.TriggerSource)
                    return true;
                if (Side == TargetSide.Both) return false;
                if (Zones == TargetZones.Weapon) return true;
                return Zones == TargetZones.Board && (Types & ~TargetTypes.Hero) == 0;
            }
        }

        /// <summary>
        /// The most candidates this query could EVER yield, or <c>null</c> when nothing bounds it.
        /// <b>A pure function of the four axes and the zone caps</b> — no table, no per-name entry.
        ///
        /// <para>WHAT IT IS FOR: it is the whole of "does this card ask the player to choose". A
        /// selection whose count equals this maximum names every candidate, so nobody is choosing;
        /// a smaller count is a choice. That deletes the old <c>all</c>-vs-<c>player: 1</c> blind
        /// spot by construction — <c>FRIENDLY_HERO</c> has a maximum of one, so a count of one IS
        /// the maximum IS "no choice", and there is no second spelling of "your hero" left for a
        /// parser to guess between (Sam, 2026-08-21).</para>
        ///
        /// <para><b>Compare a count against THIS, never against the live candidate list.</b> "Choose
        /// 2 friendly minions" is a choice whether or not the board happens to hold exactly two.
        /// Comparing against the board instead would make the same card prompt on one turn and
        /// silently auto-target on the next, which is a property of the moment rather than of the
        /// card.</para>
        ///
        /// <para><b>Filters do not narrow this</b>, and must not. A filter is evaluated at resolve
        /// time against real candidates; folding it in here would let "a damaged enemy minion"
        /// derive a maximum of one and stop prompting. Same trap as reading the live board, one
        /// level up.</para>
        ///
        /// <para>Deck and graveyard are the unbounded pair: a deck is bounded by DECKBUILDING, which
        /// is not a runtime cap, and a graveyard only grows. They answer <c>null</c>, which makes
        /// "every candidate" unauthorable on them — a known and currently unreachable gap, since no
        /// row in the corpus targets either zone.</para>
        /// </summary>
        public int? MaxCandidates
        {
            get
            {
                // A relation that NAMES an entity settles the count before the other axes speak.
                if (Relation == SourceRelation.Self || Relation == SourceRelation.TriggerSource) return 1;
                if (Relation == SourceRelation.Beside) return 2;                 // left and right

                if ((Zones & (TargetZones.Deck | TargetZones.Graveyard)) != 0) return null;

                int perSide = 0;
                if ((Zones & TargetZones.Board) != 0)
                {
                    if ((Types & TargetTypes.Hero) != 0) perSide += ZoneCaps.Hero;
                    if ((Types & TargetTypes.Minion) != 0) perSide += ZoneCaps.Board;
                }
                if ((Zones & TargetZones.Weapon) != 0 && (Types & TargetTypes.Weapon) != 0)
                    perSide += ZoneCaps.Weapon;
                if ((Zones & TargetZones.Hand) != 0 && (Types & TargetTypes.AnyCard) != 0)
                    perSide += ZoneCaps.Hand;

                int total = (Side == TargetSide.Both ? 2 : 1) * perSide;

                // "another" excludes the source, which is one of the candidates just counted.
                if (Relation == SourceRelation.NotSelf && total > 0) total -= 1;
                return total;
            }
        }
    }

    /// <summary>
    /// Every selector name as the point in query space it always was. <b>Derived from the card-text
    /// prose, not from the resolver</b> (Sam, 2026-08-18: the code grammar should mirror the prose
    /// grammar "just about a one-for-one equality"), because the prose is already executable and
    /// already gated -- <c>CardValidator</c> checks it against every authored row. Each string in
    /// <c>CardTextGenerator</c>'s table is <c>[determiner] [side] [type] [zone]</c>, so the axes are
    /// read off it rather than invented.
    ///
    /// <para><b>Three pairs collapsed here on 2026-08-20</b> (Sam). <c>ALL_MINIONS</c>,
    /// <c>FRIENDLY_MINIONS</c> and <c>ENEMY_CHARACTERS</c> are gone; <c>ANY_MINION</c>,
    /// <c>ANY_FRIENDLY_MINION</c> and <c>ANY_ENEMY</c> absorbed them. Each pair held the identical
    /// query and differed only in CHOICE MODE, which is the selection term and lives elsewhere --
    /// so the name was carrying a quantity twice. The surviving name is the one that does not spell
    /// a count: <c>ALL_MINIONS</c> alongside <c>selection: "player: 1"</c> would have read as a
    /// contradiction on 24 rows, where <c>ANY_MINION</c> alongside <c>"all"</c> reads correctly.</para>
    ///
    /// <para><b>No name may be added here whose query duplicates another's.</b> That is not a style
    /// rule -- <c>SelectorRedundancyTests</c> asserts it over every name from both seats, and a
    /// duplicate is what the last three names cost the corpus to remove.</para>
    /// </summary>
    public static class TargetQueries
    {
        private const TargetTypes Character = TargetTypes.Hero | TargetTypes.Minion;

        private static readonly Dictionary<string, TargetQuery> ByName = new Dictionary<string, TargetQuery>
        {
            // Environment reads. No criteria: the relation names the entity outright, so side, zone
            // and type are whatever it turns out to be.
            ["SELF"] = new TargetQuery(TargetSide.Both, TargetZones.Any, TargetTypes.Any, SourceRelation.Self),
            ["TRIGGER_SOURCE"] = new TargetQuery(TargetSide.Both, TargetZones.Any, TargetTypes.Any, SourceRelation.TriggerSource),

            // "an adjacent minion" -- side stays open because Beside over-determines it: adjacency
            // exists only within the SOURCE's row, whichever side that is.
            ["ADJACENT"] = new TargetQuery(TargetSide.Both, TargetZones.Board, TargetTypes.Minion, SourceRelation.Beside),
            // "another friendly minion"
            ["OTHER_FRIENDLY_MINIONS"] = new TargetQuery(TargetSide.Friendly, TargetZones.Board, TargetTypes.Minion, SourceRelation.NotSelf),

            // "a minion" / "a friendly minion" / "an enemy minion"
            ["ANY_MINION"] = new TargetQuery(TargetSide.Both, TargetZones.Board, TargetTypes.Minion),
            ["ANY_FRIENDLY_MINION"] = new TargetQuery(TargetSide.Friendly, TargetZones.Board, TargetTypes.Minion),
            ["ANY_ENEMY_MINION"] = new TargetQuery(TargetSide.Enemy, TargetZones.Board, TargetTypes.Minion),

            // "a character" -- the Board zone with no type restriction, which is the whole
            // definition of the word. The hero is handed Zone.Board for exactly this reason.
            ["ANY_CHARACTER"] = new TargetQuery(TargetSide.Both, TargetZones.Board, Character),
            ["ANY_FRIENDLY"] = new TargetQuery(TargetSide.Friendly, TargetZones.Board, Character),
            ["ANY_ENEMY"] = new TargetQuery(TargetSide.Enemy, TargetZones.Board, Character),

            // "your hero" / "the enemy hero"
            ["FRIENDLY_HERO"] = new TargetQuery(TargetSide.Friendly, TargetZones.Board, TargetTypes.Hero),
            ["ENEMY_HERO"] = new TargetQuery(TargetSide.Enemy, TargetZones.Board, TargetTypes.Hero),

            // "your weapon" / "the enemy weapon". The type is stated even though the slot holds
            // nothing else: the query says what it means on both axes, and correlation between them
            // is a property of the game rather than of the language.
            ["FRIENDLY_WEAPON"] = new TargetQuery(TargetSide.Friendly, TargetZones.Weapon, TargetTypes.Weapon),
            ["ENEMY_WEAPON"] = new TargetQuery(TargetSide.Enemy, TargetZones.Weapon, TargetTypes.Weapon),

            // "a card in your hand" -- type-agnostic, which is exactly the axis the fused kind could
            // not express and the reason "a weapon in your hand" had no spelling.
            ["FRIENDLY_HAND"] = new TargetQuery(TargetSide.Friendly, TargetZones.Hand, TargetTypes.AnyCard),
            ["ENEMY_HAND"] = new TargetQuery(TargetSide.Enemy, TargetZones.Hand, TargetTypes.AnyCard),
            ["ANY_HAND_CARD"] = new TargetQuery(TargetSide.Both, TargetZones.Hand, TargetTypes.AnyCard),

            ["FRIENDLY_DECK"] = new TargetQuery(TargetSide.Friendly, TargetZones.Deck, TargetTypes.AnyCard),
            ["ENEMY_DECK"] = new TargetQuery(TargetSide.Enemy, TargetZones.Deck, TargetTypes.AnyCard),
            ["ANY_DECK_CARD"] = new TargetQuery(TargetSide.Both, TargetZones.Deck, TargetTypes.AnyCard),

            ["FRIENDLY_GRAVEYARD"] = new TargetQuery(TargetSide.Friendly, TargetZones.Graveyard, TargetTypes.AnyCard),
            ["ENEMY_GRAVEYARD"] = new TargetQuery(TargetSide.Enemy, TargetZones.Graveyard, TargetTypes.AnyCard),
            ["ANY_GRAVEYARD_CARD"] = new TargetQuery(TargetSide.Both, TargetZones.Graveyard, TargetTypes.AnyCard),

            // Minion-only graveyard reads, for REANIMATE -- raising a dead spell or weapon has no
            // meaning, so this narrows the type axis the way ANY_FRIENDLY_MINION narrows Board.
            ["FRIENDLY_GRAVEYARD_MINION"] = new TargetQuery(TargetSide.Friendly, TargetZones.Graveyard, TargetTypes.Minion),
            ["ENEMY_GRAVEYARD_MINION"] = new TargetQuery(TargetSide.Enemy, TargetZones.Graveyard, TargetTypes.Minion),
            ["ANY_GRAVEYARD_MINION"] = new TargetQuery(TargetSide.Both, TargetZones.Graveyard, TargetTypes.Minion),
        };

        /// <summary>The known selector names -- the table's own keys, so knowing a name and knowing
        /// what it means cannot drift apart.</summary>
        public static IReadOnlyCollection<string> Names => ByName.Keys;

        public static bool TryGet(string selector, out TargetQuery query) => ByName.TryGetValue(selector, out query);

        public static TargetQuery Get(string selector)
            => ByName.TryGetValue(selector, out TargetQuery query)
                ? query
                : throw new System.InvalidOperationException($"Unknown target selector '{selector}'.");
    }

    public static class TargetAxes
    {
        /// <summary>The single-bit <see cref="TargetTypes"/> for a card type. A candidate with no
        /// definition is the hero, and only the hero.</summary>
        public static TargetTypes OfCard(CardType type)
        {
            switch (type)
            {
                case CardType.MINION: return TargetTypes.Minion;
                case CardType.SPELL: return TargetTypes.Spell;
                case CardType.WEAPON: return TargetTypes.Weapon;
                case CardType.HERO_POWER: return TargetTypes.HeroPower;
                default: return TargetTypes.None;
            }
        }

        /// <summary>The single-bit <see cref="TargetZones"/> for a zone.</summary>
        public static TargetZones OfZone(Zone zone)
        {
            switch (zone)
            {
                case Zone.Deck: return TargetZones.Deck;
                case Zone.Hand: return TargetZones.Hand;
                case Zone.Board: return TargetZones.Board;
                case Zone.Graveyard: return TargetZones.Graveyard;
                case Zone.Weapon: return TargetZones.Weapon;
                default: return TargetZones.None;
            }
        }
    }

    /// <summary>
    /// One presentation-only sound attached to a card trigger. The sound id is the filename stem
    /// registered at Addressables key <c>sfx/&lt;soundId&gt;</c>; the trigger reuses the card-effect
    /// vocabulary so authors do not have to learn a second event model.
    /// </summary>
    public sealed class CardSoundEffect
    {
        public string SoundId { get; }
        public Trigger Trigger { get; }

        public CardSoundEffect(string soundId, Trigger trigger)
        {
            SoundId = soundId ?? "";
            Trigger = trigger;
        }
    }

    /// <summary>
    /// 1:1 mapping of the D1 §2 card row (dev-docs/specs/card-schema.md:5). <b>Immutable after load</b> — runtime
    /// mutable state lives on the <see cref="CardInstance"/>, never on the definition.
    /// </summary>
    public sealed class CardDefinition
    {
        /// <summary>
        /// The permanent, meaningless key. <b>The only thing anything resolves against</b> — decks
        /// on disk, the state hash, telemetry, replays, a rule naming another card.
        ///
        /// <para>Banded by type so a later set appends inside its own range and can never collide
        /// with the next one: HERO_POWER 1-50, WEAPON 51-150, SPELL 151-1000, MINION 1001 and up.
        /// Numbering starts at 1 and never at 0, because <c>default(int)</c> is 0 and a card id of
        /// 0 is indistinguishable from an unset field at every boundary it crosses.</para>
        ///
        /// <para>Assigned once by <c>tooling/scripts/assign_numeric_card_ids.py</c>. A number is
        /// permanent from the moment it ships and is never reused — see the retirement rule: a
        /// withdrawn card keeps its row and stays resolvable rather than freeing its id.</para>
        /// </summary>
        public int Id { get; }

        /// <summary>
        /// The mnemonic name a human reads in a desync frame, a replay diff, a balance-corpus row
        /// or a validator error — <c>ember_recruit</c> beats <c>1001</c> every time.
        ///
        /// <para><b>Nothing may resolve against this, and that is the whole condition that makes
        /// it worth having</b> (Sam, 2026-08-15). Not filenames, not art lookup, not deck storage,
        /// not telemetry, not fixtures. <b>The test is that you can change a handle and have
        /// nothing break.</b> If changing one breaks something, it is a key wearing a costume and
        /// the model collapses back to the problem it was built to delete. The dangerous version is
        /// the halfway one: a handle that is "mostly cosmetic" that exactly one tool happens to key
        /// on.</para>
        ///
        /// <para>Its precedent is <see cref="ArtId"/>, which already deliberately does not track
        /// the card id and proved it on 2026-08-15 when a card was renamed and the art was not.</para>
        /// </summary>
        public string Handle { get; }

        public string Name { get; }
        public CardType Type { get; }
        public int Cost { get; }
        public int? Attack { get; }
        public int? Health { get; }
        public int? Durability { get; }
        public CardClass Class { get; }
        public Rarity Rarity { get; }
        public IReadOnlyList<Keyword> Keywords { get; }
        public IReadOnlyList<RuleDefinition> Rules { get; }

        /// <summary>
        /// The player-facing rules text, AUTHORED on the row rather than composed from
        /// <see cref="Rules"/> at read time (Sam, 2026-08-16 — see the text-direction reversal in
        /// `docs/effect-model-fork.md`). Empty on a vanilla card, exactly as an omitted
        /// <see cref="FlavorText"/> is empty: absent and blank are the same statement here.
        ///
        /// <para><b>Seeded, once, by running <see cref="CardTextGenerator"/> over every row</b> — so
        /// today the field says exactly what the generator would have said, and what changed is
        /// which of the two is the source. The generator's remaining job is to CHECK this field
        /// rather than to supply it.</para>
        /// </summary>
        public string Text { get; }

        public string FlavorText { get; }
        public string Tooltip { get; }
        public string ArtId { get; }
        public IReadOnlyList<CardSoundEffect> SoundEffects { get; }
        public SetId SetId { get; }
        public bool Collectible { get; }
        public Tribe? Tribe { get; }
        public string FactionId { get; }

        /// <summary>
        /// Whether this row may enter a live card pool — deck generation, deck-building, booster
        /// packs, the soak's generated decks. Today this is exactly <see cref="Collectible"/>.
        /// <para><b>It is deliberately still its own property, and every</b>
        /// <c>ICardDatabase.AllCollectible</c> <b>implementation must filter on it rather than
        /// re-derive the predicate.</b> That is the whole lesson of EF-032a: the pool predicate
        /// used to be spelled out at each of the ~10 call sites, a gate was added to one meaning of
        /// it, and the gate came half-loose because the other call sites never heard about it. One
        /// named seam means the next pool restriction lands in one place instead of ten. Collapsing
        /// this to a plain <c>Collectible</c> read at each call site would re-scatter it.</para>
        /// </summary>
        public bool PoolEligible => Collectible && Lifecycle == CardLifecycle.PUBLISHED;

        /// <summary>
        /// Draft / published / retired (#398, #86). Independent of <see cref="Collectible"/>, which
        /// is about the card's nature (a token is published and never collectible); this is about
        /// whether the row is live. Only PUBLISHED rows can be <see cref="PoolEligible"/>.
        /// </summary>
        public CardLifecycle Lifecycle { get; }

        public CardDefinition(
            int id, string handle, string name, CardType type, int cost,
            int? attack, int? health, int? durability,
            CardClass @class, Rarity rarity,
            IReadOnlyList<Keyword> keywords, IReadOnlyList<RuleDefinition> rules,
            string flavorText, string artId, SetId setId,
            bool collectible, Tribe? tribe, string factionId, string tooltip = "",
            string text = "", IReadOnlyList<CardSoundEffect>? soundEffects = null,
            CardLifecycle lifecycle = CardLifecycle.PUBLISHED)
        {
            Lifecycle = lifecycle;
            Id = id;
            Handle = handle ?? "";
            Name = name;
            Type = type;
            Cost = cost;
            Attack = attack;
            Health = health;
            Durability = durability;
            Class = @class;
            Rarity = rarity;
            Keywords = keywords ?? System.Array.Empty<Keyword>();
            Rules = rules ?? System.Array.Empty<RuleDefinition>();
            Text = text ?? "";
            FlavorText = flavorText ?? "";
            Tooltip = tooltip ?? "";
            ArtId = artId ?? "";
            SoundEffects = soundEffects ?? System.Array.Empty<CardSoundEffect>();
            SetId = setId;
            Collectible = collectible;
            Tribe = tribe;
            FactionId = factionId ?? "";
        }
    }

    /// <summary>
    /// How many targets come out of a <see cref="TargetSpec"/> candidate set -- an integer, plus a
    /// marker saying the engine rolls for them.
    /// </summary>
    /// <remarks>
    /// <para><b>Two things, and one of them is always a number</b> (Sam, 2026-08-21):
    /// <i>"we want counts to always be an integer"</i>, and on the old chooser field, <i>"all that
    /// field is = random | !random... so maybe drop chosen by and the engine merely provides and
    /// enforces legal targets in the absence of RANDOM"</i>. So <c>player</c> is never written --
    /// it is what is left when nothing else is said.</para>
    ///
    /// <para><b><c>all</c> is not a value here. It is <c>Count == MaxCandidates</c></b>, the static
    /// maximum derived from the selector's query (<see cref="TargetQuery.MaxCandidates"/>). Three
    /// behaviours fall out with nothing else authored:</para>
    /// <list type="table">
    ///   <item><term>count == max</term><description>no choice -- every candidate the query yields</description></item>
    ///   <item><term>count &lt; max, no marker</term><description>the player picks, from the set the engine already computes and enforces</description></item>
    ///   <item><term><see cref="IsRandom"/></term><description>the engine rolls, and may land on the same candidate twice</description></item>
    /// </list>
    ///
    /// <para><b>Why the maximum and not the absence of a term.</b> The earlier shape spelled "all"
    /// as its own mode, which left 26 rows where <c>all</c> and <c>player: 1</c> described the same
    /// thing on a selector that can only ever yield one -- "your hero" had two spellings and nothing
    /// could say which an author meant. Comparing against the maximum collapses them: for
    /// <c>FRIENDLY_HERO</c>, one IS the maximum. The ambiguity dies by deletion rather than by
    /// inference, the same move as retiring a duplicate selector name.</para>
    ///
    /// <para><b>The magic number is not a second copy of the cap.</b> Nobody types it -- rules are
    /// derived from card text, so the count is emitted from the prose, and <c>C27</c> re-checks it
    /// against the derived maximum on every validation. A cap change therefore surfaces as a
    /// rejected row rather than as silent drift.</para>
    ///
    /// <para><b>One shape is deliberately unwritable.</b> An automatic finite count -- nobody
    /// choosing, fewer than every candidate -- took the first N in board order, <i>"it takes the two
    /// leftmost every time; that's not ideal. It should definitely be random"</i> (Sam, 2026-08-18).
    /// <c>C27</c> is what keeps it unwritable now that a count no longer implies its chooser.</para>
    ///
    /// <para>Note that <c>ALL_</c> in a selector NAME means BOTH SIDES, which is why the one-side
    /// sweep is not called ALL_ENEMIES. That is a SELECTOR concern and it survives this type
    /// unchanged: a count is a quantity, never a side.</para>
    /// </remarks>
    public readonly struct TargetSelection : System.IEquatable<TargetSelection>
    {
        /// <summary>How many candidates are taken. Always one or more on a constructed spec; zero
        /// only on <c>default</c>, which <see cref="TargetSpec"/> rejects.</summary>
        public int Count { get; }

        /// <summary>Whether the engine rolls for them. Its absence means the player picks -- unless
        /// <see cref="Count"/> has reached the selector's maximum, in which case nobody picks.</summary>
        public bool IsRandom { get; }

        /// <summary>
        /// Whether the engine takes them deterministically, in canonical candidate order, and the
        /// player is never asked. This is the third disposition <see cref="Count"/> alone cannot
        /// spell: not a roll, and not "every candidate" either, because it exists precisely where
        /// <see cref="TargetSpec.MaxCandidates"/> is unbounded and "every candidate" therefore has no
        /// integer -- a deck or a graveyard. <c>CardValidator</c> refuses it anywhere else, because on
        /// a bounded pool a deterministic count below the maximum is the unprincipled "first N in
        /// board order" shape <see cref="Of"/>'s remarks describe Sam ruling out on 2026-08-18.
        /// </summary>
        public bool IsAutomatic { get; }

        private TargetSelection(int count, bool isRandom, bool isAutomatic)
        {
            Count = count;
            IsRandom = isRandom;
            IsAutomatic = isAutomatic;
        }

        /// <summary><c>N</c> -- exactly N, chosen by the controller, or by nobody once N is the maximum.</summary>
        public static TargetSelection Of(int count) => Make(count, isRandom: false, isAutomatic: false);

        /// <summary><c>random: N</c> -- exactly N, rolled by the engine.</summary>
        public static TargetSelection RandomOf(int count) => Make(count, isRandom: true, isAutomatic: false);

        /// <summary><c>auto: N</c> -- exactly N (or fewer, if the pool runs out), taken by the engine
        /// in canonical candidate order. See <see cref="IsAutomatic"/> for what distinguishes this
        /// from <see cref="RandomOf"/> and from an ordinary chosen count.</summary>
        public static TargetSelection AutomaticOf(int count) => Make(count, isRandom: false, isAutomatic: true);

        /// <summary>
        /// Every candidate <paramref name="selector"/> can ever yield -- the count that reads as
        /// "all" on the card face. Throws on a selector nothing bounds, because "all of an unbounded
        /// set" has no integer and this type only speaks integers.
        /// </summary>
        public static TargetSelection AllOf(string selector)
        {
            int? max = TargetQueries.Get(selector).MaxCandidates;
            if (!max.HasValue)
                throw new System.ArgumentException(
                    $"'{selector}' reaches the deck or graveyard, which nothing bounds, so 'every candidate' " +
                    "has no count. Write an explicit number instead.", nameof(selector));
            return Of(max.Value);
        }

        private static TargetSelection Make(int count, bool isRandom, bool isAutomatic)
        {
            if (count < 1)
                throw new System.ArgumentOutOfRangeException(nameof(count),
                    $"A selection of {count} takes nothing. Write 1 or more.");
            return new TargetSelection(count, isRandom, isAutomatic);
        }

        /// <summary>Reads an authored term, rejecting anything this type cannot say.</summary>
        public static TargetSelection Parse(string term)
        {
            if (TryParse(term, out TargetSelection selection)) return selection;
            throw new System.FormatException(
                $"'{term}' is not a selection term. Write 'N', 'random: N' or 'auto: N', N being 1 or more.");
        }

        /// <summary>Reads an authored term, reporting failure rather than throwing.</summary>
        public static bool TryParse(string? term, out TargetSelection selection)
        {
            selection = default;
            if (term == null) return false;
            string text = term.Trim();

            bool isRandom = false;
            bool isAutomatic = false;
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                string prefix = text.Substring(0, colon).TrimEnd();
                if (prefix == "random") isRandom = true;
                else if (prefix == "auto") isAutomatic = true;
                else return false;
                text = text.Substring(colon + 1).TrimStart();
            }

            // int.Parse would accept "+2", " 2" and a thousands separator under some cultures, and
            // each of those is a second spelling of a term whose spelling is the point. Digits only.
            if (text.Length == 0) return false;
            int count = 0;
            foreach (char c in text)
            {
                if (c < '0' || c > '9' || count > int.MaxValue / 10) return false;
                count = (count * 10) + (c - '0');
            }
            if (count < 1) return false;

            selection = new TargetSelection(count, isRandom, isAutomatic);
            return true;
        }

        public override string ToString()
        {
            string number = Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (IsRandom) return "random: " + number;
            if (IsAutomatic) return "auto: " + number;
            return number;
        }

        public bool Equals(TargetSelection other)
            => Count == other.Count && IsRandom == other.IsRandom && IsAutomatic == other.IsAutomatic;
        public override bool Equals(object? obj) => obj is TargetSelection other && Equals(other);
        public override int GetHashCode() => (Count * 397) ^ (IsRandom ? 1 : 0) ^ (IsAutomatic ? 2 : 0);
        public static bool operator ==(TargetSelection left, TargetSelection right) => left.Equals(right);
        public static bool operator !=(TargetSelection left, TargetSelection right) => !left.Equals(right);
    }

    /// <summary>Optional predicates applied after selector candidate enumeration.</summary>
    /// <remarks>
    /// Every axis is nullable and they compose as AND. Adding one is never a single edit: the
    /// filter is read by <c>Targeting.PassesFilter</c>, written and read by the JSON codec on both
    /// sides, spoken by <c>CardTextGenerator.Filter</c>, proposed back by
    /// <c>CardTextParser.Filters</c>, compared by <c>SameFilter</c>, and gated by
    /// <c>AuraCapabilities.IsFoldableFilter</c> — which is a WHITELIST, so a new axis is refused
    /// on an aura until somebody rules on it rather than silently admitted.
    /// </remarks>
    public sealed class TargetFilterDef
    {
        public bool? Damaged { get; }

        public Tribe? Tribe { get; }

        /// <summary>
        /// Flips <see cref="Tribe"/> from "must match" to "must not match" — "any minion that is
        /// NOT a Beast". Meaningless while <see cref="Tribe"/> is null; <c>CardValidator</c> refuses
        /// that combination rather than authoring a bit that does nothing (Sam, 2026-09-05).
        /// </summary>
        public bool NegateTribe { get; }

        /// <summary>
        /// A candidate's cost bound, inclusive — paired with <see cref="NegateCost"/> to say which
        /// side: "an enemy minion that costs 4 or less" (the default) or "... 4 or more".
        ///
        /// <para>Named <c>MaxCost</c> rather than <c>Cost</c> deliberately: the JSON key
        /// (<c>maxCost</c>) is a stable authored contract, and only one shipped card uses the axis
        /// at all today, so renaming it to fit the new direction bit would buy a tidier name at the
        /// cost of a card-data migration for zero behavior change. <see cref="NegateCost"/> flips
        /// the READING of the same stored bound rather than the bound getting a new name.</para>
        ///
        /// <para>It reads the DEFINITION's printed cost, because that is the only cost this engine
        /// has: nothing modifies a card's cost at runtime, so there is no "current cost" to consult
        /// and a candidate not backed by a definition simply fails the test. RETIREMENT TRIGGER: the
        /// day a cost-modifying effect lands, this predicate has to choose between printed and
        /// current, and <c>AuraCapabilities.IsFoldableFilter</c>'s refusal of this axis becomes
        /// load-bearing rather than merely cautious.</para>
        /// </summary>
        public int? MaxCost { get; }

        /// <summary>
        /// Flips <see cref="MaxCost"/> from "at most" to "at least". Meaningless while
        /// <see cref="MaxCost"/> is null; refused in combination the same way
        /// <see cref="NegateTribe"/> is.
        /// </summary>
        public bool NegateCost { get; }

        /// <summary>
        /// A keyword the candidate must be carrying (or, with <see cref="NegateKeyword"/>, must
        /// lack) — "all friendly Shroud minions" / "all friendly minions without Shroud".
        ///
        /// <para><b>This one reads the INSTANCE, not the definition</b>, and it is the only axis
        /// that does. <c>Damaged</c> reads live health, <c>Tribe</c> and <c>MaxCost</c> read the
        /// printed row; a keyword is BOTH, because <c>CardInstance.Keywords</c> is base ∪ granted
        /// and a granted Shroud is as real as a printed one. Reading the definition here would make
        /// "your Shroud minions" quietly mean "the ones that were PRINTED with Shroud", which is
        /// not what any card face says.</para>
        ///
        /// <para>An action marker (<c>SILENCE</c>, <c>BACKDRAFT</c>,) is never
        /// carried, so filtering on one selects nobody (and negating one selects everybody);
        /// <c>CardValidator</c> C14L refuses it rather than letting a row read as a constraint that
        /// does not do what it says.</para>
        /// </summary>
        public Keyword? Keyword { get; }

        /// <summary>Flips <see cref="Keyword"/> from "must carry" to "must lack".</summary>
        public bool NegateKeyword { get; }

        /// <summary>
        /// <see cref="Damaged"/> takes no negate bit of its own — it is already a tri-state
        /// (<c>true</c>/<c>false</c>/unset says damaged/undamaged/either), so "negated" would only
        /// ever mean "the value the author would have written directly instead."
        /// </summary>
        public TargetFilterDef(bool? damaged = null, Tribe? tribe = null, bool negateTribe = false,
            int? maxCost = null, bool negateCost = false, Keyword? keyword = null,
            bool negateKeyword = false)
        {
            Damaged = damaged;
            Tribe = tribe;
            NegateTribe = negateTribe;
            MaxCost = maxCost;
            NegateCost = negateCost;
            Keyword = keyword;
            NegateKeyword = negateKeyword;
        }
    }

    /// <summary>
    /// Whether a pick is the PRICE of playing the card, and whether the card can be played without
    /// paying it.
    ///
    /// <para><b>This is the one thing an author states and the engine's sole indicator</b> (Sam,
    /// 2026-08-22). It replaced an attempt to read cost-ness out of the prose — a verb, or the
    /// conjunction — and the reason that failed is worth keeping: <i>"Destroy a friendly minion and
    /// draw a card."</i> is THREE cards (play and draw anyway / unplayable / play and don't draw)
    /// and the sentence contains nothing that picks one. No parser can find an answer the words do
    /// not hold, which is why this is authored rather than inferred.</para>
    ///
    /// <para><b><see cref="Required"/> is the old flag, unchanged in meaning</b> — an unpayable cost
    /// makes the card unplayable on every card type, body or not. <see cref="Optional"/> is the
    /// shape that did not exist: the card plays, the price goes unpaid, and the run it buys does not
    /// happen. Only <see cref="CostPaidCondition"/> can express that half; a flag alone never
    /// could.</para>
    ///
    /// <para><b>Optional is illegal on a SPELL</b> (Sam, 2026-08-22, holding the 2026-08-07 ruling):
    /// <i>"the spell should be unplayable if the cost is not able to be paid."</i> A spell that
    /// resolves to nothing is a mana and a card for nothing, so a spell's cost is always required.
    /// <c>CardValidator</c> C28 rejects the other spelling rather than letting it read as authored
    /// intent that the runtime then ignores.</para>
    /// </summary>
    public enum CostKind
    {
        /// <summary>An ordinary effect. Not a price for anything.</summary>
        None,
        /// <summary>A price that must be payable, or the card cannot be played.</summary>
        Required,
        /// <summary>A price the player may decline; declining skips the run it buys.</summary>
        Optional,
    }

    /// <summary>
    /// The complete authored <c>target</c> object: WHICH candidates (<see cref="Selector"/>, narrowed
    /// by <see cref="Filter"/>) and HOW MANY come out, chosen by whom (<see cref="Selection"/>).
    ///
    /// <para><see cref="Cost"/> is the one axis that is not about candidates at all — it says this
    /// pick is the card's price. See <see cref="CostKind"/> for why it is authored rather than read
    /// out of the sentence.</para>
    /// </summary>
    public sealed class TargetSpec
    {
        // The list of known names was the FIRST of three enumerations of the same 26 strings.
        // A name is known exactly when the query table says what it means -- knowing a name and
        // knowing its criteria can no longer drift apart, because they are one entry.

        public string Selector { get; }
        public TargetSelection Selection { get; }
        public TargetFilterDef? Filter { get; }
        public string Binding { get; }
        public string DistinctFrom { get; }
        public CostKind Cost { get; }

        /// <summary>Whether this pick is the card's price, whichever way it is payable.</summary>
        public bool IsCost => Cost != CostKind.None;

        // `selection` has no default on purpose. Every construction site says the term out loud,
        // which is the same rule the JSON is held to and the reason a selector can no longer imply
        // one silently — the old signature let `new TargetSpec("ANY_MINION")` mean "the player picks
        // one" without the phrase appearing anywhere.
        public TargetSpec(string selector, TargetSelection selection,
            TargetFilterDef? filter = null, string? binding = null, string? distinctFrom = null,
            CostKind cost = CostKind.None)
        {
            if (selection.Count < 1)
                throw new System.ArgumentException(
                    $"'{selector}' was given no selection term. Every target says how many it takes.",
                    nameof(selection));

            Selector = selector;
            Selection = selection;
            Filter = filter;
            Binding = binding ?? "";
            DistinctFrom = distinctFrom ?? "";
            Cost = cost;
        }

        public static bool IsKnownSelector(string selector) => TargetQueries.TryGet(selector, out _);

        /// <summary>
        /// The most candidates this spec's selector could ever yield, or <c>null</c> when nothing
        /// bounds it -- including when the selector is not a known name, since an unknown query
        /// bounds nothing either.
        /// </summary>
        public int? MaxCandidates
            => TargetQueries.TryGet(Selector, out TargetQuery query) ? query.MaxCandidates : null;

        /// <summary>
        /// Whether this spec names EVERY candidate its selector can yield, so nobody is choosing --
        /// what used to be spelled <c>all</c>. Reads the selector's STATIC maximum, never the live
        /// board; see <see cref="TargetQuery.MaxCandidates"/> for why that distinction is the whole
        /// point.
        /// </summary>
        public bool IsEveryCandidate
        {
            get
            {
                if (Selection.IsRandom) return false;
                int? max = MaxCandidates;
                return max.HasValue && Selection.Count >= max.Value;
            }
        }

        /// <summary>
        /// Whether the controller picks the targets. <b>Derived, never authored</b> -- it is simply
        /// what is left once the engine is not rolling, not auto-taking, and the count has not
        /// reached the maximum (Sam, 2026-08-21; extended 2026-09-08 for
        /// <see cref="TargetSelection.IsAutomatic"/>). A null spec is not a choice.
        /// </summary>
        public static bool IsPlayerChosen(TargetSpec? target)
            => target != null && !target.Selection.IsRandom && !target.Selection.IsAutomatic &&
               !target.IsEveryCandidate;

        public static IReadOnlyCollection<string> Selectors => TargetQueries.Names;
    }

}
