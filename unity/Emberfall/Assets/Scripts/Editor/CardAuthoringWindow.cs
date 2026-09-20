using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Editor comments from the web tool's sidecar. Cached for the same reason the art
        // preview is: IMGUI repaints continuously and this is a file read. Refreshed on load and
        // on demand — the file changes underneath this window whenever somebody pulls the
        // deployed copy back, and nothing in Unity notices a write outside Assets/.
        private IReadOnlyDictionary<int, List<CardComments.Comment>> _comments =
            new Dictionary<int, List<CardComments.Comment>>();
        private bool _showResolvedComments;

        // Selected-card-only AudioClip resolution. Like the art cache above, misses are cached too:
        // an absent clip must not trigger AssetDatabase.FindAssets on every IMGUI repaint.
        private readonly Dictionary<string, AudioClip> _soundClips =
            new Dictionary<string, AudioClip>(StringComparer.Ordinal);

        private void OnEnable()
        {
            minSize = new Vector2(760, 480);   // never collapse to a bare title strip again
            _validator  = new CardValidator();
            _linter     = new ContentLinter();
            _artCatalog = new AddressablesArtCatalog();
            _guardrails = new StubBalanceGuardrails();
            LoadAll();
        }

        /// <summary>
        /// The sets this window read back WHOLE, and therefore the only ones it may write.
        ///
        /// <para>An empty set file and a set file that failed to parse look identical once the rows
        /// are in memory — both are zero rows — and <see cref="SaveAll"/> writes each file whole.
        /// So the fact that a load failed has to survive as its own state; deriving it from the row
        /// list is exactly the mistake that emptied REKINDLING.</para>
        /// </summary>
        private readonly HashSet<SetId> _loadedSets = new HashSet<SetId>();

        private void LoadAll()
        {
            _cards.Clear();
            // EVERY set file, not just Emberfall. Loading one file made this window wrong in two
            // ways at once, both silent: LiveDb() could not resolve a rule naming a card from
            // another set (reported as C12-TOKEN-UNRESOLVED against a card that is fine), and
            // NextIdInBand scanned 179 of 268 rows, so the id it minted collided with an existing
            // card in EVERY band — measured 2026-08-23, the moment CORE split out.
            // A set is SAVEABLE only once it has been read back whole. See _loadedSets — this is
            // the half of the load that SaveAll is not allowed to guess at.
            _loadedSets.Clear();
            foreach (SetId set in System.Enum.GetValues(typeof(SetId)))
            {
                string path = CardJsonRowIO.PathForSet(set);
                if (!File.Exists(path)) { Debug.LogWarning($"[CardAuthoring] missing set file: {path}"); continue; }
                // Per file, and CAUGHT. A row this window's compiled assembly cannot parse throws
                // out of LoadRows, and before 2026-08-25 that exception left the loop — so the sets
                // after the bad one never loaded, and SaveAll then wrote each of them as `[]`.
                // It happened for real: an authored `target.filter.keyword` against a stale
                // Emberfall.Core.Cards.dll took REKINDLING's 105 rows down to an empty array, and
                // the only reason the corpus survived was that it had been committed minutes
                // earlier. The parse failure is what should be loud; the file is what must be left
                // alone.
                try
                {
                    var loaded = new List<EditableCard>();
                    foreach (var (def, _) in CardJsonRowIO.LoadRows(path))
                        loaded.Add(EditableCard.FromDefinition(def));
                    _cards.AddRange(loaded);
                    _loadedSets.Add(set);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[CardAuthoring] {set} did NOT load and is now READ-ONLY in this " +
                        $"window — it will not be written until it parses. {e.GetType().Name}: {e.Message}. " +
                        "If this names an unknown property, Unity is compiling an older core than the " +
                        "one that authored the row: let it recompile and reopen this window.");
                }
            }
            _dirtyIds.Clear();
            InvalidateAll();
            ReloadComments();
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

            // NOTHING THAT CHANGES THE LIBRARY SITS ON THE LEFT, and that is the whole point of this
            // layout. The search field is the first control in the 240px list column immediately
            // below (DrawLibraryList), so the toolbar's left edge is the strip a hand travels
            // through on its way to the search box. New/Duplicate/Delete lived there and were hit by
            // accident; on 2026-09-03 that cost card 1029 (`grave_custodian`) — duplicated twice
            // reaching for search, then the ORIGINALS deleted, which silently renumbered a shipped
            // card to 1192 and orphaned every saved deck and ownership row holding 1029. It was
            // found by a test, days later, and not by anything in this window.
            //
            // So the left of the toolbar carries read-only controls only, and the mutating group
            // moves right, past the flexible space, where no control below it invites a click.
            if (GUILayout.Button("Validate All", EditorStyles.toolbarButton, GUILayout.Width(80))) CardSetValidator.RunAndLog();
            // The selected card's state, beside the button that checks all of them, so it is on
            // screen whatever the editor pane is scrolled to. The messages stay in the pinned panel
            // — a toolbar can carry a verdict, not three sentences.
            GUILayout.Space(8);
            GUILayout.Label(SelectedStatus(), EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            GUILayout.Label(_dirtyIds.Count > 0 ? $"● {_dirtyIds.Count} unsaved" : "saved", EditorStyles.miniLabel);
            GUILayout.Space(12);
            if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(50))) OnNew();
            if (GUILayout.Button("Duplicate", EditorStyles.toolbarButton, GUILayout.Width(70))) OnDuplicate();
            // Delete is separated from Duplicate deliberately. The 1029 loss took BOTH — a stray
            // Duplicate and then a Delete aimed at the wrong row — and they are the two whose
            // adjacency turns one slip into two.
            GUILayout.Space(16);
            if (GUILayout.Button("Delete", EditorStyles.toolbarButton, GUILayout.Width(50))) OnDelete();
            GUILayout.Space(16);
            if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(110))) SaveAll();
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
                // Searches the HANDLE, the name, and the ID. The id is in because it is the only
                // field that is unique — when two rows print the same name, typing the number is
                // the only way to reach a specific one, and until 2026-09-04 there was no way at
                // all. Substring rather than exact so a partial number narrows as you type.
                if (!string.IsNullOrEmpty(_search)
                    && card.Handle.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0
                    && card.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0
                    && card.Id.ToString(CultureInfo.InvariantCulture)
                        .IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var (core, lint) = Validate(card);
                string badge = BadgeFor(core, lint);
                string dirtyMark = _dirtyIds.Contains(card.Id) ? "*" : "";
                var style = i == _selected ? EditorStyles.helpBox : GUI.skin.label;
                // The display name AND THE ID (Sam, 2026-09-04, reversing "name alone" of
                // 2026-08-23). The earlier reasoning was that a number is not what a designer
                // scanning this list is looking for, which is true right up until two rows print
                // the same name — and then the name alone is not an identifier at all and the list
                // cannot tell them apart.
                //
                // That is not hypothetical: it cost card 1029. `grave_custodian` was duplicated
                // twice, so three rows read "Gravekeep Custodian", the ORIGINAL was deleted instead
                // of the copies, and a shipped id silently became 1192 — orphaning every saved deck
                // and ownership row holding 1029. The delete dialog named the handle and id
                // correctly; the LIST is where the wrong row got picked.
                //
                // The id is also the only unique field, so showing it is what makes the search box
                // above worth pointing at a number.
                // Only OPEN comments mark a row. A resolved one is a record, and a marker that
                // never clears degrades to "this card has ever been discussed", which nobody can
                // act on. Same rule the web grid's badge follows.
                string commentMark = "";
                if (_comments.TryGetValue(card.Id, out var notes))
                {
                    int open = 0;
                    for (int n = 0; n < notes.Count; n++) if (!notes[n].Resolved) open++;
                    if (open > 0) commentMark = $"  ({open})";
                }

                if (GUILayout.Button($"{badge} {card.Name}{dirtyMark} · {card.Id}{commentMark}", style))
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
            // the machinery behind it, so authoring reads top-down. The text itself is DERIVED from
            // that machinery and read-only here (see DrawCardText) — it leads the pane as the thing
            // being aimed at, not as the first thing to fill in.
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
            EditorGUILayout.LabelField("Sound Effects", EditorStyles.boldLabel);
            DrawSoundEffects(card);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rules", EditorStyles.boldLabel);
            DrawRules(card);

            if (EditorGUI.EndChangeCheck())
            {
                _dirtyIds.Add(card.Id);
                Invalidate(card.Id);
            }

            // OUTSIDE both change checks, like DrawArtPreview above: every control in here is a
            // button, and a clicked button sets GUI.changed — inside a check, expanding a comment
            // list would mark the card unsaved.
            DrawComments(card);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void ReloadComments()
        {
            CardComments.ResetWarnings();
            _comments = CardComments.Load();
        }

        /// <summary>
        /// Editor comments from the web tool, read out of the sidecar and shown against the card
        /// they were written on.
        /// </summary>
        /// <remarks>
        /// READ-ONLY here on purpose. The comment is authored in the browser, and this window is
        /// where the person who has to act on it works — that is the whole of what makes the
        /// feature worth having. Writing them from both ends would need a merge, and the repo
        /// copy and the deployed copy already need one (<c>comments.py merge</c>); adding a third
        /// writer that cannot see either of the other two is how a note goes missing.
        ///
        /// <para>The file lives outside Assets/, so nothing in Unity notices when it changes —
        /// hence the explicit Reload button rather than a watcher that would be wrong to trust.</para>
        /// </remarks>
        private void DrawComments(EditableCard card)
        {
            _comments.TryGetValue(card.Id, out var all);
            int total = all?.Count ?? 0;
            int open = 0;
            for (int i = 0; i < total; i++) if (!all[i].Resolved) open++;

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                open > 0 ? $"Comments ({open} open)" : "Comments", EditorStyles.boldLabel);
            if (total > open)
            {
                _showResolvedComments = GUILayout.Toggle(
                    _showResolvedComments, $"show {total - open} resolved",
                    EditorStyles.miniButton, GUILayout.Width(120));
            }
            if (GUILayout.Button("Reload", EditorStyles.miniButton, GUILayout.Width(60)))
                ReloadComments();
            EditorGUILayout.EndHorizontal();

            if (total == 0)
            {
                EditorGUILayout.LabelField(
                    "none — written in the web tool (cards.outofcontext.to)", EditorStyles.miniLabel);
                return;
            }

            foreach (var c in all)
            {
                if (c.Resolved && !_showResolvedComments) continue;
                // Info rather than None so the block reads as content, and so a resolved one is
                // visually distinct from an open one without a second style to maintain.
                EditorGUILayout.HelpBox(
                    $"{c.Author} · {c.At}{(c.Resolved ? " · resolved" : "")}\n{c.Text}",
                    c.Resolved ? MessageType.None : MessageType.Info);
            }
        }

        /// <summary>
        /// The card face, DERIVED from the keywords and rules above it and shown read-only.
        /// </summary>
        /// <remarks>
        /// <b>In this window the text is not authored — it is rendered.</b> C26 compares
        /// <see cref="CardDefinition.Text"/> to <c>CardTextGenerator.Generate</c> with
        /// <see cref="StringComparison.Ordinal"/>, so the only value the field may legally hold is
        /// the generated one. A hand-typed field whose sole correct value is computable is not an
        /// input; it is a quiz with one answer, and every way of getting it wrong — a trailing
        /// space, a doubled space after a period, a keyword sentence that belongs in front, a rule
        /// left over from the last version of the card — fails identically and looks identical on
        /// screen. Sam, 2026-09-04, after an <c>AEGIS</c> carrier keyword put one word at the front
        /// of a sentence he had already matched character for character: <i>"if this thing is going
        /// to work this way and refuse to accept a card's text for reasons as nitty gritty as
        /// punctuation or word order and leave 0 wiggle room for an author to manipulate the
        /// text..... just make it fill out the field."</i>
        ///
        /// <para>So the field fills itself on every repaint. This is a reversal of the window's half
        /// of the 2026-08-16 text-direction change — which made the sentence the authored thing and
        /// the rendering the check — and ONLY of the window's half. <b>The engine direction is
        /// unchanged:</b> <c>text</c> is still the canonical field on disk, still what a player
        /// reads, and C26 is still an exact-match error in CI. Nothing here waives a rule; it stops
        /// asking a human to type a string the machine already knows.</para>
        ///
        /// <para><b>Unrenderable is the one case that still needs a person</b>, and it is now the
        /// only thing this method can report. A rule shape with no phrasing cannot be derived from,
        /// so the stale text is left exactly as it was and the box says to report the gap rather
        /// than re-shape the card to fit — which is the whole point C26 was defending, and the only
        /// signal that was ever worth an author's attention.</para>
        ///
        /// <para>Rendered through <c>SelectableLabel</c>, not a disabled <c>TextField</c>: a
        /// disabled field cannot be selected, so its contents cannot be copied out of the window.
        /// That is how the sentence gets into a bug report.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER:</b> if rules are ever parsed FROM the sentence — the
        /// forcing function named in <c>CardValidator</c>'s C26 block — the field becomes a real
        /// input again and this method goes back to being a two-way editor.</para>
        /// </remarks>
        private void DrawCardText(EditableCard card)
        {
            var divergence = CardTextGenerator.Check(card.ToDefinition(), LiveDb(), out string expected);

            if (divergence == CardTextDivergence.Unrenderable)
            {
                DrawDerivedText(card.Text);
                EditorGUILayout.HelpBox(
                    "C26: these rules are a shape with no phrasing, so no text can be derived for them. " +
                    "The text below is whatever was last saved. This is a grammar gap — report the " +
                    "card rather than re-shaping it to fit.",
                    MessageType.Error);
                return;
            }

            // Assigning outside a control means GUI.changed does not fire, so the enclosing change
            // check will not mark the row unsaved on its own — and a row whose stored text no longer
            // matches its rules is exactly the row that must be written back. Mark it here.
            if (!string.Equals(card.Text, expected, StringComparison.Ordinal))
            {
                card.Text = expected;
                _dirtyIds.Add(card.Id);
                Invalidate(card.Id);
            }

            DrawDerivedText(card.Text);
        }

        /// <summary>Read-only, wrapped, and selectable so it can be copied out of the window.</summary>
        private static void DrawDerivedText(string text)
        {
            text = text ?? "";
            GUIStyle style = DerivedTextStyle;

            // CalcHeight needs the width the label will actually get: the pane minus the prefix
            // column and the surrounding padding. Floored so a narrow window cannot ask for a
            // negative height, which throws inside the layout system rather than drawing badly.
            float width = Mathf.Max(60f, EditorGUIUtility.currentViewWidth - EditorGUIUtility.labelWidth - 32f);
            float height = Mathf.Max(EditorGUIUtility.singleLineHeight,
                style.CalcHeight(new GUIContent(text), width));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("text");
            EditorGUILayout.SelectableLabel(text, style, GUILayout.Height(height));
            EditorGUILayout.EndHorizontal();
        }

        private static GUIStyle _derivedTextStyle;

        private static GUIStyle DerivedTextStyle
        {
            get
            {
                // Built on demand rather than in a field initialiser: EditorStyles is null until the
                // first OnGUI, so touching it during construction returns a style with no font.
                if (_derivedTextStyle == null)
                    _derivedTextStyle = new GUIStyle(EditorStyles.textField) { wordWrap = true };
                return _derivedTextStyle;
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

        private void DrawSoundEffects(EditableCard card)
        {
            for (int i = 0; i < card.SoundEffects.Count; i++)
            {
                EditableSoundEffect sound = card.SoundEffects[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"soundEffects[{i}]", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("✕", GUILayout.Width(24)))
                {
                    card.SoundEffects.RemoveAt(i);
                    _dirtyIds.Add(card.Id);
                    Invalidate(card.Id);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                sound.Trigger = (Trigger)EditorGUILayout.EnumPopup("trigger", sound.Trigger);
                AudioClip current = ResolveSoundClip(sound.SoundId);
                AudioClip picked = (AudioClip)EditorGUILayout.ObjectField(
                    "clip", current, typeof(AudioClip), allowSceneObjects: false);

                if (picked != current)
                {
                    if (picked == null)
                    {
                        sound.SoundId = "";
                    }
                    else if (AddressablesSetup.TryRegisterSoundEffect(
                                 picked, out string soundId, out string error))
                    {
                        sound.SoundId = soundId;
                        _soundClips[soundId] = picked;
                    }
                    else
                    {
                        ShowNotification(new GUIContent(error));
                        Debug.LogWarning("[CardAuthoring] " + error);
                    }
                }

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.TextField("soundId", sound.SoundId);

                if (sound.Trigger == Trigger.AURA)
                    EditorGUILayout.HelpBox(
                        "AURA is continuous recomputation, not a trigger event. Choose an ON_* trigger.",
                        MessageType.Error);
                else if (!string.IsNullOrEmpty(sound.SoundId) && current == null && picked == null)
                    EditorGUILayout.HelpBox(
                        $"No AudioClip named '{sound.SoundId}' exists under {AddressablesSetup.SoundEffectsFolder}.",
                        MessageType.Error);

                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ Add Sound Effect"))
            {
                card.SoundEffects.Add(new EditableSoundEffect());
                _dirtyIds.Add(card.Id);
                Invalidate(card.Id);
            }
        }

        private AudioClip ResolveSoundClip(string soundId)
        {
            if (string.IsNullOrEmpty(soundId)) return null;
            if (_soundClips.TryGetValue(soundId, out AudioClip cached)) return cached;

            AudioClip found = null;
            foreach (string guid in AssetDatabase.FindAssets(
                         "t:AudioClip", new[] { AddressablesSetup.SoundEffectsFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), soundId,
                        StringComparison.Ordinal))
                    continue;
                found = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                break;
            }

            _soundClips[soundId] = found;
            return found;
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
                    rule.Count = EditorGUILayout.IntField("action.count", rule.Count);
                    break;
                case EditableRuleActionKind.Unearth:
                    // Unearth draws NO fields. Its offer size is fixed at UnearthAction.Options and
                    // is not the author's to set — a count box here would be a control that changes
                    // nothing, which is worse than no control at all.
                    EditorGUILayout.LabelField("action",
                        $"choose 1 of {UnearthAction.Options} — not authorable");
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

        /// <summary>The carrier-keyword popup with no label, for a row that already has one.</summary>
        private static Keyword DrawCarrierKeywordPopup(Keyword current)
        {
            int selected = Array.IndexOf(CarrierKeywordOptions, current);
            if (selected < 0) selected = 0;
            selected = EditorGUILayout.Popup(selected, CarrierKeywordNames);
            return CarrierKeywordOptions[selected];
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

            // One selection term, always written — there is no "has a selection" toggle. Counted
            // means N (the controller picks unless N reaches the selector's maximum), Random means
            // "random: N", and Automatic means "auto: N" for deterministic unbounded selectors.
            // C31 rejects Automatic on a bounded selector.
            target.SelectionCount = Mathf.Max(1,
                EditorGUILayout.IntField("target.selection count", target.SelectionCount));
            target.SelectionMode = (EditableTargetSelectionMode)EditorGUILayout.EnumPopup(
                "target.selection mode", target.SelectionMode);
            // Authored, not derived: whether this pick is the card's price is the one thing no
            // sentence can be read back to recover.
            target.Cost = (CostKind)EditorGUILayout.EnumPopup("target.cost", target.Cost);
            DrawFilters(target);
            target.Binding = EditorGUILayout.TextField("target.binding", target.Binding);
            target.DistinctFrom = EditorGUILayout.TextField("target.distinctFrom", target.DistinctFrom);
        }

        /// <summary>
        /// The target's filter clauses, as a growable list of axis-plus-value rows.
        ///
        /// <para>This replaced a pair of "filter has x" toggles, one per axis, which is a shape that
        /// only works while there are two of them. An axis added to <c>TargetFilterDef</c> now costs
        /// one arm of the value switch below and nothing else — no new toggle, no new field pair, and
        /// no third place to forget.</para>
        ///
        /// <para>An axis already in the list is not offered again: the target holds ONE value per
        /// axis, so a second clause could only overwrite the first and would read as a constraint
        /// that is not there. The add control disappears entirely once every axis is spoken for.</para>
        /// </summary>
        private static void DrawFilters(EditableTarget target)
        {
            EditorGUILayout.LabelField("target.filter", EditorStyles.boldLabel);
            int remove = -1;
            for (int i = 0; i < target.Filters.Count; i++)
            {
                EditableFilter clause = target.Filters[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditableFilterKind chosen =
                        (EditableFilterKind)EditorGUILayout.EnumPopup(clause.Kind, GUILayout.Width(90));
                    if (chosen != clause.Kind && !Holds(target, chosen)) clause.Kind = chosen;

                    switch (clause.Kind)
                    {
                        case EditableFilterKind.Damaged:
                            // No negate toggle here: Damaged is already the two-valued choice
                            // itself, so this toggle IS "negative" (Sam, 2026-09-05).
                            clause.Damaged = EditorGUILayout.Toggle(clause.Damaged);
                            EditorGUILayout.LabelField(clause.Damaged ? "damaged" : "undamaged");
                            break;
                        case EditableFilterKind.Tribe:
                            clause.Tribe = (Tribe)EditorGUILayout.EnumPopup(clause.Tribe);
                            clause.NegateTribe = EditorGUILayout.ToggleLeft(
                                "negative", clause.NegateTribe, GUILayout.Width(70));
                            break;
                        case EditableFilterKind.MaxCost:
                            clause.MaxCost = Mathf.Clamp(EditorGUILayout.IntField(clause.MaxCost), 0, 25);
                            clause.NegateCost = EditorGUILayout.ToggleLeft(
                                "negative", clause.NegateCost, GUILayout.Width(70));
                            EditorGUILayout.LabelField(clause.NegateCost ? "or more" : "or less");
                            break;
                        case EditableFilterKind.Keyword:
                            // The same popup a carrier keyword uses, so an action marker is not
                            // offered here either — C14L would reject it, and a filter naming one
                            // (negated or not) would either select nobody or everybody.
                            clause.Keyword = DrawCarrierKeywordPopup(clause.Keyword);
                            clause.NegateKeyword = EditorGUILayout.ToggleLeft(
                                "negative", clause.NegateKeyword, GUILayout.Width(70));
                            break;
                    }

                    if (GUILayout.Button("−", GUILayout.Width(22))) remove = i;
                }
            }
            if (remove >= 0) target.Filters.RemoveAt(remove);

            EditableFilterKind? free = FirstUnused(target);
            if (free.HasValue && GUILayout.Button("add filter", GUILayout.Width(90)))
                target.Filters.Add(new EditableFilter { Kind = free.Value });
        }

        /// <summary>Whether the target already narrows on an axis.</summary>
        private static bool Holds(EditableTarget target, EditableFilterKind kind)
        {
            foreach (EditableFilter clause in target.Filters)
                if (clause.Kind == kind) return true;
            return false;
        }

        /// <summary>The first axis this target is not already narrowed by, or null when none is.</summary>
        private static EditableFilterKind? FirstUnused(EditableTarget target)
        {
            foreach (EditableFilterKind kind in (EditableFilterKind[])Enum.GetValues(typeof(EditableFilterKind)))
                if (!Holds(target, kind)) return kind;
            return null;
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
                // NEVER write a set this window did not read. An unloaded set has no rows in
                // memory, so "write what is in memory" means "empty the file" — the difference
                // between a set with no cards and a set that failed to parse is invisible in
                // `bySet` and total on disk.
                if (!_loadedSets.Contains(set))
                {
                    Debug.LogWarning($"[CardAuthoring] {set} was not loaded, so it is not being " +
                        "written. Its file on disk is untouched.");
                    continue;
                }
                bySet.TryGetValue(set, out var rows);
                CardJsonRowIO.SaveRows(CardJsonRowIO.PathForSet(set), rows ?? new List<CardDefinition>());
            }

            AssetDatabase.Refresh();
            _dirtyIds.Clear();
        }
    }
}
