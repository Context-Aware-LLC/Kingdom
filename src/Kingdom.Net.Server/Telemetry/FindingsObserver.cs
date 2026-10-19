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
    /// <para><b>Health is replayed, not read.</b> An event carries the damage dealt but not the Health
    /// it was dealt to, so a minion's and a hero's Health is carried forward through the command's
    /// events from the snapshot. A stat change mid-command (<see cref="GameEventKind.StatsModified"/>)
    /// is not replayed, which can misjudge an Overkill on a minion buffed in the same command.</para>
    /// </summary>
    internal sealed class FindingsObserver
    {
        /// <summary>What a command's events cannot say on their own, read before the core sees it.</summary>
        internal sealed class Before
        {
            /// <summary>The seat controlling each hero, board minion and weapon.</summary>
            public readonly Dictionary<InstanceId, int> Seat = new Dictionary<InstanceId, int>();
            /// <summary>Each board minion's Health.</summary>
            public readonly Dictionary<InstanceId, int> MinionHealth = new Dictionary<InstanceId, int>();
            public readonly HashSet<InstanceId> InHandOrDeck = new HashSet<InstanceId>();
            public readonly int[] HeroHealth = new int[2];
            public readonly int[] HeroArmor = new int[2];
            public int? HeroPowerSeat;
            public InstanceId? Attacker;
        }

        private const int OverkillHit = 10;
        private const int BigMinionCost = 8;

        private readonly ICardDatabase? _db;

        /// <summary>The seat each card was first seen on. A minion taken by the other side keeps it.</summary>
        private readonly Dictionary<InstanceId, int> _startedOn = new Dictionary<InstanceId, int>();

        public readonly int[] SelfDamage = new int[2];
        public readonly int[] Overkills = new int[2];
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
                foreach (CardInstance minion in ps.Board)
                {
                    before.Seat[minion.Id] = seat;
                    before.MinionHealth[minion.Id] = minion.CurrentHealth;
                }
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
            var minionHealth = new Dictionary<InstanceId, int>(before.MinionHealth);

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
                            int soak = heroArmor[heroSeat] < amount ? heroArmor[heroSeat] : amount;
                            heroArmor[heroSeat] -= soak;
                            if (Falls(heroHealth, heroSeat, amount - soak))
                                FellTo[heroSeat] = Blow(state, before, e.Source, sourceSeat, heroSeat);
                        }
                        else if (minionHealth.TryGetValue(e.Target.Value, out int health))
                        {
                            if (health == 1 && amount >= OverkillHit && sourceSeat is int dealer) Overkills[dealer]++;
                            minionHealth[e.Target.Value] = health - amount;
                        }
                        break;

                    case GameEventKind.FatigueDamage when heroSeat >= 0:
                        if (Falls(heroHealth, heroSeat, amount)) FellTo[heroSeat] = FinalBlow.Fatigue;
                        break;

                    case GameEventKind.HealthRestored when e.Target.HasValue:
                        if (heroSeat >= 0) heroHealth[heroSeat] += amount;
                        else if (minionHealth.TryGetValue(e.Target.Value, out int healed))
                            minionHealth[e.Target.Value] = healed + amount;
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
