using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
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
                            AppendActor(sounds, playedDefinition, Trigger.ON_CAST);
                            AppendObservers(sounds, Trigger.ON_SPELL_CAST);
                        }
                        else
                        {
                            AppendActor(sounds, playedDefinition, Trigger.ON_PLAY);
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
                            AppendActor(sounds, weapon, Trigger.ON_PLAY);
                    }
                    break;

                case GameEventKind.HeroPowerUsed:
                    if (TryHero(matchEvent.Source, out HeroSnapshot powerHero) &&
                        _database.TryGet(powerHero.HeroPowerDefinitionId, out CardDefinition power))
                        AppendActor(sounds, power, Trigger.ON_CAST);
                    break;

                case GameEventKind.AttackDeclared:
                    if (TryCard(matchEvent.Source, out CardSnapshot attacker) && !attacker.IsSilenced &&
                        _database.TryGet(attacker.DefinitionId, out CardDefinition attackerDefinition))
                    {
                        AppendActor(sounds, attackerDefinition, Trigger.ON_ATTACK);
                    }
                    else if (TryHero(matchEvent.Source, out HeroSnapshot attackHero) &&
                             _lastWeaponByHero.TryGetValue(matchEvent.Source.GetValueOrDefault(), out int weaponId) &&
                             _database.TryGet(weaponId, out CardDefinition attackingWeapon))
                    {
                        AppendActor(sounds, attackingWeapon, Trigger.ON_ATTACK);
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
                    {
                        // A token arriving is the same event to a player as a minion being played,
                        // so it gets the same sound. This is the ONLY actor-side raise of ON_PLAY
                        // outside CardPlayed, and it is sound-only: an authored ON_PLAY row on a
                        // token now fires here too, which is what an author writing "when this
                        // arrives" already means.
                        if (!summoned.IsSilenced &&
                            _database.TryGet(summoned.DefinitionId, out CardDefinition summonedDef))
                            AppendActor(sounds, summonedDef, Trigger.ON_PLAY);
                        AppendObservers(sounds, Trigger.ON_FRIENDLY_SUMMON,
                            summoned.Controller, summoned.Id);
                    }
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
                AppendActor(sounds, definition, trigger);
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

        /// <summary>
        /// A sound for the card that just ACTED, falling back to the shared library when the card
        /// authored no row for this trigger. Only the actor gets a fallback: the observer paths
        /// (<see cref="AppendObserverSide"/>, <see cref="AppendTurnObservers"/>) call
        /// <see cref="Append"/> directly and must keep doing so, because they run once per carrier
        /// on the board and a defaulted sound there is one clip per minion, not one per event.
        /// </summary>
        private static void AppendActor(List<string> sounds, CardDefinition definition, Trigger trigger)
        {
            int before = sounds.Count;
            Append(sounds, definition, trigger);
            if (sounds.Count > before) return;   // an authored row always wins over the default
            string fallback = DefaultSoundId(definition, trigger);
            if (!string.IsNullOrEmpty(fallback)) sounds.Add(fallback);
        }

        /// <summary>
        /// The shared-library default for a (type, trigger) pair, or an EMPTY STRING where the
        /// library has nothing — empty rather than null so the file needs no nullable annotation:
        /// the test project compiles it with `Nullable` on and Unity compiles it without, and a
        /// `string?` that satisfies one is a syntax error waiting in the other. This is why <c>soundEffects[]</c> is empty on almost every card: a row is for
        /// a card that wants something OTHER than its class's sound, not the way a card gets one.
        /// </summary>
        /// <remarks>
        /// <para><b>Phase 1 and the summons of <c>tooling/sfx/sound-library.md</c> are here</b> — the nine
        /// class casts, the hero power, and the art-keyed minion summons. The remaining phase-2
        /// stems (attacks, deaths, weapons) are deliberately ABSENT rather than written ahead: a default naming a
        /// clip that does not exist is silence plus an Addressables warning on every such event,
        /// which reads exactly like a broken router.</para>
        ///
        /// <para><b>The class token is the enum member lowercased, and there is no name map on
        /// purpose.</b> <c>HeroVoiceDirector</c> derives <c>vo_&lt;class&gt;_&lt;moment&gt;_&lt;n&gt;</c>
        /// the same way. A second, shorter vocabulary for the same eight classes buys nothing and
        /// costs a silent miss — an unresolved address is a warning at most and the call site
        /// still looks correct.</para>
        ///
        /// <para>RETIREMENT TRIGGER: adding a phase-2 clip means adding its case HERE, in the same
        /// commit that lands the file under <c>Assets/Sound/SFX/</c> — and dropping the phase-1
        /// caveat above once the table is complete.</para>
        /// </remarks>
        private static string DefaultSoundId(CardDefinition definition, Trigger trigger)
        {
            if (trigger == Trigger.ON_PLAY && definition.Type == CardType.MINION)
                // Keyed on what the ART shows, not on class or cost. See MinionSummonArchetypes,
                // which also explains why three archetypes deliberately resolve to nothing.
                return MinionSummonArchetypes.Resolve(definition);

            if (trigger != Trigger.ON_CAST) return string.Empty;
            switch (definition.Type)
            {
                case CardType.SPELL:
                    return definition.Class.ToString().ToLowerInvariant() + "_spell_cast";
                case CardType.HERO_POWER:
                    // One clip for all eight was the sheet's call, on the grounds that the class
                    // already announces itself through its cast sound. That holds for a power that
                    // throws something and breaks for one that does not: CLOCKWRIGHT's Blueprint
                    // draws a card, and a magical surge is the wrong GESTURE for rummaging through
                    // paper (Sam, 2026-08-28).
                    //
                    // An explicit list, not "<class>_hero_power" with a fallback, because a class
                    // gets its own clip only when someone recorded one -- and a derived name for a
                    // clip nobody recorded is an Addressables warning on every activation, which is
                    // the single most frequent event in the game after drawing.
                    switch (definition.Class)
                    {
                        case CardClass.CLOCKWRIGHT: return "clockwright_hero_power";
                        default: return "hero_power_activate";
                    }
                default:
                    return string.Empty;
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
