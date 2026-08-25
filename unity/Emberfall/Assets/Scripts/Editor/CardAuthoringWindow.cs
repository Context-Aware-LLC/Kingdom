using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Card Authoring EditorWindow (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M1-KICKOFF.md Phase B, dev-docs/pipelines/card-authoring-and-import.md:9). Edits the
    /// canonical JSON rows across every set file (see CardJsonRowIO.PathForSet), never a derived copy.
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
        /// <summary>
        /// The selector popup, taken from the engine rather than retyped. The class comment above
        /// promises a designer cannot pick an unknown selector; a hand-kept copy of the names is
        /// exactly how that promise breaks, and it had broken — this list still offered three names
        /// the engine retired on 2026-08-20, so the popup could author a row the validator rejects.
        /// The sibling keyword list below always derived from its enum; this now matches it.
        /// </summary>
        private static readonly string[] SelectorOptions =
            new[] { "(none)" }
                .Concat(TargetSpec.Selectors.OrderBy(n => n, StringComparer.Ordinal))
                .ToArray();

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
        private readonly HashSet<int> _dirtyIds = new HashSet<int>();
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

        // Art preview cache, keyed on the artId we last SEARCHED — a miss is cached too. IMGUI
        // repaints continuously, and AssetDatabase.FindAssets is a project-wide scan, so caching
        // only the hits would leave every card whose art is missing running that scan every frame.
        // "Recheck" is the way back out when the file is fixed on disk while the window stays open.
        private string _artPreviewFor;
        private Sprite _artPreviewSprite;
        private string _artPreviewPath;

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
            // EVERY set file, not just Emberfall. Loading one file made this window wrong in two
            // ways at once, both silent: LiveDb() could not resolve a rule naming a card from
            // another set (reported as C12-TOKEN-UNRESOLVED against a card that is fine), and
            // NextIdInBand scanned 179 of 268 rows, so the id it minted collided with an existing
            // card in EVERY band — measured 2026-08-23, the moment CORE split out.
            foreach (string path in CardJsonRowIO.AllSetPaths())
            {
                if (!File.Exists(path)) { Debug.LogWarning($"[CardAuthoring] missing set file: {path}"); continue; }
                foreach (var (def, _) in CardJsonRowIO.LoadRows(path))
                    _cards.Add(EditableCard.FromDefinition(def));
            }
            _dirtyIds.Clear();
            InvalidateAll();
            if (_selected >= _cards.Count) _selected = _cards.Count - 1;
        }

        /// <summary>
        /// The pool every per-card check resolves against, built ONCE PER REPAINT rather than once
        /// per card.
        ///
        /// <para><b>It used to be per call, and the library list calls it per row.</b> With no
        /// search filter that was <c>_cards.Count</c> squared <c>ToDefinition()</c> calls and as
        /// many dictionary builds on every single IMGUI frame — and IMGUI repaints continuously,
        /// which is the same fact the art-preview cache above exists for. The cost also jumped the
        /// moment this window started loading all three set files instead of one: 179 rows became
        /// 268, and the work is quadratic, so it did not grow by half, it grew by 2.2x.</para>
        /// </summary>
        private ICardDatabase LiveDb() => _liveDb ??= new InMemoryCardDatabase(_cards.Select(c => c.ToDefinition()));

        private ICardDatabase _liveDb;

        /// <summary>
        /// Cached validation per card, because the library list draws a badge for EVERY row and
        /// IMGUI repaints continuously — so an uncached badge means the full C-series plus the
        /// content lint, for all 268 rows, many times a second while you sit still. That is why
        /// typing in this window crawled.
        /// </summary>
        private readonly Dictionary<int, (ValidationReport core, ValidationReport lint)> _reports = new();

        /// <summary>
        /// Drops the cached pool and every cached report. For a STRUCTURAL change — a row added,
        /// removed, reloaded, or moved between sets — since those change what every other row
        /// resolves against.
        /// </summary>
        private void InvalidateAll()
        {
            _liveDb = null;
            _reports.Clear();
        }

        /// <summary>
        /// Drops one card's cached report after an edit to that card.
        ///
        /// <para><b>Deliberately re-checks nothing else, and does not rebuild the pool.</b> A
        /// keystroke therefore costs one validation and one lint — not 268 — and no allocation of
        /// the pool at all.</para>
        ///
        /// <para>Keeping the pool is safe because a field edit cannot change its KEY SET: rows are
        /// resolved by id, and the id stopped being editable when it became the permanent key. Only
        /// adding, removing or reloading a row changes what ids exist, and those call
        /// <see cref="InvalidateAll"/>.</para>
        ///
        /// <para>The staleness this buys is one thing wide, and it is the same thing the report
        /// cache already accepts: C13 reads another row's <c>Collectible</c>, so toggling that on a
        /// token can leave a stale badge on a card that summons it. Validate All is the
        /// authoritative sweep and reads from disk.</para>
        /// </summary>
        /// <summary>
        /// Queues a card for revalidation shortly after the typing stops, rather than on the
        /// keystroke itself (Sam, 2026-08-23: "I still don't think every keystroke is reasonable").
        ///
        /// <para>Held rather than dropped: the STALE report keeps rendering until the new one is
        /// computed, so the panel does not flicker empty between keystrokes. What it costs is a
        /// quarter second of a badge describing the previous keystroke, which is invisible while
        /// you are still typing and gone by the time you look up.</para>
        /// </summary>
        private void Invalidate(int cardId)
        {
            _revalidateId = cardId;
            _revalidateAt = EditorApplication.timeSinceStartup + RevalidateDelay;
        }

        private const double RevalidateDelay = 0.25;
        private int _revalidateId = -1;
        private double _revalidateAt;

        /// <summary>Fires a queued revalidation once the delay is up. Called once per repaint.</summary>
        private void TickRevalidate()
        {
            if (_revalidateId < 0) return;
            if (EditorApplication.timeSinceStartup < _revalidateAt)
            {
                // IMGUI only repaints on input, and the whole point here is the moment input STOPS,
                // so nothing else would wake this window to run the check.
                Repaint();
                return;
            }
            _reports.Remove(_revalidateId);
            _revalidateId = -1;
        }

        private (ValidationReport core, ValidationReport lint) Validate(EditableCard card)
        {
            if (_reports.TryGetValue(card.Id, out var cached)) return cached;

            var def = card.ToDefinition();
            var db = LiveDb();
            var report = (_validator.Validate(def, db), _linter.Lint(def, _artCatalog, _guardrails, db));
            _reports[card.Id] = report;
            return report;
        }

        private void OnGUI()
        {
            TickRevalidate();
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
            if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(110))) SaveAll();
            if (GUILayout.Button("Validate All", EditorStyles.toolbarButton, GUILayout.Width(80))) CardSetValidator.RunAndLog();
            // The selected card's state, beside the button that checks all of them, so it is on
            // screen whatever the editor pane is scrolled to. The messages stay in the pinned panel
            // — a toolbar can carry a verdict, not three sentences.
            GUILayout.Space(8);
            GUILayout.Label(SelectedStatus(), EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();
            GUILayout.Label(_dirtyIds.Count > 0 ? $"● {_dirtyIds.Count} unsaved" : "saved", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>The selected card's verdict, for the toolbar. Empty when nothing is selected.</summary>
        private string SelectedStatus()
        {
            if (_selected < 0 || _selected >= _cards.Count) return "";
            var (core, lint) = Validate(_cards[_selected]);
            int errors = core.Issues.Count(i => i.Severity == Severity.Error);
            int warnings = core.Issues.Count - errors + lint.Issues.Count;
            if (errors > 0) return $"✕ {errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}";
            if (warnings > 0) return $"▲ {warnings} warning{(warnings == 1 ? "" : "s")}";
            return "● clean";
        }

        private void DrawLibraryList()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(240));
            _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            for (int i = 0; i < _cards.Count; i++)
            {
                var card = _cards[i];
                // Searches the HANDLE and the name — the two things a human can actually type.
                // The number is shown but not searched; nobody remembers 1049.
                if (!string.IsNullOrEmpty(_search)
                    && card.Handle.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0
                    && card.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var (core, lint) = Validate(card);
                string badge = BadgeFor(core, lint);
                string dirtyMark = _dirtyIds.Contains(card.Id) ? "*" : "";
                var style = i == _selected ? EditorStyles.helpBox : GUI.skin.label;
                // The display NAME alone (Sam, 2026-08-23). The number and the handle are both
                // identifiers, and neither is what a designer scanning this list is looking for —
                // they are on the row itself, under Identity, once a card is selected.
                //
                // Two rows CAN print the same name and this list cannot tell them apart; that is
                // the same lossiness CardNameIndex reports rather than resolves. Search still
                // matches the handle, so a duplicate name stays reachable by typing the handle
                // even though the handle is no longer shown.
                if (GUILayout.Button($"{badge} {card.Name}{dirtyMark}", style))
                    _selected = i;
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Show the art the selected card's artId actually resolves to, or say plainly that it
        /// resolves to nothing.
        ///
        /// Resolution goes through the game's own <see cref="EditorArtProvider"/> rather than a
        /// private lookup of its own. A preview that agreed with nothing would be worse than no
        /// preview: the whole value here is that a card looking right in this window is a card
        /// whose art the vertical slice will find.
        /// </summary>
        private void DrawArtPreview(EditableCard card)
        {
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Art", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Recheck", EditorStyles.miniButton, GUILayout.Width(64)))
                _artPreviewFor = null;
            EditorGUILayout.EndHorizontal();

            string artId = card.ArtId ?? "";
            if (_artPreviewFor != artId)
            {
                _artPreviewFor = artId;
                _artPreviewSprite = null;
                _artPreviewPath = null;
                new EditorArtProvider().LoadArt(artId, sprite =>
                {
                    _artPreviewSprite = sprite;
                    _artPreviewPath = sprite == null ? null : AssetDatabase.GetAssetPath(sprite);
                });
            }

            if (artId.Length == 0)
            {
                EditorGUILayout.HelpBox("No artId on this row, so nothing to show.", MessageType.Info);
                return;
            }

            string expectedFolder = $"Assets/Art/Cards/{card.SetId}/";
            if (_artPreviewSprite == null)
            {
                // The exact sentence an author needs, because the failure is otherwise silent until
                // CI: audit_card_art_names.py --fail-missing-only is a blocking stage, and the
                // artId IS the filename, so a dropped separator is the whole bug.
                EditorGUILayout.HelpBox(
                    $"artId '{artId}' resolves to no sprite under Assets/Art/.\n" +
                    $"Expected {expectedFolder}{ArtIds.ToFileName(artId)}.png — the artId IS the " +
                    "filename. Check for a missing separator before assuming the art is absent.",
                    MessageType.Error);
                return;
            }

            var texture = _artPreviewSprite.texture;
            const float previewWidth = 220f;
            float height = texture.width > 0 ? previewWidth * texture.height / texture.width : previewWidth;
            Rect rect = GUILayoutUtility.GetRect(previewWidth, height, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);

            EditorGUILayout.LabelField($"{texture.width} × {texture.height}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(_artPreviewPath, EditorStyles.miniLabel);

            // The set folder decides which Addressables bundle the file ships in, and NOTHING else
            // reports a mismatch: the address is a flat card_art/<fileName> with no set in it, so a
            // file in the wrong folder still renders here and still passes CardCi while shipping in
            // the wrong bundle. Only audit_card_art_names.py looks — and now this line.
            if (!_artPreviewPath.StartsWith(expectedFolder, StringComparison.OrdinalIgnoreCase))
                EditorGUILayout.HelpBox(
                    $"This row is setId {card.SetId}, but its art sits outside {expectedFolder}. " +
                    "It will render correctly and ship in the wrong Addressables bundle.",
                    MessageType.Warning);

            if (GUILayout.Button("Select in Project", GUILayout.Width(140)))
                EditorGUIUtility.PingObject(_artPreviewSprite);
        }

        private static string BadgeFor(ValidationReport core, ValidationReport lint)
        {
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

            // PINNED ABOVE THE SCROLL VIEW, not inside it. This panel used to sit at the very
            // bottom of the pane, under Rules, so seeing what the validator found meant scrolling
            // past every field on the card — which nobody does, which made a live check that runs
            // on every edit effectively invisible (Sam, 2026-08-23: "I don't see the output from it
            // anywhere"). A check nobody reads is the same as no check.
            DrawValidationPanel(card);

            _editScroll = EditorGUILayout.BeginScrollView(_editScroll);

            // The face first: what the card SAYS, and the art it says it over. Everything below is
            // the machinery behind it. Authoring reads top-down as a result, and the text field —
            // the authored source of the rules since the 2026-08-16 reversal — is no longer the
            // last thing on the page.
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Text", EditorStyles.boldLabel);
            DrawCardText(card);
            card.FlavorText = EditorGUILayout.TextField("flavorText", card.FlavorText);
            card.Tooltip = EditorGUILayout.TextField("tooltip", card.Tooltip);
            card.ArtId = EditorGUILayout.TextField("artId", card.ArtId);

            if (EditorGUI.EndChangeCheck())
            {
                _dirtyIds.Add(card.Id);
                Invalidate(card.Id);
            }

            // Deliberately BETWEEN the two change checks, never inside one. Every control in this
            // block is a button, and a clicked button sets GUI.changed — inside a check, merely
            // pinging the art in the Project window would mark the card unsaved. That is why the
            // pane runs two checks rather than one: the split is where this block sits.
            DrawArtPreview(card);

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            // The id is PERMANENT and is shown, never typed. Editing it would repoint a key that
            // saved decks, replays and telemetry already carry — the failure the whole identity
            // model exists to make impossible. The handle beneath it is free to change.
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.IntField("id", card.Id);
            card.Handle = EditorGUILayout.TextField("handle", card.Handle);
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

            if (EditorGUI.EndChangeCheck())
            {
                _dirtyIds.Add(card.Id);
                Invalidate(card.Id);
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// The authored card face, plus what the rules under it read as when the two disagree.
        /// </summary>
        /// <remarks>
        /// This field used to be a read-only <c>generatedText</c> label — the author watched the
        /// sentence appear and could not touch it. Under the 2026-08-16 text-direction reversal the
        /// text is the authored thing and the rendering is the check, so the two swap places: the
        /// field is editable and the generator's output only appears when it disagrees.
        ///
        /// <para>The "use it" button is the whole ergonomics of the reversal for a NEW card: author
        /// the rules, take the rendering as a first draft, then edit the prose freely. What it is
        /// NOT is a sync button — pressing it on an existing card overwrites a sentence somebody
        /// wrote, which is why it sits behind a visible disagreement rather than running on save.</para>
        /// </remarks>
        private void DrawCardText(EditableCard card)
        {
            card.Text = EditorGUILayout.TextField("text", card.Text);

            var divergence = CardTextGenerator.Check(card.ToDefinition(), LiveDb(), out string expected);
            if (divergence == CardTextDivergence.None) return;

            if (divergence == CardTextDivergence.Unrenderable)
            {
                EditorGUILayout.HelpBox(
                    "C26: these rules are a shape with no phrasing, so 'text' cannot be checked against them.",
                    MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox(
                divergence == CardTextDivergence.Orphaned
                    ? "C26: this card has no keywords or rules, so it should carry no text."
                    : $"C26: the rules read as \"{expected}\".",
                MessageType.Error);

            using (new EditorGUI.DisabledScope(divergence == CardTextDivergence.Orphaned))
                if (GUILayout.Button("Use what the rules read as"))
                {
                    card.Text = expected;
                    GUI.FocusControl(null);
                }
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
                if (GUILayout.Button("✕", GUILayout.Width(24))) { card.Rules.RemoveAt(i); _dirtyIds.Add(card.Id); Invalidate(card.Id); EditorGUILayout.EndHorizontal(); EditorGUILayout.EndVertical(); break; }
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
                if (rule.ConditionKind == EditableConditionKind.Controls)
                {
                    rule.ControlsTribe = (Tribe)EditorGUILayout.EnumPopup(
                        "condition.tribe", rule.ControlsTribe);
                    // C29 pins this to the carrier: on a minion of that same tribe it MUST be set,
                    // and anywhere else it must not be — the card would otherwise count itself, or
                    // print "another" with nothing to be another of.
                    rule.ControlsExcludesSource = EditorGUILayout.Toggle(
                        "condition.other", rule.ControlsExcludesSource);
                }

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
                Invalidate(card.Id);
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
                rule.CardId = EditorGUILayout.IntField("action.cardId", rule.CardId);

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

            // One selection term, always written — there is no "has a selection" toggle, because a
            // count of the selector's own maximum IS "every candidate". Who chooses follows from
            // the pair: random rolls, otherwise the controller picks, unless the count has reached
            // the maximum and nobody picks. C27 rejects the one shape this lets you type but the
            // model deliberately cannot say — an automatic finite count.
            target.SelectionCount = Mathf.Max(1,
                EditorGUILayout.IntField("target.selection count", target.SelectionCount));
            target.RandomSelection = EditorGUILayout.Toggle("target.selection random", target.RandomSelection);
            // Authored, not derived: whether this pick is the card's price is the one thing no
            // sentence can be read back to recover.
            target.Cost = (CostKind)EditorGUILayout.EnumPopup("target.cost", target.Cost);
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

        /// <summary>
        /// What the validator and the linter say about the selected card, right now.
        /// </summary>
        /// <remarks>
        /// Loud when there is something to say and nearly silent when there is not: a clean card
        /// gets one grey line rather than a help box, because a permanent "Clean." panel at the top
        /// of every card is exactly the kind of chrome a reader stops seeing — and then stops
        /// seeing the errors that replace it.
        /// </remarks>
        private void DrawValidationPanel(EditableCard card)
        {
            var (core, lint) = Validate(card);
            int errors = core.Issues.Count(i => i.Severity == Severity.Error);
            int warnings = core.Issues.Count - errors + lint.Issues.Count;

            if (errors == 0 && warnings == 0)
            {
                EditorGUILayout.LabelField("● clean", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.LabelField(
                errors > 0 ? $"✕ {errors} error(s), {warnings} warning(s)" : $"▲ {warnings} warning(s)",
                EditorStyles.boldLabel);

            // Errors first: a card with both is blocked by the errors, and reading the warnings
            // first buries that.
            foreach (var issue in core.Issues.Where(i => i.Severity == Severity.Error))
                EditorGUILayout.HelpBox($"[{issue.Code}] {issue.Field}: {issue.Message}", MessageType.Error);
            foreach (var issue in core.Issues.Where(i => i.Severity != Severity.Error))
                EditorGUILayout.HelpBox($"[{issue.Code}] {issue.Field}: {issue.Message}", MessageType.Warning);
            foreach (var issue in lint.Issues)
                EditorGUILayout.HelpBox($"[{issue.Code}] {issue.Field}: {issue.Message}", MessageType.Warning);
        }

        // ── toolbar actions ──────────────────────────────────────────────────────

        private void OnNew()
        {
            var card = new EditableCard
            {
                Id = NextIdInBand(CardType.MINION),
                Handle = UniqueHandle("new_card"),
                Name = "New Card",
            };
            _cards.Add(card);
            InvalidateAll();
            _selected = _cards.Count - 1;
            _dirtyIds.Add(card.Id);
        }

        private void OnDuplicate()
        {
            if (_selected < 0) return;
            var clone = _cards[_selected].Clone();
            clone.Id = NextIdInBand(clone.Type);
            clone.Handle = UniqueHandle(clone.Handle + "_copy");
            _cards.Insert(_selected + 1, clone);
            InvalidateAll();
            _selected += 1;
            _dirtyIds.Add(clone.Id);
        }

        private void OnDelete()
        {
            if (_selected < 0) return;
            var card = _cards[_selected];
            if (!EditorUtility.DisplayDialog("Delete Card", $"Delete '{card.Handle}' (card {card.Id})? This removes it from its set file on next Save.", "Delete", "Cancel"))
                return;

            var referencedBy = _cards.Where(c => c.Rules.Any(rule => rule.ReferencedCardId == card.Id))
                .Select(c => c.Id).ToList();
            if (referencedBy.Count > 0
                && !EditorUtility.DisplayDialog("Broken Reference Warning",
                    $"'{card.Id}' is referenced by: {string.Join(", ", referencedBy)}. Delete anyway?", "Delete Anyway", "Cancel"))
                return;

            _cards.RemoveAt(_selected);
            InvalidateAll();
            _dirtyIds.Remove(card.Id);
            _selected = Mathf.Min(_selected, _cards.Count - 1);
        }

        /// <summary>
        /// The next free number in the band this card TYPE owns — HERO_POWER 1-50, WEAPON 51-150,
        /// SPELL 151-1000, MINION 1001 and up. Mirrors CardValidator's C1B, which rejects a row
        /// that lands outside its band.
        ///
        /// <para><b>Scoped to the rows this window has loaded</b>, which is every shipped set, so
        /// it cannot hand out a number already in use. It is not a reservation: two authors
        /// working at once would both be handed the same next number, and the duplicate surfaces
        /// as C2 at validation rather than silently.</para>
        /// </summary>
        private int NextIdInBand(CardType type)
        {
            (int first, int last) = type switch
            {
                CardType.HERO_POWER => (1, 50),
                CardType.WEAPON => (51, 150),
                CardType.SPELL => (151, 1000),
                _ => (1001, int.MaxValue),
            };
            int next = first;
            foreach (var c in _cards)
                if (c.Id >= first && c.Id <= last && c.Id >= next) next = c.Id + 1;
            if (next > last)
                Debug.LogError($"[CardAuthoring] the {type} band ({first}-{last}) is full. " +
                               "Widen the band rather than spilling into the next one.");
            return next;
        }

        private string UniqueHandle(string baseId)
        {
            string id = baseId;
            int n = 1;
            while (_cards.Any(c => c.Handle == id))
                id = $"{baseId}_{n++}";
            return id;
        }

        /// <summary>
        /// Writes every loaded row back to the file its <see cref="SetId"/> names — so a card whose
        /// set changed MOVES between files, and a new card is filed by what it says it is rather
        /// than by which file the window happened to open.
        ///
        /// <para><b>Writes each set file whole, from what is in memory.</b> Any set file this
        /// window did not load would therefore be TRUNCATED, which is why LoadAll reads all of them
        /// and warns rather than skipping quietly.</para>
        /// </summary>
        private void SaveAll()
        {
            var bySet = new Dictionary<SetId, List<CardDefinition>>();
            foreach (var card in _cards)
            {
                CardDefinition def = card.ToDefinition();
                if (!bySet.TryGetValue(def.SetId, out var rows)) bySet[def.SetId] = rows = new List<CardDefinition>();
                rows.Add(def);
            }

            foreach (SetId set in System.Enum.GetValues(typeof(SetId)))
            {
                bySet.TryGetValue(set, out var rows);
                CardJsonRowIO.SaveRows(CardJsonRowIO.PathForSet(set), rows ?? new List<CardDefinition>());
            }

            AssetDatabase.Refresh();
            _dirtyIds.Clear();
        }
    }
}
