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
        };

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
            EditorGUILayout.LabelField("Effects", EditorStyles.boldLabel);
            DrawEffects(card);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Text", EditorStyles.boldLabel);
            card.CardText = EditorGUILayout.TextField("cardText", card.CardText);
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
            foreach (Keyword kw in Enum.GetValues(typeof(Keyword)))
            {
                bool has = card.Keywords.Contains(kw);
                bool now = EditorGUILayout.ToggleLeft(kw.ToString(), has);
                if (now && !has) card.Keywords.Add(kw);
                else if (!now && has) card.Keywords.Remove(kw);
            }
        }

        private void DrawEffects(EditableCard card)
        {
            for (int i = 0; i < card.Effects.Count; i++)
            {
                var fx = card.Effects[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"effects[{i}]", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button("▲", GUILayout.Width(24))) { Swap(card.Effects, i, i - 1); }
                using (new EditorGUI.DisabledScope(i == card.Effects.Count - 1))
                    if (GUILayout.Button("▼", GUILayout.Width(24))) { Swap(card.Effects, i, i + 1); }
                if (GUILayout.Button("✕", GUILayout.Width(24))) { card.Effects.RemoveAt(i); _dirtyIds.Add(card.Id); EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical(); break; }
                EditorGUILayout.EndHorizontal();

                fx.Trigger = (Trigger)EditorGUILayout.EnumPopup("trigger", fx.Trigger);
                if (fx.Trigger == Trigger.ON_TURN_START)
                {
                    fx.HasCondition = true;
                    fx.ConditionKind = ConditionType.TURN_OWNER;
                    fx.TurnOwnerScope = (TurnOwner)EditorGUILayout.EnumPopup("condition.turnOwner", fx.TurnOwnerScope);
                }
                else
                {
                    // TURN_OWNER is the only live condition today and has no context on other triggers.
                    // RETIREMENT TRIGGER: when another ConditionType lands, replace this reset with its
                    // type-specific editor instead of erasing every non-turn-start condition.
                    fx.HasCondition = false;
                }
                fx.Action = (Emberfall.Core.Contracts.Action)EditorGUILayout.EnumPopup("action", fx.Action);

                fx.HasAmount = EditorGUILayout.Toggle("has amount", fx.HasAmount);
                if (fx.HasAmount) fx.Amount = EditorGUILayout.IntField("amount", fx.Amount);

                fx.HasAmount2 = EditorGUILayout.Toggle("has amount2", fx.HasAmount2);
                if (fx.HasAmount2) fx.Amount2 = EditorGUILayout.IntField("amount2", fx.Amount2);

                fx.HasTokenId = EditorGUILayout.Toggle("has payload.tokenId", fx.HasTokenId);
                if (fx.HasTokenId) fx.TokenId = EditorGUILayout.TextField("tokenId", fx.TokenId);

                fx.HasKeyword = EditorGUILayout.Toggle("has payload.keyword", fx.HasKeyword);
                if (fx.HasKeyword) fx.Keyword = (Keyword)EditorGUILayout.EnumPopup("keyword", fx.Keyword);

                if (fx.Action == Emberfall.Core.Contracts.Action.GAIN_MANA)
                {
                    fx.Permanent = EditorGUILayout.Toggle("payload.permanent", fx.Permanent);
                    if (fx.Permanent) fx.Empty = EditorGUILayout.Toggle("payload.empty", fx.Empty);
                    else fx.Empty = false;
                }

                int selIndex = fx.HasSelector ? Array.IndexOf(SelectorOptions, fx.Selector) : 0;
                if (selIndex < 0) selIndex = 0;
                int newSelIndex = EditorGUILayout.Popup("target.selector", selIndex, SelectorOptions);
                if (newSelIndex == 0) { fx.HasSelector = false; fx.Selector = ""; }
                else { fx.HasSelector = true; fx.Selector = SelectorOptions[newSelIndex]; }

                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Effect"))
            {
                card.Effects.Add(new EditableEffect());
                _dirtyIds.Add(card.Id);
            }
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

            var referencedBy = _cards.Where(c => c.Effects.Any(e => e.TokenId == card.Id)).Select(c => c.Id).ToList();
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
