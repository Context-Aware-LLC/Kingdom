using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>
    /// The Council's Findings that need more than a counter: who dealt which hit, whose card it was,
    /// and what took a hero to 0 (dev-docs/design/whimsy-pass-2026-10-17.md, #2). Read off each
    /// command's event stream against a snapshot taken before it, so like the rest of
    /// <see cref="MatchTelemetryRecorder"/> it observes the core from outside and touches no game state.
    ///
    /// <para><b>A minion's Health is read; a hero's is replayed.</b> A hit's
    /// <see cref="GameEvent.HealthBefore"/> is the Health it really landed on, so both Overkills are
    /// exact even on a minion buffed earlier in the same command, which a replay through
    /// <see cref="GameEventKind.StatsModified"/> cannot see. A hero's Health and Armor are still
    /// carried forward from the snapshot, because the final blow needs Armor too.</para>
    ///
    /// <para><b>The King's commissions read <see cref="Tally"/></b> (#421). Those numbers decide
    /// prizes, so each is a sum or a best over every hit, never an estimate.</para>
    /// </summary>
    internal sealed class FindingsObserver
    {
        /// <summary>What a command's events cannot say on their own, read before the core sees it.</summary>
        internal sealed class Before
        {
            /// <summary>The seat controlling each hero, board minion and weapon.</summary>
            public readonly Dictionary<InstanceId, int> Seat = new Dictionary<InstanceId, int>();
            public readonly HashSet<InstanceId> InHandOrDeck = new HashSet<InstanceId>();
            public readonly int[] HeroHealth = new int[2];
            public readonly int[] HeroArmor = new int[2];
            public int? HeroPowerSeat;
            public InstanceId? Attacker;
        }

        /// <summary>One seat's exact commission numbers (#421), read through
        /// <c>MatchTelemetryRecorder.TallyFor</c>, which adds the two the recorder counts itself.</summary>
        public readonly struct Tally
        {
            public Tally(int overkillBest, int selfDamage, int unspentMana, int fatigueDamage, int cardsBurned,
                         int tollDamage, int stolenDamage, int heroPowerDamage)
            {
                OverkillBest = overkillBest;
                SelfDamage = selfDamage;
                UnspentMana = unspentMana;
                FatigueDamage = fatigueDamage;
                CardsBurned = cardsBurned;
                TollDamage = tollDamage;
                StolenDamage = stolenDamage;
                HeroPowerDamage = heroPowerDamage;
            }

            /// <summary>The most damage the seat dealt beyond a living minion's Health in one hit.</summary>
            public int OverkillBest { get; }
            /// <summary>Damage the seat's own cards, hero and Hero Power dealt its own hero.</summary>
            public int SelfDamage { get; }
            /// <summary>Mana left at each of the seat's turn ends, summed.</summary>
            public int UnspentMana { get; }
            /// <summary>Fatigue damage the seat's hero took.</summary>
            public int FatigueDamage { get; }
            /// <summary>Cards the seat drew into a full hand.</summary>
            public int CardsBurned { get; }
            /// <summary>Damage the seat's Tolls dealt the enemy hero, Armor-soaked damage included.</summary>
            public int TollDamage { get; }
            /// <summary>Damage minions the seat took from the other side dealt that side's hero.</summary>
            public int StolenDamage { get; }
            /// <summary>Damage the seat's Hero Power dealt the enemy hero.</summary>
            public int HeroPowerDamage { get; }
        }

        private const int OverkillHit = 10;
        private const int BigMinionCost = 8;

        private readonly ICardDatabase? _db;

        /// <summary>The seat each card was first seen on. A minion taken by the other side keeps it.</summary>
        private readonly Dictionary<InstanceId, int> _startedOn = new Dictionary<InstanceId, int>();

        public readonly int[] SelfDamage = new int[2];
        public readonly int[] Overkills = new int[2];
        public readonly int[] OverkillBest = new int[2];
        public readonly int[] FatigueDamage = new int[2];
        public readonly int[] TollDamage = new int[2];
        public readonly int[] StolenDamage = new int[2];
        public readonly int[] HeroPowerDamage = new int[2];
        public readonly int[] BigMinionsBounced = new int[2];
        public readonly int[] LegendariesLost = new int[2];
        public readonly int[] MostBulwarks = new int[2];
        public readonly FinalBlow[] FellTo = new FinalBlow[2];

        /// <param name="db">Null leaves every finding that needs a card's cost, rarity or type at none.</param>
        public FindingsObserver(ICardDatabase? db) => _db = db;

        /// <summary>Notes where every card is, which is how a stolen minion is told from its owner's.</summary>
        public void Observe(GameState state)
        {
            for (int seat = 0; seat < 2; seat++)
            {
                PlayerState ps = state.Players[seat];
                NoteAll(ps.Deck, seat);
                NoteAll(ps.Hand, seat);
                NoteAll(ps.Board, seat);
                NoteAll(ps.Graveyard, seat);
                if (ps.Weapon != null) Note(ps.Weapon.Id, seat);

                int bulwarks = 0;
                foreach (CardInstance minion in ps.Board)
                    if (minion.Keywords.Contains(Keyword.BULWARK)) bulwarks++;
                if (bulwarks > MostBulwarks[seat]) MostBulwarks[seat] = bulwarks;
            }
        }

        public static Before Snapshot(GameState state, PlayerIntent intent)
        {
            var before = new Before();
            for (int seat = 0; seat < 2; seat++)
            {
                PlayerState ps = state.Players[seat];
                before.Seat[ps.Hero.Id] = seat;
                before.HeroHealth[seat] = ps.Hero.CurrentHealth;
                before.HeroArmor[seat] = ps.Hero.Armor;
                foreach (CardInstance minion in ps.Board) before.Seat[minion.Id] = seat;
                if (ps.Weapon != null) before.Seat[ps.Weapon.Id] = seat;
                foreach (CardInstance c in ps.Hand) before.InHandOrDeck.Add(c.Id);
                foreach (CardInstance c in ps.Deck) before.InHandOrDeck.Add(c.Id);
            }
            if (intent is HeroPowerIntent) before.HeroPowerSeat = state.SeatOf(intent.Player);
            if (intent is AttackIntent attack) before.Attacker = attack.Attacker;
            return before;
        }

        public void After(GameState state, Before before, IReadOnlyList<GameEvent>? events)
        {
            Observe(state);
            if (events == null) return;

            int[] heroHealth = { before.HeroHealth[0], before.HeroHealth[1] };
            int[] heroArmor = { before.HeroArmor[0], before.HeroArmor[1] };

            foreach (GameEvent e in events)
            {
                int amount = e.Amount ?? 0;
                int heroSeat = e.Target is InstanceId t ? HeroSeat(state, t) : -1;
                switch (e.Kind)
                {
                    case GameEventKind.DamageApplied when amount > 0 && e.Target.HasValue:
                        int? sourceSeat = e.Source is InstanceId s ? ControllerOf(state, before, s) : null;
                        if (heroSeat >= 0)
                        {
                            if (sourceSeat == heroSeat) SelfDamage[heroSeat] += amount;
                            else if (sourceSeat is int dealer)
                                Credit(dealer, Named(state, before, e.Source, sourceSeat, heroSeat), amount);
                            int soak = heroArmor[heroSeat] < amount ? heroArmor[heroSeat] : amount;
                            heroArmor[heroSeat] -= soak;
                            if (Falls(heroHealth, heroSeat, amount - soak))
                                FellTo[heroSeat] = Blow(state, before, e.Source, sourceSeat, heroSeat);
                        }
                        else if (e.HealthBefore is int health && health > 0 && sourceSeat is int dealer)
                        {
                            // A minion already at 0 is dead and waiting on the death check; a further
                            // hit on it kills nothing, so it scores no Overkill of either kind.
                            if (health == 1 && amount >= OverkillHit) Overkills[dealer]++;
                            if (amount - health > OverkillBest[dealer]) OverkillBest[dealer] = amount - health;
                        }
                        break;

                    case GameEventKind.FatigueDamage when heroSeat >= 0:
                        FatigueDamage[heroSeat] += amount;
                        if (Falls(heroHealth, heroSeat, amount)) FellTo[heroSeat] = FinalBlow.Fatigue;
                        break;

                    case GameEventKind.HealthRestored when heroSeat >= 0:
                        heroHealth[heroSeat] += amount;
                        break;

                    case GameEventKind.ArmorGained when heroSeat >= 0:
                        heroArmor[heroSeat] += amount;
                        break;

                    case GameEventKind.CardBounced when e.Target.HasValue:
                        // A card bounced off the board is re-minted in hand, so an id that was already in
                        // a hand or deck came from there and is no return from the board.
                        if (before.InHandOrDeck.Contains(e.Target.Value)) break;
                        if (Find(state, e.Target.Value, out CardInstance? bounced, out int bouncedTo)
                            && Definition(bounced!) is { Type: CardType.MINION } def && def.Cost >= BigMinionCost)
                            BigMinionsBounced[bouncedTo]++;
                        break;

                    case GameEventKind.ControlChanged when e.Target.HasValue:
                        InstanceId taken = e.Target.Value;
                        if (!before.Seat.TryGetValue(taken, out int from)) break;
                        if (!_startedOn.TryGetValue(taken, out int owner) || owner != from) break;
                        if (Find(state, taken, out CardInstance? card, out int to) && to != from
                            && Definition(card!) is { Rarity: Rarity.LEGENDARY })
                            LegendariesLost[from]++;
                        break;
                }
            }
        }

        /// <summary>Lowers a hero's replayed Health; true when this is the hit that took it to 0.</summary>
        private static bool Falls(int[] heroHealth, int seat, int toHealth)
        {
            int was = heroHealth[seat];
            heroHealth[seat] = was - toHealth;
            return was > 0 && heroHealth[seat] <= 0;
        }

        /// <summary>A hit <paramref name="dealer"/> landed on the other side's hero, credited to every
        /// commission whose kind <paramref name="named"/> says it was. Event amounts, so a hit the
        /// Armor soaked still counts as dealt.</summary>
        private void Credit(int dealer, FinalBlow named, int amount)
        {
            if ((named & FinalBlow.Toll) != 0) TollDamage[dealer] += amount;
            if ((named & FinalBlow.StolenMinion) != 0) StolenDamage[dealer] += amount;
            if ((named & FinalBlow.HeroPower) != 0) HeroPowerDamage[dealer] += amount;
        }

        private FinalBlow Blow(GameState state, Before before, InstanceId? source, int? sourceSeat, int fallen)
        {
            FinalBlow blow = Named(state, before, source, sourceSeat, fallen);
            return blow == FinalBlow.None ? FinalBlow.Other : blow;
        }

        private FinalBlow Named(GameState state, Before before, InstanceId? source, int? sourceSeat, int fallen)
        {
            if (source is not InstanceId id) return FinalBlow.None;
            FinalBlow blow = sourceSeat == fallen ? FinalBlow.OwnSide : FinalBlow.None;

            if (before.HeroPowerSeat is int caster && caster != fallen && id == state.Players[caster].Hero.Id)
                blow |= FinalBlow.HeroPower;

            if (!Find(state, id, out CardInstance? card, out _) || Definition(card!) is not CardDefinition def)
                return blow;

            if (def.Type == CardType.SPELL) blow |= FinalBlow.Spell;
            if (def.Type == CardType.MINION)
            {
                // A Toll is rung by a death, so its source has left the board; a minion that died
                // trading blows in its own attack dealt that hit with its Attack, not its Toll.
                if (card!.Zone != Zone.Board && id != before.Attacker && HasToll(def)) blow |= FinalBlow.Toll;
                if (sourceSeat is int controller && controller != fallen
                    && _startedOn.TryGetValue(id, out int owner) && owner == fallen)
                    blow |= FinalBlow.StolenMinion;
            }
            return blow;
        }

        private static bool HasToll(CardDefinition def)
        {
            foreach (RuleDefinition rule in def.Rules)
                if (rule.Activation is TriggeredActivation { Trigger: Trigger.ON_DEATH }) return true;
            return false;
        }

        /// <summary>Who controlled <paramref name="id"/> when it acted: its seat before the command
        /// for anything that was in play, else wherever it is now (a resolved spell sits in its
        /// caster's graveyard), else the seat it was first seen on.</summary>
        private int? ControllerOf(GameState state, Before before, InstanceId id)
        {
            if (before.Seat.TryGetValue(id, out int seat)) return seat;
            if (Find(state, id, out _, out seat)) return seat;
            if (_startedOn.TryGetValue(id, out seat)) return seat;
            return null;
        }

        private CardDefinition? Definition(CardInstance card)
            => _db != null && _db.TryGet(card.DefinitionId, out CardDefinition def) ? def : null;

        private static int HeroSeat(GameState state, InstanceId id)
        {
            for (int seat = 0; seat < 2; seat++)
                if (state.Players[seat].Hero.Id == id) return seat;
            return -1;
        }

        private static bool Find(GameState state, InstanceId id, out CardInstance? card, out int seat)
        {
            for (seat = 0; seat < 2; seat++)
            {
                PlayerState ps = state.Players[seat];
                if ((card = ps.Board.Find(c => c.Id == id)) != null) return true;
                if (ps.Weapon != null && ps.Weapon.Id == id) { card = ps.Weapon; return true; }
                if ((card = ps.Graveyard.Find(c => c.Id == id)) != null) return true;
                if ((card = ps.Hand.Find(c => c.Id == id)) != null) return true;
                if ((card = ps.Deck.Find(c => c.Id == id)) != null) return true;
            }
            card = null;
            seat = -1;
            return false;
        }

        private void NoteAll(List<CardInstance> cards, int seat)
        {
            foreach (CardInstance c in cards) Note(c.Id, seat);
        }

        private void Note(InstanceId id, int seat)
        {
            if (!_startedOn.ContainsKey(id)) _startedOn[id] = seat;
        }
    }
}
