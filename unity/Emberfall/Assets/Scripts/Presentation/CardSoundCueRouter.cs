using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Unity-free bridge from semantic match events to authored card sound ids. It deliberately
    /// reads presentation snapshots and never feeds anything back into the deterministic engine.
    /// </summary>
    internal sealed class CardSoundCueRouter
    {
        private readonly ICardDatabase _database;
        private readonly Dictionary<InstanceId, CardSnapshot> _cards = new();
        private readonly Dictionary<InstanceId, HeroSnapshot> _heroes = new();
        private readonly Dictionary<InstanceId, int> _lastWeaponByHero = new();
        private readonly Dictionary<PlayerId, int> _seatByPlayer = new();
        private readonly List<CarrierSnapshot>[] _carriers =
            { new List<CarrierSnapshot>(), new List<CarrierSnapshot>() };
        private PlayerId _activePlayer;

        public CardSoundCueRouter(ICardDatabase database)
        {
            _database = database ?? throw new System.ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Captures the newest public snapshot. Instance mappings are retained for the match so a
        /// destruction event can still resolve the card that just left its carrier zone.
        /// </summary>
        public void Capture(GameState state)
        {
            if (state == null) return;
            _activePlayer = state.ActivePlayer;
            _heroes.Clear();
            _seatByPlayer.Clear();
            _carriers[0].Clear();
            _carriers[1].Clear();

            for (int seat = 0; seat < state.Players.Length; seat++)
            {
                PlayerState player = state.Players[seat];
                _seatByPlayer[player.Id] = seat;
                int? weaponDefinitionId = player.Weapon?.DefinitionId;
                _heroes[player.Hero.Id] = new HeroSnapshot(
                    player.Id, player.Hero.HeroPowerId, weaponDefinitionId);
                if (weaponDefinitionId.HasValue)
                    _lastWeaponByHero[player.Hero.Id] = weaponDefinitionId.Value;

                CaptureZone(player.Deck);
                CaptureZone(player.Hand);
                CaptureZone(player.Graveyard);

                for (int i = 0; i < player.Board.Count; i++)
                {
                    CardInstance card = player.Board[i];
                    CaptureCard(card);
                    _carriers[seat].Add(new CarrierSnapshot(
                        card.Id, card.Controller, card.DefinitionId, card.IsSilenced));
                }

                if (player.Weapon != null)
                {
                    CardInstance weapon = player.Weapon;
                    CaptureCard(weapon);
                    _carriers[seat].Add(new CarrierSnapshot(
                        weapon.Id, weapon.Controller, weapon.DefinitionId, weapon.IsSilenced));
                }
            }
        }

        public IReadOnlyList<string> Route(GameEvent matchEvent)
        {
            var sounds = new List<string>();
            switch (matchEvent.Kind)
            {
                case GameEventKind.CardPlayed:
                    if (TryCard(matchEvent.Source, out CardSnapshot played) && !played.IsSilenced &&
                        _database.TryGet(played.DefinitionId, out CardDefinition playedDefinition))
                    {
                        if (playedDefinition.Type == CardType.SPELL)
                        {
                            Append(sounds, playedDefinition, Trigger.ON_CAST);
                            AppendObservers(sounds, Trigger.ON_SPELL_CAST);
                        }
                        else
                        {
                            Append(sounds, playedDefinition, Trigger.ON_PLAY);
                            if (playedDefinition.Type == CardType.MINION)
                                AppendObservers(sounds, Trigger.ON_FRIENDLY_SUMMON,
                                    played.Controller, played.Id);
                        }
                    }
                    break;

                case GameEventKind.WeaponEquipped:
                    if (TryHero(matchEvent.Source, out HeroSnapshot equipHero))
                    {
                        int weaponId = equipHero.WeaponDefinitionId ??
                            (_lastWeaponByHero.TryGetValue(matchEvent.Source.GetValueOrDefault(), out int prior)
                                ? prior
                                : 0);
                        if (weaponId > 0 && _database.TryGet(weaponId, out CardDefinition weapon))
                            Append(sounds, weapon, Trigger.ON_PLAY);
                    }
                    break;

                case GameEventKind.HeroPowerUsed:
                    if (TryHero(matchEvent.Source, out HeroSnapshot powerHero) &&
                        _database.TryGet(powerHero.HeroPowerDefinitionId, out CardDefinition power))
                        Append(sounds, power, Trigger.ON_CAST);
                    break;

                case GameEventKind.AttackDeclared:
                    if (TryCard(matchEvent.Source, out CardSnapshot attacker) && !attacker.IsSilenced &&
                        _database.TryGet(attacker.DefinitionId, out CardDefinition attackerDefinition))
                    {
                        Append(sounds, attackerDefinition, Trigger.ON_ATTACK);
                    }
                    else if (TryHero(matchEvent.Source, out HeroSnapshot attackHero) &&
                             _lastWeaponByHero.TryGetValue(matchEvent.Source.GetValueOrDefault(), out int weaponId) &&
                             _database.TryGet(weaponId, out CardDefinition attackingWeapon))
                    {
                        Append(sounds, attackingWeapon, Trigger.ON_ATTACK);
                    }
                    break;

                case GameEventKind.DamageApplied:
                    AppendCard(sounds, matchEvent.Target, Trigger.ON_DAMAGED);
                    break;

                case GameEventKind.HealthRestored:
                    AppendCard(sounds, matchEvent.Target, Trigger.ON_HEAL);
                    break;

                case GameEventKind.CardDestroyed:
                    AppendCard(sounds, matchEvent.Source, Trigger.ON_DEATH);
                    break;

                case GameEventKind.CardSummoned:
                    if (TryCard(matchEvent.Source, out CardSnapshot summoned))
                        AppendObservers(sounds, Trigger.ON_FRIENDLY_SUMMON,
                            summoned.Controller, summoned.Id);
                    break;

                case GameEventKind.TurnStarted:
                    AppendTurnObservers(sounds, Trigger.ON_TURN_START, matchEvent.Source);
                    break;

                case GameEventKind.TurnEnded:
                    AppendTurnObservers(sounds, Trigger.ON_TURN_END, matchEvent.Source);
                    break;
            }
            return sounds;
        }

        private void AppendCard(List<string> sounds, InstanceId? instance, Trigger trigger)
        {
            if (!TryCard(instance, out CardSnapshot card) || card.IsSilenced) return;
            if (_database.TryGet(card.DefinitionId, out CardDefinition definition))
                Append(sounds, definition, trigger);
        }

        private void AppendTurnObservers(List<string> sounds, Trigger trigger, InstanceId? heroId)
        {
            int firstSeat = 0;
            if (TryHero(heroId, out HeroSnapshot hero))
                firstSeat = SeatOf(hero.Controller);
            AppendObserverSide(sounds, trigger, firstSeat, null, null);
            AppendObserverSide(sounds, trigger, firstSeat == 0 ? 1 : 0, null, null);
        }

        private void AppendObservers(List<string> sounds, Trigger trigger,
            PlayerId? controller = null, InstanceId? excluded = null)
        {
            int firstSeat = SeatOf(_activePlayer);
            AppendObserverSide(sounds, trigger, firstSeat, controller, excluded);
            AppendObserverSide(sounds, trigger, firstSeat == 0 ? 1 : 0, controller, excluded);
        }

        private void AppendObserverSide(List<string> sounds, Trigger trigger, int seat,
            PlayerId? controller, InstanceId? excluded)
        {
            if (seat < 0 || seat >= _carriers.Length) return;
            foreach (CarrierSnapshot carrier in _carriers[seat])
            {
                if (carrier.IsSilenced || (excluded.HasValue && carrier.Id == excluded.Value) ||
                    (controller.HasValue && !carrier.Controller.Equals(controller.Value)))
                    continue;
                if (_database.TryGet(carrier.DefinitionId, out CardDefinition definition))
                    Append(sounds, definition, trigger);
            }
        }

        private static void Append(List<string> sounds, CardDefinition definition, Trigger trigger)
        {
            for (int i = 0; i < definition.SoundEffects.Count; i++)
            {
                CardSoundEffect sound = definition.SoundEffects[i];
                if (sound.Trigger == trigger && !string.IsNullOrEmpty(sound.SoundId))
                    sounds.Add(sound.SoundId);
            }
        }

        private int SeatOf(PlayerId player)
            => _seatByPlayer.TryGetValue(player, out int seat) ? seat : 0;

        private bool TryCard(InstanceId? id, out CardSnapshot card)
        {
            card = default;
            return id.HasValue && _cards.TryGetValue(id.Value, out card);
        }

        private bool TryHero(InstanceId? id, out HeroSnapshot hero)
        {
            hero = default;
            return id.HasValue && _heroes.TryGetValue(id.Value, out hero);
        }

        private void CaptureZone(IReadOnlyList<CardInstance> cards)
        {
            for (int i = 0; i < cards.Count; i++) CaptureCard(cards[i]);
        }

        private void CaptureCard(CardInstance card)
        {
            _cards[card.Id] = new CardSnapshot(
                card.Id, card.Controller, card.DefinitionId, card.IsSilenced);
        }

        private readonly struct CardSnapshot
        {
            public readonly InstanceId Id;
            public readonly PlayerId Controller;
            public readonly int DefinitionId;
            public readonly bool IsSilenced;

            public CardSnapshot(InstanceId id, PlayerId controller, int definitionId, bool isSilenced)
            {
                Id = id;
                Controller = controller;
                DefinitionId = definitionId;
                IsSilenced = isSilenced;
            }
        }

        private readonly struct CarrierSnapshot
        {
            public readonly InstanceId Id;
            public readonly PlayerId Controller;
            public readonly int DefinitionId;
            public readonly bool IsSilenced;

            public CarrierSnapshot(InstanceId id, PlayerId controller, int definitionId, bool isSilenced)
            {
                Id = id;
                Controller = controller;
                DefinitionId = definitionId;
                IsSilenced = isSilenced;
            }
        }

        private readonly struct HeroSnapshot
        {
            public readonly PlayerId Controller;
            public readonly int HeroPowerDefinitionId;
            public readonly int? WeaponDefinitionId;

            public HeroSnapshot(PlayerId controller, int heroPowerDefinitionId, int? weaponDefinitionId)
            {
                Controller = controller;
                HeroPowerDefinitionId = heroPowerDefinitionId;
                WeaponDefinitionId = weaponDefinitionId;
            }
        }
    }
}
