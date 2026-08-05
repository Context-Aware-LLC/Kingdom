using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Card Authoring EditorWindow (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M1-KICKOFF.md Phase B, dev-docs/pipelines/card-authoring-and-import.md:9). Edits the
    /// canonical JSON row (M1.cards.json today — see CardJsonRowIO), never the baked SO directly.
    /// Every enum control is a real C# enum popup (EnumPopup/Toggle), so a designer cannot type an
    /// unknown verb/keyword/selector — invalid effects are structurally unrepresentable in the UI.
    ///
    /// Built with IMGUI rather than the spec's UI Toolkit layout: this pass is verified headlessly
    /// via Unity MCP (console output, menu items) without a live visual UI Toolkit debugging loop,
    /// and IMGUI is far more reliably correct to write blind. Functionally equivalent for M1's bar
    /// (exit criterion #3: no per-card C#, card #21 is "art + data row").
    /// </summary>
    public sealed class CardAuthoringWindow : EditorWindow
    {
        private static readonly string[] SelectorOptions =
        {
            "(none)", "SELF", "FRIENDLY_MINIONS", "ENEMY_MINIONS", "ALL_MINIONS", "FRIENDLY_HERO",
            "ENEMY_HERO", "ANY_CHARACTER", "ANY_FRIENDLY", "ANY_ENEMY", "ANY_MINION",
            "ANY_FRIENDLY_MINION", "ADJACENT", "TRIGGER_SOURCE",
            "FRIENDLY_WEAPON", "ENEMY_WEAPON",
            "FRIENDLY_HAND", "ENEMY_HAND", "ANY_HAND_CARD",
            "FRIENDLY_DECK", "ENEMY_DECK", "ANY_DECK_CARD",
            "FRIENDLY_GRAVEYARD", "ENEMY_GRAVEYARD", "ANY_GRAVEYARD_CARD",
        };

        private static readonly Keyword[] CarrierKeywordOptions =
            Enum.GetValues(typeof(Keyword)).Cast<Keyword>()
                .Where(KeywordSemantics.IsCarrierBehavior).ToArray();

        private static readonly string[] CarrierKeywordNames =
            CarrierKeywordOptions.Select(keyword => keyword.ToString()).ToArray();

        [MenuItem("Emberfall/Card Authoring Tool")]
        public static void Open() => GetWindow<CardAuthoringWindow>("Card Authoring");

        [MenuItem("Emberfall/Validate All Cards")]
        public static void ValidateAllMenuItem() => CardSetValidator.RunAndLog();

        private readonly List<EditableCard> _cards = new List<EditableCard>();
        private readonly HashSet<string> _dirtyIds = new HashSet<string>();
        private int _selected = -1;
        private string _search = "";
        private Vector2 _listScroll, _editScroll;

        // Assigned in OnEnable, NOT as field initializers: AddressablesArtCatalog's constructor reads
        // AddressableAssetSettingsDefaultObject.Settings, which calls AssetDatabase.IsValidFolder — and
        // Unity forbids that from a ScriptableObject constructor / instance field initializer (an
        // EditorWindow is a ScriptableObject). Doing it in a field initializer threw, left _artCatalog
        // null, and the per-repaint NRE in Lint collapsed the window to its title bar.
        private ICardValidator _validator;
        private IContentLinter _linter;
        private IAddressableArtCatalog _artCatalog;
        private IBalanceGuardrails _guardrails;

        private void OnEnable()
        {
            minSize = new Vector2(760, 480);   // never collapse to a bare title strip again
            _validator  = new CardValidator();
            _linter     = new ContentLinter();
            _artCatalog = new AddressablesArtCatalog();
            _guardrails = new StubBalanceGuardrails();
            LoadAll();
        }

        private void LoadAll()
        {
            _cards.Clear();
            foreach (var (def, _) in CardJsonRowIO.LoadRows(CardJsonRowIO.DefaultM1JsonPath))
                _cards.Add(EditableCard.FromDefinition(def));
            _dirtyIds.Clear();
            if (_selected >= _cards.Count) _selected = _cards.Count - 1;
        }

        private ICardDatabase LiveDb() => new InMemoryCardDatabase(_cards.Select(c => c.ToDefinition()));

        private (ValidationReport core, ValidationReport lint) Validate(EditableCard card)
        {
            var def = card.ToDefinition();
            var db = LiveDb();
            return (_validator.Validate(def, db), _linter.Lint(def, _artCatalog, _guardrails, db));
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.BeginHorizontal();
            DrawLibraryList();
            DrawEditorPane();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(50))) OnNew();
            if (GUILayout.Button("Duplicate", EditorStyles.toolbarButton, GUILayout.Width(70))) OnDuplicate();
            if (GUILayout.Button("Delete", EditorStyles.toolbarButton, GUILayout.Width(50))) OnDelete();
            GUILayout.Space(8);
            if (GUILayout.Button("Save (JSON only)", EditorStyles.toolbarButton, GUILayout.Width(110))) SaveAll();
            if (GUILayout.Button("Bake Selected", EditorStyles.toolbarButton, GUILayout.Width(90))) OnBakeSelected();
            if (GUILayout.Button("Bake All", EditorStyles.toolbarButton, GUILayout.Width(60))) OnBakeAll();
            if (GUILayout.Button("Validate All", EditorStyles.toolbarButton, GUILayout.Width(80))) CardSetValidator.RunAndLog();
            GUILayout.FlexibleSpace();
            GUILayout.Label(_dirtyIds.Count > 0 ? $"● {_dirtyIds.Count} unsaved" : "saved", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawLibraryList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(240));
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            for (int i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                if (!string.IsNullOrEmpty(_search)
                    && card.Id.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0
                    && card.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var (core, lint) = Validate(card);
                string badge = BadgeFor(core, lint);
                string dirtyMark = _dirtyIds.Contains(card.Id) ? "*" : "";
                var style = i == _selected ? EditorStyles.helpBox : GUI.skin.label;
                if (GUILayout.Button($"{badge} {card.Id}{dirtyMark}  ({card.Name})", style))
                    _selected = i;
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private static string BadgeFor(ValidationReport core, ValidationReport lint)
        {
            if (core.RequiresEngineeringPrimitive) return "⛔";
            if (!core.Ok) return "✕";
            if (core.Issues.Any(i => i.Severity == Severity.Warning) || lint.Issues.Any(i => i.Severity == Severity.Warning))
                return "▲";
            return "●";
        }

        private void DrawEditorPane()
        {
            EditorGUILayout.BeginVertical();
            if (_selected < 0 || _selected >= _cards.Count)
            {
                EditorGUILayout.HelpBox("Select a card from the library, or create a New one.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            var card = _cards[_selected];
            _editScroll = EditorGUILayout.BeginScrollView(_editScroll);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            card.Id = EditorGUILayout.TextField("id", card.Id);
            card.Name = EditorGUILayout.TextField("name", card.Name);
            card.SetId = (SetId)EditorGUILayout.EnumPopup("setId", card.SetId);
            card.Collectible = EditorGUILayout.Toggle("collectible", card.Collectible);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Classification", EditorStyles.boldLabel);
            card.Type = (CardType)EditorGUILayout.EnumPopup("type", card.Type);
            card.Class = (CardClass)EditorGUILayout.EnumPopup("class", card.Class);
            card.Rarity = (Rarity)EditorGUILayout.EnumPopup("rarity", card.Rarity);
            card.HasTribe = EditorGUILayout.Toggle("has tribe", card.HasTribe);
            if (card.HasTribe) card.Tribe = (Tribe)EditorGUILayout.EnumPopup("tribe", card.Tribe);
            card.FactionId = EditorGUILayout.TextField("factionId", card.FactionId);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Stats", EditorStyles.boldLabel);
            card.Cost = EditorGUILayout.IntField("cost", card.Cost);
            DrawStatFields(card);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Keywords", EditorStyles.boldLabel);
            DrawKeywords(card);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rules", EditorStyles.boldLabel);
            DrawRules(card);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Text", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("generatedText", CardTextGenerator.Generate(card.ToDefinition()), EditorStyles.wordWrappedLabel);
            card.FlavorText = EditorGUILayout.TextField("flavorText", card.FlavorText);
            card.ArtId = EditorGUILayout.TextField("artId", card.ArtId);

            EditorGUILayout.Space();
            card.RequiresEngineeringPrimitive = EditorGUILayout.Toggle(
                new GUIContent("requiresEngineeringPrimitive", "Set when an effect can't compose from existing verbs. Blocks data-only bake (guardrails §7)."),
                card.RequiresEngineeringPrimitive);

            if (EditorGUI.EndChangeCheck())
                _dirtyIds.Add(card.Id);

            EditorGUILayout.Space();
            DrawValidationPanel(card);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawStatFields(EditableCard card)
        {
            // Per-type field gating (dev-docs/specs/card-schema.md:30).
            bool attackEnabled = card.Type == CardType.MINION || card.Type == CardType.WEAPON;
            bool healthEnabled = card.Type == CardType.MINION;
            bool durabilityEnabled = card.Type == CardType.WEAPON;

            using (new EditorGUI.DisabledScope(!attackEnabled))
                card.Attack = EditorGUILayout.IntField("attack", attackEnabled ? card.Attack : 0);
            card.HasAttack = attackEnabled;

            using (new EditorGUI.DisabledScope(!healthEnabled))
                card.Health = EditorGUILayout.IntField("health", healthEnabled ? card.Health : 0);
            card.HasHealth = healthEnabled;

            using (new EditorGUI.DisabledScope(!durabilityEnabled))
                card.Durability = EditorGUILayout.IntField("durability", durabilityEnabled ? card.Durability : 0);
            card.HasDurability = durabilityEnabled;
        }

        private void DrawKeywords(EditableCard card)
        {
            foreach (Keyword kw in CarrierKeywordOptions)
            {
                bool has = card.Keywords.Contains(kw);
                bool now = EditorGUILayout.ToggleLeft(kw.ToString(), has);
                if (now && !has) card.Keywords.Add(kw);
                else if (!now && has) card.Keywords.Remove(kw);
            }
        }

        private void DrawRules(EditableCard card)
        {
            for (int i = 0; i < card.Rules.Count; i++)
            {
                var rule = card.Rules[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"rules[{i}]", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("▲", GUILayout.Width(24))) { Swap(card.Rules, i, i - 1); }
                using (new EditorGUI.DisabledScope(i == card.Rules.Count - 1))
                    if (GUILayout.Button("▼", GUILayout.Width(24))) { Swap(card.Rules, i, i + 1); }
                if (GUILayout.Button("✕", GUILayout.Width(24))) { card.Rules.RemoveAt(i); _dirtyIds.Add(card.Id); EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical(); break; }
                EditorGUILayout.EndHorizontal();

                rule.Id = EditorGUILayout.TextField("id", rule.Id);
                rule.ActivationKind = (EditableActivationKind)EditorGUILayout.EnumPopup(
                    "activation.type", rule.ActivationKind);
                if (rule.ActivationKind == EditableActivationKind.Triggered)
                    rule.Trigger = (Trigger)EditorGUILayout.EnumPopup("activation.trigger", rule.Trigger);

                rule.ConditionKind = (EditableConditionKind)EditorGUILayout.EnumPopup(
                    "condition.type", rule.ConditionKind);
                if (rule.ConditionKind == EditableConditionKind.TurnOwner)
                    rule.TurnOwnerScope = (TurnOwner)EditorGUILayout.EnumPopup(
                        "condition.owner", rule.TurnOwnerScope);

                rule.LifetimeKind = (EditableLifetimeKind)EditorGUILayout.EnumPopup(
                    "lifetime.type", rule.LifetimeKind);
                if (rule.LifetimeKind == EditableLifetimeKind.Finite)
                    rule.Uses = EditorGUILayout.IntField("lifetime.uses", rule.Uses);

                rule.ActionKind = (EditableRuleActionKind)EditorGUILayout.EnumPopup(
                    "action.type", rule.ActionKind);
                DrawRuleAction(rule);

                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Rule"))
            {
                string id = "rule_" + (card.Rules.Count + 1);
                while (card.Rules.Any(rule => rule.Id == id)) id += "_new";
                card.Rules.Add(new EditableRule { Id = id });
                _dirtyIds.Add(card.Id);
            }
        }

        private static void DrawRuleAction(EditableRule rule)
        {
            switch (rule.ActionKind)
            {
                case EditableRuleActionKind.Damage:
                case EditableRuleActionKind.Heal:
                case EditableRuleActionKind.GainMana:
                case EditableRuleActionKind.Backdraft:
                case EditableRuleActionKind.GainArmor:
                    rule.Amount = EditorGUILayout.IntField("action.amount", rule.Amount);
                    break;
                case EditableRuleActionKind.ModifyStats:
                    rule.AttackDelta = EditorGUILayout.IntField("action.attack", rule.AttackDelta);
                    rule.HealthDelta = EditorGUILayout.IntField("action.health", rule.HealthDelta);
                    break;
                case EditableRuleActionKind.Draw:
                case EditableRuleActionKind.Summon:
                case EditableRuleActionKind.AddToHand:
                case EditableRuleActionKind.Unearth:
                    rule.Count = EditorGUILayout.IntField("action.count", rule.Count);
                    break;
            }

            if (rule.ActionKind == EditableRuleActionKind.Summon
                || rule.ActionKind == EditableRuleActionKind.AddToHand
                || rule.ActionKind == EditableRuleActionKind.Equip
                || rule.ActionKind == EditableRuleActionKind.Transform)
                rule.CardId = EditorGUILayout.TextField("action.cardId", rule.CardId);

            if (rule.ActionKind == EditableRuleActionKind.GrantKeyword)
                rule.Keyword = DrawCarrierKeywordPopup("action.keyword", rule.Keyword);
            if (rule.ActionKind == EditableRuleActionKind.GainMana)
                rule.ManaKind = (ManaGainKind)EditorGUILayout.EnumPopup("action.manaKind", rule.ManaKind);
            if (rule.IsTargeted) DrawTarget(rule.Target);
        }

        private static Keyword DrawCarrierKeywordPopup(string label, Keyword current)
        {
            int selected = Array.IndexOf(CarrierKeywordOptions, current);
            if (selected < 0) selected = 0;
            selected = EditorGUILayout.Popup(label, selected, CarrierKeywordNames);
            return CarrierKeywordOptions[selected];
        }

        private static void DrawTarget(EditableTarget target)
        {
            int selectorIndex = Array.IndexOf(SelectorOptions, target.Selector);
            if (selectorIndex < 1) selectorIndex = 1;
            target.Selector = SelectorOptions[EditorGUILayout.Popup(
                "action.target.selector", selectorIndex, SelectorOptions)];

            target.HasChosenBy = EditorGUILayout.Toggle("target has chosenBy", target.HasChosenBy);
            if (target.HasChosenBy)
                target.ChosenBy = (TargetChosenBy)EditorGUILayout.EnumPopup("target.chosenBy", target.ChosenBy);
            target.HasCount = EditorGUILayout.Toggle("target has count", target.HasCount);
            if (target.HasCount) target.Count = EditorGUILayout.IntField("target.count", target.Count);
            target.HasDamagedFilter = EditorGUILayout.Toggle("filter has damaged", target.HasDamagedFilter);
            if (target.HasDamagedFilter) target.Damaged = EditorGUILayout.Toggle("filter.damaged", target.Damaged);
            target.HasTribeFilter = EditorGUILayout.Toggle("filter has tribe", target.HasTribeFilter);
            if (target.HasTribeFilter)
                target.Tribe = (Tribe)EditorGUILayout.EnumPopup("filter.tribe", target.Tribe);
            target.Binding = EditorGUILayout.TextField("target.binding", target.Binding);
            target.DistinctFrom = EditorGUILayout.TextField("target.distinctFrom", target.DistinctFrom);
        }

        private static void Swap<T>(List<T> list, int a, int b)
        {
            (list[a], list[b]) = (list[b], list[a]);
        }

        private void DrawValidationPanel(EditableCard card)
        {
            var (core, lint) = Validate(card);
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);

            if (core.RequiresEngineeringPrimitive)
                EditorGUILayout.HelpBox(
                    "requiresEngineeringPrimitive is set — this card cannot be baked data-only. " +
                    "Route to Shepherd+Eng to add a reusable primitive (guardrails §7).",
                    MessageType.Error);

            foreach (var issue in core.Issues)
                EditorGUILayout.HelpBox($"[{issue.Code}] {issue.Field}: {issue.Message}",
                    issue.Severity == Severity.Error ? MessageType.Error : MessageType.Warning);

            foreach (var issue in lint.Issues)
                EditorGUILayout.HelpBox($"[{issue.Code}] {issue.Field}: {issue.Message}", MessageType.Warning);

            if (core.Ok && !core.RequiresEngineeringPrimitive && core.Issues.Count == 0 && lint.Issues.Count == 0)
                EditorGUILayout.HelpBox("Clean.", MessageType.None);
        }

        // ── toolbar actions ──────────────────────────────────────────────────────

        private void OnNew()
        {
            var card = new EditableCard { Id = UniqueId("new_card"), Name = "New Card" };
            _cards.Add(card);
            _selected = _cards.Count - 1;
            _dirtyIds.Add(card.Id);
        }

        private void OnDuplicate()
        {
            if (_selected < 0) return;
            var clone = _cards[_selected].Clone();
            clone.Id = UniqueId(clone.Id + "_copy");
            _cards.Insert(_selected + 1, clone);
            _selected += 1;
            _dirtyIds.Add(clone.Id);
        }

        private void OnDelete()
        {
            if (_selected < 0) return;
            var card = _cards[_selected];
            if (!EditorUtility.DisplayDialog("Delete Card", $"Delete '{card.Id}'? This removes it from M1.cards.json on next Save.", "Delete", "Cancel"))
                return;

            var referencedBy = _cards.Where(c => c.Rules.Any(rule => rule.ReferencedCardId == card.Id))
                .Select(c => c.Id).ToList();
            if (referencedBy.Count > 0
                && !EditorUtility.DisplayDialog("Broken Reference Warning",
                    $"'{card.Id}' is referenced by: {string.Join(", ", referencedBy)}. Delete anyway?", "Delete Anyway", "Cancel"))
                return;

            _cards.RemoveAt(_selected);
            _dirtyIds.Remove(card.Id);
            _selected = Mathf.Min(_selected, _cards.Count - 1);
        }

        private string UniqueId(string baseId)
        {
            string id = baseId;
            int n = 1;
            while (_cards.Any(c => c.Id == id))
                id = $"{baseId}_{n++}";
            return id;
        }

        private void SaveAll()
        {
            var defs = _cards.Select(c => c.ToDefinition()).ToList();
            CardJsonRowIO.SaveRows(CardJsonRowIO.DefaultM1JsonPath, defs);
            AssetDatabase.Refresh();
            _dirtyIds.Clear();
        }

        private void OnBakeSelected()
        {
            if (_selected < 0) return;
            var card = _cards[_selected];
            var (core, _) = Validate(card);
            if (core.RequiresEngineeringPrimitive || !core.Ok)
            {
                EditorUtility.DisplayDialog("Bake Blocked",
                    $"'{card.Id}' has validation errors or requiresEngineeringPrimitive set — fix them before baking.",
                    "OK");
                return;
            }

            SaveAll();
            var importer = new CardImporter(_validator, LiveDb());
            var result = importer.ImportRow(card.ToDefinition());
            if (result.Success)
                Debug.Log($"[Card Authoring] Baked '{card.Id}' -> {string.Join(", ", result.AssetPaths)}");
            else
                Debug.LogError($"[Card Authoring] Bake failed for '{card.Id}'.");
        }

        private void OnBakeAll()
        {
            var blocked = _cards.Select(c => (c, report: Validate(c).core))
                .Where(r => r.report.RequiresEngineeringPrimitive || !r.report.Ok)
                .ToList();
            if (blocked.Count > 0)
            {
                bool proceed = EditorUtility.DisplayDialog("Some Cards Blocked",
                    $"{blocked.Count} card(s) fail validation and will be skipped: {string.Join(", ", blocked.Select(b => b.c.Id))}. Bake the rest?",
                    "Bake Rest", "Cancel");
                if (!proceed) return;
            }

            SaveAll();
            var importer = new CardImporter(_validator, LiveDb());
            var result = importer.ImportAllFromFlatFile(CardJsonRowIO.DefaultM1JsonPath);
            Debug.Log($"[Card Authoring] Baked {result.AssetPaths.Count} card(s). Success={result.Success}");
        }
    }
}
