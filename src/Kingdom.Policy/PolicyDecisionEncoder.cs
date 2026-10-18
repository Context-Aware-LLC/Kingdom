using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Kingdom.Policy
{
    /// <summary>
    /// The C# port of the Python <c>DecisionEncoder</c>
    /// (<c>tooling/scripts/training_exporters/exporters.py</c>, feature schema 8): one decision's
    /// scorer request in, the network's input arrays out.
    ///
    /// <para>IT READS THE SAME JSON THE PYTHON SCORER IS SENT, on purpose. The observation, the
    /// player memory and the legal actions arrive exactly as <see cref="CheckpointSeatPolicy"/>
    /// serializes them for the Python process, so the two encoders can be compared on identical
    /// bytes (<c>onnx-parity</c> in NetSoak, and an arena run with <c>--verify-onnx</c>). Python's
    /// semantics are followed where they are surprising, too: a key that is present with a null
    /// value is NOT the same as a missing key (<c>dict.get(key, 0)</c> returns the null), a card id
    /// is matched as Python's <c>str()</c> of the JSON value, and anything the Python encoder
    /// refuses is refused here with <see cref="PolicyEncodingException"/>.</para>
    ///
    /// <para>The target and ongoing-effect axes are padded to the caps the ONNX export was traced
    /// at (see <c>onnx_export.py</c>); a decision wider than either is refused.</para>
    ///
    /// <para>CHANGING THE PYTHON ENCODER CHANGES THIS FILE. The feature lists below are checked
    /// against the model's feature spec when a model loads (<see cref="RequireFeatureSpec"/>), which
    /// catches a renamed or reordered feature; it cannot catch a change in how a feature is
    /// computed. Re-run the parity check after any edit to either encoder.</para>
    /// </summary>
    internal sealed class PolicyDecisionEncoder
    {
        internal const int FeatureSchemaVersion = 8;

        internal static readonly string[] Phases =
            { "<PAD>", "<UNK>", "Mulligan", "StartOfTurn", "Main", "EndOfTurn", "GameOver" };

        internal static readonly string[] ActionTypes =
        {
            "<PAD>", "<UNK>", "play_card", "attack", "hero_power", "end_turn",
            "mulligan", "unearth_choice", "concede", "auto_confirm",
        };

        private static readonly string[] PlayerFeatureNames =
        {
            "hero_health", "hero_max_health", "hero_armor", "hero_is_frosted",
            "mana_max", "mana_available", "mana_spent", "mana_locked", "mana_temporary",
            "deck_count", "hand_count", "board_count", "graveyard_count",
            "board_total_attack", "board_total_health", "board_frosted_count",
            "board_aegis_count", "weapon_present", "weapon_attack", "weapon_durability",
            "hero_power_used", "hero_attacks_used", "next_turn_backdraft_lock",
        };

        internal static readonly string[] ObservationFeatures = BuildObservationFeatures();

        internal const int MaxHandSlots = 10;
        internal const int MaxBoardSlots = 7;

        internal static readonly string[] VisibleEntityKinds =
            { "<PAD>", "hero", "weapon", "hand_card", "board_minion" };

        internal static readonly string[] VisibleEntityLayout = BuildEntityLayout();

        internal static readonly string[] VisibleEntityKeywords =
        {
            "BULWARK", "FERVOR", "SKIRMISH", "AEGIS",
            "LIFEDRAIN", "DOUBLESTRIKE", "SHROUD", "VENOM",
            "ARCANE_SURGE", "SILENCE", "FROST", "BACKDRAFT",
            "REKINDLE", "OVERWHELM", "VANGUARD", "REINFORCE",
        };

        internal static readonly string[] TargetZones =
            { "<PAD>", "hero", "hand", "board", "graveyard", "weapon" };

        internal static readonly string[] VisibleEntityFeatures =
        {
            "current_attack", "current_health", "max_health", "armor", "current_durability",
            "has_aegis_shield", "is_frosted", "frost_thaw_pending", "is_silenced",
            "summoned_this_turn", "attacks_used_this_turn", "has_used_rekindle",
        };

        internal static readonly string[] CardMemoryFeatures =
            { "self_remaining_deck", "self_graveyard", "opponent_graveyard", "known_opponent_hand" };

        private static readonly string[] CardMemoryFields =
            { "selfRemainingDeck", "selfGraveyard", "opponentGraveyard", "knownOpponentHand" };

        internal static readonly string[] OngoingEffectLifetimes =
            { "<PAD>", "<UNK>", "ONE_SHOT", "FINITE", "PERMANENT" };

        private const int Unknown = 1;

        private readonly Dictionary<string, int> _cardIds;
        private readonly Dictionary<string, int> _bindingIds;

        internal PolicyDecisionEncoder(
            IReadOnlyList<string> cardDefinitions,
            IReadOnlyList<string> targetBindings,
            int maxTargets,
            int maxOngoingEffects)
        {
            RequirePadUnkFirst(cardDefinitions, "cardDefinitions");
            RequirePadUnkFirst(targetBindings, "targetBindings");
            if (maxTargets < 1) throw new ArgumentOutOfRangeException(nameof(maxTargets));
            if (maxOngoingEffects < 1) throw new ArgumentOutOfRangeException(nameof(maxOngoingEffects));
            _cardIds = Index(cardDefinitions);
            _bindingIds = Index(targetBindings);
            CardDefinitionCount = cardDefinitions.Count;
            MaxTargets = maxTargets;
            MaxOngoingEffects = maxOngoingEffects;
        }

        internal int CardDefinitionCount { get; }
        internal int MaxTargets { get; }
        internal int MaxOngoingEffects { get; }

        internal IReadOnlyList<int> UnknownCards(IEnumerable<int> cardIds)
        {
            var unknown = new SortedSet<int>();
            foreach (int id in cardIds)
                if (!_cardIds.ContainsKey(id.ToString(CultureInfo.InvariantCulture))) unknown.Add(id);
            return new List<int>(unknown);
        }

        /// <summary>
        /// Refuses a model whose feature spec does not list exactly the features this encoder
        /// computes, in this order. The spec is the Python exporter's <c>_feature_spec()</c>, copied
        /// into <c>bot-model.json</c> at export.
        /// </summary>
        internal static void RequireFeatureSpec(JsonElement spec)
        {
            if (!spec.TryGetProperty("schemaVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != FeatureSchemaVersion)
                throw new InvalidDataException(
                    $"bot model feature spec is not schema {FeatureSchemaVersion}");
            RequireNamed(spec, "observationFeatures", ObservationFeatures);
            RequireNamed(spec, "visibleEntityFeatures", VisibleEntityFeatures);
            RequireNamed(spec, "visibleEntityLayout", VisibleEntityLayout);
            RequireNamed(spec, "cardMemoryFeatures", CardMemoryFeatures);
            RequireStrings(spec, "visibleEntityKeywords", VisibleEntityKeywords);
            RequireStrings(spec, "visibleEntityKinds", VisibleEntityKinds);
            RequireStrings(spec, "targetZones", TargetZones);
            RequireStrings(spec, "ongoingEffectLifetimes", OngoingEffectLifetimes);
        }

        /// <summary>Refuses an action vocabulary whose fixed lists are not this encoder's.</summary>
        internal static void RequireActionVocabulary(JsonElement vocabulary)
        {
            if (!vocabulary.TryGetProperty("schemaVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
                throw new InvalidDataException("bot model action vocabulary is not schema 1");
            RequireStrings(vocabulary, "actionTypes", ActionTypes);
            RequireStrings(vocabulary, "phases", Phases);
        }

        internal PolicyInputs Encode(
            JsonElement observation,
            JsonElement playerMemory,
            IReadOnlyList<JsonElement> legalActions)
        {
            if (observation.ValueKind != JsonValueKind.Object)
                throw Fail("projected observation must be an object");
            if (playerMemory.ValueKind != JsonValueKind.Object)
                throw Fail("player memory must be an object");
            if (legalActions is null || legalActions.Count == 0)
                throw Fail("live decision must contain at least one legal action");
            string? actor = null;
            foreach (JsonElement action in legalActions)
            {
                if (action.ValueKind != JsonValueKind.Object)
                    throw Fail("every legal action must be an object");
                if (!action.TryGetProperty("player", out JsonElement player)
                    || player.ValueKind != JsonValueKind.String)
                    throw Fail("all legal actions must name the same acting player");
                string name = player.GetString()!;
                if (actor is not null && !string.Equals(actor, name, StringComparison.Ordinal))
                    throw Fail("all legal actions must name the same acting player");
                actor = name;
            }

            var inputs = new PolicyInputs(
                legalActions.Count, MaxTargets, MaxOngoingEffects, CardDefinitionCount);
            JsonElement[] players = Players(observation);
            int actorSeat = SeatOf(players, actor!);

            Dictionary<string, int> instanceRows = EncodeVisibleEntities(inputs, players, actorSeat);
            Dictionary<string, Target> targets = TargetMap(players, actor!, instanceRows);
            Dictionary<string, string> instances = VisibleInstanceDefinitions(players);
            EncodeObservation(inputs, observation, players, actor!, actorSeat);
            EncodeMemory(inputs, playerMemory);
            EncodeOngoingEffects(inputs, observation, players, actor!);
            for (int index = 0; index < legalActions.Count; index++)
                EncodeAction(inputs, index, legalActions[index], players, instanceRows, targets, instances);
            return inputs;
        }

        // ── observation ──────────────────────────────────────────────────────────

        private static void EncodeObservation(
            PolicyInputs inputs, JsonElement observation, JsonElement[] players, string actor, int actorSeat)
        {
            float[] values = inputs.Observations;
            int phaseId = Unknown;
            if (observation.TryGetProperty("phase", out JsonElement phase)
                && phase.ValueKind == JsonValueKind.String)
            {
                int found = Array.IndexOf(Phases, phase.GetString());
                if (found >= 0) phaseId = found;
            }
            long[] fatigue =
            {
                IntOr(observation, "fatigueCounter0", 0),
                IntOr(observation, "fatigueCounter1", 0),
            };
            values[0] = (float)FloatOr(observation, "turnNumber", 0);
            values[1] = phaseId;
            values[2] = IsString(observation, "activePlayer", actor) ? 1f : 0f;
            values[3] = (float)FloatOr(observation, "actionResolutionsThisTurn", 0);
            values[4] = fatigue[actorSeat];
            values[5] = fatigue[1 - actorSeat];
            PlayerFeatures(players[actorSeat], values, 6);
            PlayerFeatures(players[1 - actorSeat], values, 6 + PlayerFeatureNames.Length);
        }

        private static void PlayerFeatures(JsonElement player, float[] values, int offset)
        {
            JsonElement hero = RequireObject(Get(player, "hero"), "player.hero");
            JsonElement mana = RequireObject(Get(player, "mana"), "player.mana");
            List<JsonElement> board = ObjectList(player, "board", "player.board");
            JsonElement? weaponValue = Get(player, "weapon");
            JsonElement? weapon = weaponValue is { ValueKind: JsonValueKind.Object } w ? w : null;

            long attack = 0, health = 0, frosted = 0, aegis = 0;
            foreach (JsonElement card in board)
            {
                attack += IntOr(card, "currentAttack", 0);
                health += IntOr(card, "currentHealth", 0);
                if (BoolOr(card, "isFrosted", false)) frosted++;
                if (BoolOr(card, "hasAegisShield", false)) aegis++;
            }
            double durability = 0;
            if (weapon is JsonElement held && Get(held, "currentDurability") is JsonElement d
                && Truthy(d))
                durability = ToFloat(d);

            double[] features =
            {
                FloatOr(hero, "currentHealth", 0), FloatOr(hero, "maxHealth", 0),
                FloatOr(hero, "armor", 0), IsFrosted(hero) ? 1 : 0,
                FloatOr(mana, "max", 0), FloatOr(mana, "available", 0), FloatOr(mana, "spent", 0),
                FloatOr(mana, "locked", 0), FloatOr(mana, "temporary", 0),
                ObjectList(player, "deck", "player.deck").Count,
                ObjectList(player, "hand", "player.hand").Count,
                board.Count,
                ObjectList(player, "graveyard", "player.graveyard").Count,
                attack, health, frosted, aegis,
                weapon is JsonElement present && present.EnumerateObject().MoveNext() ? 1 : 0,
                weapon is JsonElement armed ? FloatOr(armed, "currentAttack", 0) : 0,
                durability,
                BoolOr(player, "heroPowerUsedThisTurn", false) ? 1 : 0,
                FloatOr(player, "heroAttacksUsedThisTurn", 0),
                FloatOr(player, "nextTurnBackdraftLock", 0),
            };
            for (int i = 0; i < features.Length; i++) values[offset + i] = (float)features[i];
        }

        // ── visible entities ─────────────────────────────────────────────────────

        private Dictionary<string, int> EncodeVisibleEntities(
            PolicyInputs inputs, JsonElement[] players, int actorSeat)
        {
            JsonElement own = players[actorSeat];
            JsonElement opponent = players[1 - actorSeat];
            var rows = new Dictionary<string, int>(StringComparer.Ordinal);

            void Put(int index, int kind, JsonElement entity, JsonElement? definition, JsonElement? attacksUsed)
            {
                string slot = VisibleEntityLayout[index];
                JsonElement? id = Get(entity, "id");
                if (id is not { ValueKind: JsonValueKind.String } || InstanceNumber(id) == 0)
                    throw Fail($"visible entity '{slot}' must have a non-redacted instance id");
                string instance = id.Value.GetString()!;
                if (rows.ContainsKey(instance))
                    throw Fail($"visible instance '{instance}' appears more than once");
                var keywords = new List<int>();
                if (Get(entity, "keywords") is JsonElement raw)
                {
                    if (raw.ValueKind != JsonValueKind.Array)
                        throw Fail($"visible entity '{instance}' keywords must be strings");
                    foreach (JsonElement keyword in raw.EnumerateArray())
                    {
                        if (keyword.ValueKind != JsonValueKind.String)
                            throw Fail($"visible entity '{instance}' keywords must be strings");
                        int found = Array.IndexOf(VisibleEntityKeywords, keyword.GetString());
                        if (found < 0)
                            throw Fail($"visible entity '{instance}' has unsupported keywords: {keyword.GetString()}");
                        keywords.Add(found);
                    }
                }
                inputs.VisibleEntityMask[index] = true;
                inputs.VisibleEntityKind[index] = kind;
                inputs.VisibleEntityDefinition[index] = CardId(PyStr(definition));
                JsonElement? durability = Get(entity, "currentDurability");
                double attacks = attacksUsed is JsonElement used && used.ValueKind != JsonValueKind.Null
                    ? ToFloat(used)
                    : FloatOr(entity, "attacksUsedThisTurn", 0);
                double[] features =
                {
                    FloatOr(entity, "currentAttack", 0),
                    FloatOr(entity, "currentHealth", 0),
                    FloatOr(entity, "maxHealth", 0),
                    FloatOr(entity, "armor", 0),
                    durability is JsonElement dur && dur.ValueKind != JsonValueKind.Null ? ToFloat(dur) : 0,
                    BoolOr(entity, "hasAegisShield", false) ? 1 : 0,
                    IsFrosted(entity) ? 1 : 0,
                    FrostThawPending(entity) ? 1 : 0,
                    BoolOr(entity, "isSilenced", false) ? 1 : 0,
                    BoolOr(entity, "summonedThisTurn", false) ? 1 : 0,
                    attacks,
                    BoolOr(entity, "hasUsedRekindle", false) ? 1 : 0,
                };
                int width = VisibleEntityFeatures.Length;
                for (int i = 0; i < width; i++)
                    inputs.VisibleEntityFeatures[index * width + i] = (float)features[i];
                foreach (int keyword in keywords)
                    inputs.VisibleEntityKeywords[index * VisibleEntityKeywords.Length + keyword] = 1f;
                rows[instance] = index;
            }

            JsonElement ownHero = RequireObject(Get(own, "hero"), "self.hero");
            JsonElement opponentHero = RequireObject(Get(opponent, "hero"), "opponent.hero");
            Put(1, 1, ownHero, Get(ownHero, "heroPowerId"), GetOr(own, "heroAttacksUsedThisTurn"));
            Put(2, 1, opponentHero, Get(opponentHero, "heroPowerId"),
                GetOr(opponent, "heroAttacksUsedThisTurn"));
            if (Get(own, "weapon") is { ValueKind: JsonValueKind.Object } ownWeapon)
                Put(3, 2, ownWeapon, Get(ownWeapon, "definitionId"), null);
            if (Get(opponent, "weapon") is { ValueKind: JsonValueKind.Object } opponentWeapon)
                Put(4, 2, opponentWeapon, Get(opponentWeapon, "definitionId"), null);

            List<JsonElement> hand = ObjectList(own, "hand", "self.hand");
            List<JsonElement> ownBoard = ObjectList(own, "board", "self.board");
            List<JsonElement> opponentBoard = ObjectList(opponent, "board", "opponent.board");
            if (hand.Count > MaxHandSlots)
                throw Fail($"self hand exceeds {MaxHandSlots} cards");
            if (ownBoard.Count > MaxBoardSlots || opponentBoard.Count > MaxBoardSlots)
                throw Fail($"board exceeds {MaxBoardSlots} minions");
            for (int slot = 0; slot < hand.Count; slot++)
                Put(5 + slot, 3, hand[slot], Get(hand[slot], "definitionId"), null);
            for (int slot = 0; slot < ownBoard.Count; slot++)
                Put(15 + slot, 4, ownBoard[slot], Get(ownBoard[slot], "definitionId"), null);
            for (int slot = 0; slot < opponentBoard.Count; slot++)
                Put(22 + slot, 4, opponentBoard[slot], Get(opponentBoard[slot], "definitionId"), null);
            return rows;
        }

        /// <summary>Python passes <c>own.get("heroAttacksUsedThisTurn", 0)</c>: a missing key is 0,
        /// a null one is None, and None means "read the entity's own field".</summary>
        private static JsonElement? GetOr(JsonElement player, string name)
            => Get(player, name) ?? Zero;

        private static readonly JsonElement Zero = JsonDocument.Parse("0").RootElement.Clone();

        // ── targets ──────────────────────────────────────────────────────────────

        private readonly record struct Target(int Entity, int Definition, int Zone, int ControllerIsSelf);

        private Dictionary<string, Target> TargetMap(
            JsonElement[] players, string actor, Dictionary<string, int> instanceRows)
        {
            var result = new Dictionary<string, Target>(StringComparer.Ordinal);

            void Put(JsonElement? value, int zone, bool controllerIsSelf, JsonElement? definition, bool hasDefinition)
            {
                if (value is not { ValueKind: JsonValueKind.Object } entity) return;
                JsonElement? id = Get(entity, "id");
                if (id is not { ValueKind: JsonValueKind.String } || InstanceNumber(id) == 0)
                    throw Fail($"public {TargetZones[zone]} target must have a non-redacted instance id");
                string instance = id.Value.GetString()!;
                if (result.ContainsKey(instance))
                    throw Fail($"public target instance '{instance}' appears more than once");
                // Python: card_ids.get(str(definition), 1) if definition not in (None, "") else 0.
                int encoded = !hasDefinition || definition is null
                              || definition.Value.ValueKind == JsonValueKind.Null
                              || (definition.Value.ValueKind == JsonValueKind.String
                                  && definition.Value.GetString()!.Length == 0)
                    ? 0
                    : CardId(PyStr(definition));
                result[instance] = new Target(
                    instanceRows.TryGetValue(instance, out int row) ? row : 0,
                    encoded, zone, controllerIsSelf ? 1 : 0);
            }

            foreach (JsonElement player in players)
            {
                bool self = IsString(player, "id", actor);
                Put(Get(player, "hero"), 1, self, null, hasDefinition: false);
                if (Get(player, "weapon") is { ValueKind: JsonValueKind.Object } weapon)
                    Put(weapon, 5, self, Get(weapon, "definitionId"), hasDefinition: true);
                if (self)
                    foreach (JsonElement card in ObjectList(player, "hand", "self.hand"))
                        Put(card, 2, true, Get(card, "definitionId"), hasDefinition: true);
                foreach (JsonElement card in ObjectList(player, "board", "player.board"))
                    Put(card, 3, self, Get(card, "definitionId"), hasDefinition: true);
                foreach (JsonElement card in ObjectList(player, "graveyard", "player.graveyard"))
                    Put(card, 4, self, Get(card, "definitionId"), hasDefinition: true);
            }
            return result;
        }

        private static Dictionary<string, string> VisibleInstanceDefinitions(JsonElement[] players)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement player in players)
            {
                foreach (string zone in new[] { "deck", "hand", "board", "graveyard" })
                {
                    if (Get(player, zone) is not { ValueKind: JsonValueKind.Array } cards) continue;
                    foreach (JsonElement card in cards.EnumerateArray())
                        Remember(card);
                }
                if (Get(player, "weapon") is { ValueKind: JsonValueKind.Object } weapon)
                    Remember(weapon);
            }
            return result;

            void Remember(JsonElement card)
            {
                if (card.ValueKind != JsonValueKind.Object) return;
                // Python keys this dict by the raw id; only a string id can ever match an action's
                // source, which is always a string.
                if (Get(card, "id") is not { ValueKind: JsonValueKind.String } id) return;
                JsonElement? definition = Get(card, "definitionId");
                result[id.GetString()!] = definition is null ? "" : PyStr(definition);
            }
        }

        // ── memory and ongoing effects ───────────────────────────────────────────

        private void EncodeMemory(PolicyInputs inputs, JsonElement memory)
        {
            int width = CardMemoryFeatures.Length;
            for (int feature = 0; feature < CardMemoryFields.Length; feature++)
            {
                string field = CardMemoryFields[feature];
                if (Get(memory, field) is not { ValueKind: JsonValueKind.Array } values)
                    throw Fail($"playerMemory.{field} must be an array");
                foreach (JsonElement card in values.EnumerateArray())
                    inputs.CardMemoryCounts[CardId(PyStr(card)) * width + feature] += 1f;
            }
        }

        private void EncodeOngoingEffects(
            PolicyInputs inputs, JsonElement observation, JsonElement[] players, string actor)
        {
            JsonElement? raw = Get(observation, "ongoingEffects");
            if (raw is null) return;
            if (raw.Value.ValueKind != JsonValueKind.Array)
                throw Fail("observation.ongoingEffects must be an array");
            int index = 0;
            foreach (JsonElement value in raw.Value.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object)
                    throw Fail($"ongoingEffects[{index}] must be an object");
                JsonElement? controller = Get(value, "controller");
                if (controller is not { ValueKind: JsonValueKind.String } named
                    || (!IsString(players[0], "id", named.GetString()!)
                        && !IsString(players[1], "id", named.GetString()!)))
                    throw Fail($"ongoingEffects[{index}] has an unknown controller");
                JsonElement? source = Get(value, "sourceDefinitionId");
                string key = source is null || source.Value.ValueKind == JsonValueKind.Null
                    ? ""
                    : PyStr(source);
                if (!_cardIds.TryGetValue(key, out int definition))
                    throw Fail($"ongoingEffects[{index}] lacks a known sourceDefinitionId");
                int lifetime = Unknown;
                bool permanent = false;
                if (Get(value, "lifetime") is { ValueKind: JsonValueKind.String } named2)
                {
                    int found = Array.IndexOf(OngoingEffectLifetimes, named2.GetString());
                    if (found >= 0) lifetime = found;
                    permanent = named2.GetString() == "PERMANENT";
                }
                JsonElement? remaining = Get(value, "remainingUses");
                double uses;
                if (permanent)
                {
                    if (remaining is JsonElement r && r.ValueKind != JsonValueKind.Null)
                        throw Fail($"ongoingEffects[{index}] permanent effect must not declare remainingUses");
                    uses = 0;
                }
                else
                {
                    if (remaining is not { ValueKind: JsonValueKind.Number } count
                        || !count.TryGetInt64(out long whole) || whole < 0)
                        throw Fail($"ongoingEffects[{index}] bounded effect must declare a nonnegative integer remainingUses");
                    uses = whole;
                }
                if (index >= MaxOngoingEffects)
                    throw Fail($"decision has more than {MaxOngoingEffects} ongoing effects");
                inputs.OngoingEffectMask[index] = true;
                inputs.OngoingEffectDefinition[index] = definition;
                inputs.OngoingEffectControllerIsSelf[index] = named.GetString() == actor ? 1f : 0f;
                inputs.OngoingEffectLifetime[index] = lifetime;
                inputs.OngoingEffectRemainingUses[index] = (float)uses;
                index++;
            }
        }

        // ── actions ──────────────────────────────────────────────────────────────

        private void EncodeAction(
            PolicyInputs inputs,
            int index,
            JsonElement action,
            JsonElement[] players,
            Dictionary<string, int> instanceRows,
            Dictionary<string, Target> targetMap,
            Dictionary<string, string> instances)
        {
            JsonElement? tagValue = Get(action, "$type");
            string? tag = tagValue is { ValueKind: JsonValueKind.String } t ? t.GetString() : null;
            int typeId = tag is null ? Unknown : Array.IndexOf(ActionTypes, tag);
            if (typeId < 0) typeId = Unknown;
            if (typeId == Unknown)
                throw Fail($"unsupported legal action type '{tag}'");

            JsonElement? sourceWire = tag == "play_card" ? Get(action, "card") : Get(action, "attacker");
            if (sourceWire is { ValueKind: JsonValueKind.Null }) sourceWire = null;
            InstanceNumber(sourceWire);
            int sourceEntity = 0;
            if (sourceWire is JsonElement wire && Truthy(wire))
                sourceEntity = instanceRows.TryGetValue(PyStr(wire), out int row) ? row : 0;
            if (sourceWire is not null && sourceEntity == 0)
                throw Fail($"legal action source {PyStr(sourceWire)} is absent from visible entities");
            int sourceDefinition = 0;
            if (sourceWire is { ValueKind: JsonValueKind.String } sourceId
                && instances.TryGetValue(sourceId.GetString()!, out string? definitionKey))
                sourceDefinition = CardId(definitionKey);
            if (tag == "hero_power")
            {
                sourceEntity = 1;
                JsonElement? owner = Get(action, "player");
                foreach (JsonElement player in players)
                {
                    if (owner is not { ValueKind: JsonValueKind.String } name
                        || !IsString(player, "id", name.GetString()!))
                        continue;
                    if (Get(player, "hero") is { ValueKind: JsonValueKind.Object } hero)
                        sourceDefinition = CardId(PyStrOr(hero, "heroPowerId", ""));
                }
            }
            if (tag == "unearth_choice")
                sourceDefinition = CardId(PyStrOr(action, "chosenCardId", ""));

            var targetValues = new List<JsonElement>();
            if (tag == "attack" && action.TryGetProperty("defender", out JsonElement defender))
                targetValues.Add(defender);
            if (Get(action, "targets") is { ValueKind: JsonValueKind.Array } raw)
                foreach (JsonElement target in raw.EnumerateArray()) targetValues.Add(target);
            if (tag == "mulligan" && Get(action, "toReplace") is { ValueKind: JsonValueKind.Array } replacements)
                foreach (JsonElement target in replacements.EnumerateArray()) targetValues.Add(target);
            if (targetValues.Count > MaxTargets)
                throw Fail($"legal action has more than {MaxTargets} targets");

            int targetBase = index * MaxTargets;
            for (int slot = 0; slot < targetValues.Count; slot++)
            {
                JsonElement target = targetValues[slot];
                int binding;
                JsonElement? instance;
                if (target.ValueKind == JsonValueKind.Object)
                {
                    instance = Get(target, "instance");
                    binding = _bindingIds.TryGetValue(PyStrOr(target, "binding", ""), out int id) ? id : Unknown;
                }
                else
                {
                    instance = target;
                    binding = 0;
                }
                if (instance is { ValueKind: JsonValueKind.Null }) instance = null;
                InstanceNumber(instance);
                if (!targetMap.TryGetValue(PyStr(instance), out Target encoded))
                    throw Fail($"legal action target {PyStr(instance)} is absent from actor-visible targets");
                int at = targetBase + slot;
                inputs.LegalActionTargetMask[at] = true;
                inputs.LegalActionTargetBinding[at] = binding;
                inputs.LegalActionTargetEntity[at] = encoded.Entity;
                inputs.LegalActionTargetDefinition[at] = encoded.Definition;
                inputs.LegalActionTargetZone[at] = encoded.Zone;
                inputs.LegalActionTargetControllerIsSelf[at] = encoded.ControllerIsSelf;
            }

            inputs.LegalActionMask[index] = true;
            inputs.LegalActionType[index] = typeId;
            inputs.LegalActionSourceDefinition[index] = sourceDefinition;
            inputs.LegalActionSourceEntity[index] = sourceEntity;
            inputs.LegalActionSlot[index] = Get(action, "slot") is JsonElement slotValue
                ? ToInt(slotValue)
                : -1;
            if (Get(action, "choiceSequence") is JsonElement sequence) ToInt(sequence);
        }

        // ── Python semantics ─────────────────────────────────────────────────────

        private static JsonElement[] Players(JsonElement observation)
        {
            if (Get(observation, "players") is not { ValueKind: JsonValueKind.Array } players
                || players.GetArrayLength() != 2)
                throw Fail("observation must contain exactly two players");
            var result = new JsonElement[2];
            int index = 0;
            foreach (JsonElement player in players.EnumerateArray())
            {
                if (player.ValueKind != JsonValueKind.Object)
                    throw Fail("observation must contain exactly two players");
                result[index++] = player;
            }
            return result;
        }

        private static int SeatOf(JsonElement[] players, string actor)
        {
            for (int seat = 0; seat < players.Length; seat++)
                if (IsString(players[seat], "id", actor)) return seat;
            throw Fail($"actor '{actor}' is absent from observation");
        }

        private int CardId(string key) => _cardIds.TryGetValue(key, out int id) ? id : Unknown;

        /// <summary>The value at <paramref name="name"/>, or null when the key is ABSENT. A key that
        /// is present with a JSON null comes back as a Null element, as Python's get does.</summary>
        private static JsonElement? Get(JsonElement value, string name)
            => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement found)
                ? found
                : null;

        private static bool IsString(JsonElement value, string name, string expected)
            => Get(value, name) is { ValueKind: JsonValueKind.String } found
               && string.Equals(found.GetString(), expected, StringComparison.Ordinal);

        private static JsonElement RequireObject(JsonElement? value, string name)
            => value is { ValueKind: JsonValueKind.Object } found ? found : throw Fail($"{name} must be an object");

        /// <summary><c>_object_list(value.get(name, []), label)</c>: absent is empty; anything but an
        /// array of objects, null included, is refused.</summary>
        private static List<JsonElement> ObjectList(JsonElement value, string name, string label)
        {
            var result = new List<JsonElement>();
            JsonElement? list = Get(value, name);
            if (list is null) return result;
            if (list.Value.ValueKind != JsonValueKind.Array)
                throw Fail($"{label} must be an array of objects");
            foreach (JsonElement item in list.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw Fail($"{label} must be an array of objects");
                result.Add(item);
            }
            return result;
        }

        /// <summary><c>float(value.get(name, fallback))</c>.</summary>
        private static double FloatOr(JsonElement value, string name, double fallback)
            => Get(value, name) is JsonElement found ? ToFloat(found) : fallback;

        /// <summary><c>int(value.get(name, fallback))</c>.</summary>
        private static long IntOr(JsonElement value, string name, long fallback)
            => Get(value, name) is JsonElement found ? ToInt(found) : fallback;

        /// <summary><c>bool(value.get(name, fallback))</c>.</summary>
        private static bool BoolOr(JsonElement value, string name, bool fallback)
            => Get(value, name) is JsonElement found ? Truthy(found) : fallback;

        private static double ToFloat(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => throw Fail($"expected a number, found {value.ValueKind}"),
        };

        private static long ToInt(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt64(out long whole) ? whole : (long)Math.Truncate(value.GetDouble()),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => throw Fail($"expected an integer, found {value.ValueKind}"),
        };

        private static bool Truthy(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.Number => value.GetDouble() != 0,
            JsonValueKind.String => value.GetString()!.Length > 0,
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => value.EnumerateObject().MoveNext(),
        };

        /// <summary>Python's <c>str()</c> of a JSON value, as far as a vocabulary lookup can tell:
        /// an absent value is <c>None</c>.</summary>
        private static string PyStr(JsonElement? value)
        {
            if (value is null) return "None";
            JsonElement found = value.Value;
            return found.ValueKind switch
            {
                JsonValueKind.String => found.GetString()!,
                JsonValueKind.Null => "None",
                JsonValueKind.True => "True",
                JsonValueKind.False => "False",
                JsonValueKind.Number => found.TryGetInt64(out long whole)
                    ? whole.ToString(CultureInfo.InvariantCulture)
                    : "float:" + found.GetRawText(),
                // A container never matches a vocabulary entry, in either language.
                _ => "\0container",
            };
        }

        /// <summary><c>str(value.get(name, fallback))</c>.</summary>
        private static string PyStrOr(JsonElement value, string name, string fallback)
            => Get(value, name) is JsonElement found ? PyStr(found) : fallback;

        /// <summary><c>_instance_number</c>: None is 0, anything else must be <c>i&lt;digits&gt;</c>.</summary>
        private static long InstanceNumber(JsonElement? value)
        {
            if (value is null || value.Value.ValueKind == JsonValueKind.Null) return 0;
            string text = PyStr(value);
            if (text.Length < 2 || text[0] != 'i')
                throw Fail($"unsupported instance id '{text}'; expected wire form 'i<number>'");
            long number = 0;
            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9')
                    throw Fail($"unsupported instance id '{text}'; expected wire form 'i<number>'");
                number = unchecked(number * 10 + (c - '0'));
            }
            return number;
        }

        private static List<JsonElement> FrostEffects(JsonElement entity)
        {
            var result = new List<JsonElement>();
            if (Get(entity, "effects") is not { ValueKind: JsonValueKind.Array } effects) return result;
            foreach (JsonElement effect in effects.EnumerateArray())
                if (effect.ValueKind == JsonValueKind.Object && IsString(effect, "kind", "Frost"))
                    result.Add(effect);
            return result;
        }

        private static bool IsFrosted(JsonElement entity)
            => Get(entity, "isFrosted") is JsonElement flag ? Truthy(flag) : FrostEffects(entity).Count > 0;

        private static bool FrostThawPending(JsonElement entity)
        {
            if (Get(entity, "frostThawPending") is JsonElement flag) return Truthy(flag);
            foreach (JsonElement effect in FrostEffects(entity))
                if (IntOr(effect, "turnsRemaining", 0) > 0) return true;
            return false;
        }

        private static PolicyEncodingException Fail(string message) => new PolicyEncodingException(message);

        // ── construction ─────────────────────────────────────────────────────────

        private static string[] BuildObservationFeatures()
        {
            var names = new List<string>
            {
                "turn_number", "phase_id", "actor_is_active", "action_resolutions_this_turn",
                "self_fatigue", "opponent_fatigue",
            };
            foreach (string side in new[] { "self", "opponent" })
                foreach (string name in PlayerFeatureNames)
                    names.Add($"{side}_{name}");
            return names.ToArray();
        }

        private static string[] BuildEntityLayout()
        {
            var names = new List<string>
            {
                "padding", "self_hero", "opponent_hero", "self_weapon", "opponent_weapon",
            };
            for (int slot = 0; slot < MaxHandSlots; slot++) names.Add($"self_hand_{slot}");
            for (int slot = 0; slot < MaxBoardSlots; slot++) names.Add($"self_board_{slot}");
            for (int slot = 0; slot < MaxBoardSlots; slot++) names.Add($"opponent_board_{slot}");
            return names.ToArray();
        }

        private static Dictionary<string, int> Index(IReadOnlyList<string> values)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < values.Count; i++) result[values[i]] = i;
            return result;
        }

        private static void RequirePadUnkFirst(IReadOnlyList<string> values, string name)
        {
            if (values is null || values.Count < 2 || values[0] != "<PAD>" || values[1] != "<UNK>")
                throw new InvalidDataException($"action vocabulary {name} must be a PAD/UNK-first string array");
        }

        private static void RequireNamed(JsonElement spec, string field, string[] expected)
        {
            if (!spec.TryGetProperty(field, out JsonElement list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() != expected.Length)
                throw new InvalidDataException($"bot model feature spec {field} does not match this encoder");
            int index = 0;
            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (!IsString(entry, "name", expected[index])
                    || entry.GetProperty("index").GetInt32() != index)
                    throw new InvalidDataException(
                        $"bot model feature spec {field}[{index}] is not '{expected[index]}'");
                index++;
            }
        }

        private static void RequireStrings(JsonElement spec, string field, string[] expected)
        {
            if (!spec.TryGetProperty(field, out JsonElement list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() != expected.Length)
                throw new InvalidDataException($"bot model {field} does not match this encoder");
            int index = 0;
            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() != expected[index])
                    throw new InvalidDataException($"bot model {field}[{index}] is not '{expected[index]}'");
                index++;
            }
        }
    }

    /// <summary>The network's input arrays for ONE decision, flattened row-major, already in the
    /// dtypes the ONNX graph takes (<c>to_torch_batch</c>). The batch axis is 1.</summary>
    internal sealed class PolicyInputs
    {
        internal PolicyInputs(int actions, int targets, int effects, int cardDefinitions)
        {
            int entities = PolicyDecisionEncoder.VisibleEntityLayout.Length;
            Actions = actions;
            Targets = targets;
            Effects = effects;
            CardDefinitions = cardDefinitions;
            Observations = new float[PolicyDecisionEncoder.ObservationFeatures.Length];
            VisibleEntityMask = new bool[entities];
            VisibleEntityKind = new long[entities];
            VisibleEntityDefinition = new long[entities];
            VisibleEntityFeatures = new float[entities * PolicyDecisionEncoder.VisibleEntityFeatures.Length];
            VisibleEntityKeywords = new float[entities * PolicyDecisionEncoder.VisibleEntityKeywords.Length];
            CardMemoryCounts = new float[cardDefinitions * PolicyDecisionEncoder.CardMemoryFeatures.Length];
            OngoingEffectMask = new bool[effects];
            OngoingEffectDefinition = new long[effects];
            OngoingEffectControllerIsSelf = new float[effects];
            OngoingEffectLifetime = new long[effects];
            OngoingEffectRemainingUses = new float[effects];
            LegalActionMask = new bool[actions];
            LegalActionType = new long[actions];
            LegalActionSourceDefinition = new long[actions];
            LegalActionSourceEntity = new long[actions];
            LegalActionSlot = new float[actions];
            LegalActionTargetMask = new bool[actions * targets];
            LegalActionTargetBinding = new long[actions * targets];
            LegalActionTargetEntity = new long[actions * targets];
            LegalActionTargetDefinition = new long[actions * targets];
            LegalActionTargetZone = new long[actions * targets];
            LegalActionTargetControllerIsSelf = new float[actions * targets];
        }

        internal int Actions { get; }
        internal int Targets { get; }
        internal int Effects { get; }
        internal int CardDefinitions { get; }

        internal float[] Observations { get; }
        internal bool[] VisibleEntityMask { get; }
        internal long[] VisibleEntityKind { get; }
        internal long[] VisibleEntityDefinition { get; }
        internal float[] VisibleEntityFeatures { get; }
        internal float[] VisibleEntityKeywords { get; }
        internal float[] CardMemoryCounts { get; }
        internal bool[] OngoingEffectMask { get; }
        internal long[] OngoingEffectDefinition { get; }
        internal float[] OngoingEffectControllerIsSelf { get; }
        internal long[] OngoingEffectLifetime { get; }
        internal float[] OngoingEffectRemainingUses { get; }
        internal bool[] LegalActionMask { get; }
        internal long[] LegalActionType { get; }
        internal long[] LegalActionSourceDefinition { get; }
        internal long[] LegalActionSourceEntity { get; }
        internal float[] LegalActionSlot { get; }
        internal bool[] LegalActionTargetMask { get; }
        internal long[] LegalActionTargetBinding { get; }
        internal long[] LegalActionTargetEntity { get; }
        internal long[] LegalActionTargetDefinition { get; }
        internal long[] LegalActionTargetZone { get; }
        internal float[] LegalActionTargetControllerIsSelf { get; }
    }

    /// <summary>A decision the encoder refuses, as the Python encoder's <c>ExportError</c> would.
    /// A seat that gets one cannot be scored by the model on this move.</summary>
    public sealed class PolicyEncodingException : Exception
    {
        public PolicyEncodingException(string message) : base(message) { }
    }
}
