using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The S7 deckbuilder slice (dev-docs/ui/client-surfaces.md:9), grown out of the C-G0-5 spike:
    /// ListView virtualization of the full card pool, live L1–L5 legality (via
    /// <see cref="DeckValidator"/>) driving the legality pill, single-source theme.uss token
    /// theming, the 22px text floor, and the responsive split view.
    ///
    /// <para>Opens one real <see cref="Character"/>'s deck from <see cref="PlayerAccount.Local"/> —
    /// <see cref="PendingCharacterId"/>, set by <c>MainMenuController.OnDeckBuilder</c> before this
    /// scene loads, or the roster's first character when that is empty (so the scene still opens
    /// standalone in the Editor). There is no character switcher yet — S6's sidebar is separate,
    /// unbuilt scope — so today this is always the one character's own deck.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// DeckbuilderScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DeckbuilderController : MonoBehaviour
    {
        /// <summary>Which character to open, set by the caller before loading this scene.</summary>
        public static string PendingCharacterId;

        private PlayerAccount _account;
        private Character _character;
        private ICardDatabase _db;
        private DeckList _deck;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private CardFaceBaker _faceBaker;

        // Cached elements.
        private VisualElement _root;
        private VisualElement _collectionGrid;
        private readonly Dictionary<string, VisualElement> _collectionTiles = new Dictionary<string, VisualElement>();
        private ListView _deckList;
        private VisualElement _curve;
        private VisualElement _legalityBox;
        private Label _countLabel;
        private Label _legalityPill;
        private Label _heroLabel;
        private Button _saveButton;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Deckbuilder] No rootVisualElement — is the UXML assigned?"); return; }

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Deckbuilder] could not open this device's collection: {ex.Message}");
                return;
            }

            IReadOnlyList<Character> roster = _account.Roster();
            _character = FindPending(roster) ?? (roster.Count > 0 ? roster[0] : null);
            if (_character == null)
            {
                Debug.LogError("[Deckbuilder] No character on this account — Main Menu should have " +
                                "gated this scene behind character creation.");
                return;
            }

            _deck = _account.DecksOf(_character, _db).Opening?.Deck;
            if (_deck == null)
            {
                Debug.LogError($"[Deckbuilder] '{_character.Name}' has no deck to open.");
                return;
            }

            BuildPool();
            CacheElements();
            InitFaceBaker();
            BuildCollectionGrid();
            WireDeckList();
            WireChrome();
            Refresh();
        }

        // The real card, baked to a texture (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.9) — one
        // card front implementation (BoardController.CreatePresenter), photographed, never a
        // second UI Toolkit re-implementation of the card face drifting from it.
        private void InitFaceBaker()
        {
            var bakerGo = new GameObject("CardFaceBaker");
            bakerGo.transform.SetParent(transform, false);
            _faceBaker = bakerGo.AddComponent<CardFaceBaker>();
            _faceBaker.Initialize(_db);
        }

        private Character FindPending(IReadOnlyList<Character> roster)
        {
            if (string.IsNullOrEmpty(PendingCharacterId)) return null;
            foreach (var c in roster)
                if (c.Id == PendingCharacterId) return c;
            return null;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor/spike-only: fill the deck toward <paramref name="target"/> cards from the pool,
        /// respecting L1–L5 via the real add path, to exercise the live legality loop for verification.
        /// </summary>
        public void DevFill(int target)
        {
            bool progressed = true;
            while (_deck.TotalCount < target && progressed)
            {
                progressed = false;
                foreach (var def in _pool)
                {
                    if (_deck.TotalCount >= target) break;
                    if (DeckValidator.CanAdd(_deck, _character, def)) { AddCopy(def); progressed = true; }
                }
            }
        }
#endif

        // ---- Pool: character's pairing ∪ neutral, sorted by cost then name (§2 left list) ----
        private void BuildPool()
        {
            _pool.Clear();
            foreach (var def in _db.AllCollectible())
                if (_character.AllowsClass(def.Class))
                    _pool.Add(def);
            _pool.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost)
                                                  : string.CompareOrdinal(a.Name, b.Name));
        }

        private void CacheElements()
        {
            _collectionGrid = _root.Q<VisualElement>("collection-grid");
            _deckList       = _root.Q<ListView>("deck-list");
            _curve          = _root.Q<VisualElement>("curve-strip");
            _legalityBox    = _root.Q<VisualElement>("legality-box");
            _countLabel     = _root.Q<Label>("count-label");
            _legalityPill   = _root.Q<Label>("legality-pill");
            _heroLabel      = _root.Q<Label>("hero-label");
            _saveButton     = _root.Q<Button>("save-button");

            if (_heroLabel != null)
            {
                _heroLabel.text = $"{_character.Name} · {Pretty(_character.PrimaryClass)}/{Pretty(_character.SecondaryClass)}";
                _heroLabel.style.color = ClassColorToken(_character.PrimaryClass);
            }
        }

        // ---- Collection wall (§2.9): one persistent tile per pool card, built once. Not
        // virtualized — ~90 cards of baked (cached) textures is cheap next to the 13-renderer
        // hierarchy a virtualized ListView of live cards would need instead. ----
        private void BuildCollectionGrid()
        {
            _collectionGrid.Clear();
            _collectionTiles.Clear();

            foreach (var def in _pool)
            {
                var tile = new VisualElement();
                tile.AddToClassList("card-tile");
                tile.userData = def;

                var copies = new Label { name = "copies" };
                copies.AddToClassList("tile-copies");
                tile.Add(copies);

                tile.RegisterCallback<ClickEvent>(_ => AddCopy(def));

                string defId = def.Id;
                _faceBaker.Bake(def, tex =>
                {
                    if (tex == null) return;
                    tile.style.backgroundImage = new StyleBackground(Background.FromRenderTexture((RenderTexture)tex));
                });

                _collectionGrid.Add(tile);
                _collectionTiles[def.Id] = tile;
            }
        }

        private void RefreshCollectionTiles()
        {
            foreach (var def in _pool)
            {
                if (!_collectionTiles.TryGetValue(def.Id, out VisualElement tile)) continue;

                int count = _deck.CountOf(def.Id);
                int max = DeckValidator.MaxCopies(def.Rarity);
                var copies = tile.Q<Label>("copies");
                if (copies != null) copies.text = count > 0 ? $"{count}/{max}" : $"·/{max}";

                tile.EnableInClassList("card-tile--disabled", !DeckValidator.CanAdd(_deck, _character, def));
            }
        }

        // ---- Deck ListView ----
        private void WireDeckList()
        {
            _deckList.fixedItemHeight = 54;
            _deckList.selectionType = SelectionType.None;
            _deckList.itemsSource = _deck.Entries;
            _deckList.makeItem = MakeCardRow;
            _deckList.bindItem = BindDeckRow;
            _deckList.RegisterCallback<ClickEvent>(OnDeckClick);
        }

        private void BindDeckRow(VisualElement row, int index)
        {
            var entry = _deck.Entries[index];
            if (!_db.TryGet(entry.CardId, out var def)) { row.style.display = DisplayStyle.None; return; }
            row.style.display = DisplayStyle.Flex;
            row.userData = def;
            SetCardRow(row, def, entry.Count, DeckValidator.MaxCopies(def.Rarity));
            row.EnableInClassList("card-row--disabled", false);
        }

        private void OnDeckClick(ClickEvent evt)
        {
            var e = evt.target as VisualElement;
            while (e != null && !(e.userData is CardDefinition)) e = e.parent;
            if (!(e?.userData is CardDefinition def)) return;
            RemoveCopy(def);
        }

        // ---- Shared row factory (§2.3 tile is Phase 2; the spike uses a legible full-width row) ----
        private static VisualElement MakeCardRow()
        {
            var row = new VisualElement();
            row.AddToClassList("card-row");
            var cost = new Label { name = "cost" };   cost.AddToClassList("card-cost");
            var dot  = new VisualElement { name = "dot" }; dot.AddToClassList("card-rarity-dot");
            var name = new Label { name = "name" };   name.AddToClassList("card-name");
            var copies = new Label { name = "copies" }; copies.AddToClassList("card-copies");
            row.Add(cost); row.Add(dot); row.Add(name); row.Add(copies);
            return row;
        }

        private static void SetCardRow(VisualElement row, CardDefinition def, int count, int max)
        {
            row.Q<Label>("cost").text = def.Cost.ToString();
            row.Q<Label>("name").text = def.Name;
            row.Q<Label>("copies").text = count > 0 ? $"{count}/{max}" : $"·/{max}";

            var dot = row.Q<VisualElement>("dot");
            SwapPrefixedClass(dot, "rarity--", RarityClass(def.Rarity));
            SwapPrefixedClass(row, "edge--", ClassEdgeClass(def.Class));
        }

        // ---- Mutations ----
        private void AddCopy(CardDefinition def)
        {
            if (!DeckValidator.CanAdd(_deck, _character, def)) return;   // L1 ceiling / L2/L3 cap / L4 off-class
            int i = _deck.Entries.FindIndex(x => x.CardId == def.Id);
            if (i >= 0) _deck.Entries[i] = _deck.Entries[i].With(_deck.Entries[i].Count + 1);
            else        _deck.Entries.Add(new DeckEntry(def.Id, 1));
            SortDeck();
            Refresh();
        }

        private void RemoveCopy(CardDefinition def)
        {
            int i = _deck.Entries.FindIndex(x => x.CardId == def.Id);
            if (i < 0) return;
            int c = _deck.Entries[i].Count - 1;
            if (c <= 0) _deck.Entries.RemoveAt(i);
            else        _deck.Entries[i] = _deck.Entries[i].With(c);
            Refresh();
        }

        private void SortDeck()
        {
            _deck.Entries.Sort((a, b) =>
            {
                _db.TryGet(a.CardId, out var da);
                _db.TryGet(b.CardId, out var db);
                int ca = da?.Cost ?? 0, cb = db?.Cost ?? 0;
                return ca != cb ? ca.CompareTo(cb)
                                : string.CompareOrdinal(da?.Name, db?.Name);
            });
        }

        // ---- Chrome (name field, buttons) ----
        private void WireChrome()
        {
            var nameField = _root.Q<TextField>("deck-name");
            if (nameField != null)
            {
                nameField.value = _deck.Name;
                nameField.RegisterValueChangedCallback(e => _deck.Name = e.newValue);
            }
            _saveButton?.RegisterCallback<ClickEvent>(_ => SaveDeck());
            _root.Q<Button>("done-button")?.RegisterCallback<ClickEvent>(_ => Go("MainMenu"));
        }

        /// <summary>Persist the deck as-is. Never legality-gated (collection-deckbuilder-spec §1,
        /// Sam 2026-07-26): an illegal deck is a savable draft, not a blocked action — only queue
        /// eligibility (computed fresh on load, never stored) refuses it a match.</summary>
        private void SaveDeck()
        {
            _account.Save(_deck);
            Debug.Log($"[Deckbuilder] Saved '{_deck.Name}' ({_deck.TotalCount} cards) for {_character.Name}.");
        }

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the Editor).
        /// Mirrors <c>MainMenuController.Go</c> — see there for why <c>CanStreamedLevelBeLoaded</c>
        /// over <c>GetBuildIndexByScenePath</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[Deckbuilder] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }

        // ---- The live legality loop: single source, runs on every mutation ----
        private void Refresh()
        {
            RefreshCollectionTiles();
            _deckList.itemsSource = _deck.Entries;
            _deckList.RefreshItems();
            RebuildCurve();

            var result = DeckValidator.Validate(_deck, _character, _db);

            _countLabel.text = $"{_deck.TotalCount} / {DeckValidator.DeckSize}";

            _legalityPill.text = result.IsValid ? "Legal" : "Incomplete";
            _legalityPill.EnableInClassList("db-pill--valid", result.IsValid);
            _legalityPill.EnableInClassList("db-pill--invalid", !result.IsValid);

            _legalityBox.Clear();
            foreach (var v in result.Violations)
            {
                var msg = new Label(v.Message);
                msg.AddToClassList("legality-msg");
                _legalityBox.Add(msg);
            }
        }

        private void RebuildCurve()
        {
            _curve.Clear();
            const int buckets = 8;                 // 0..6, 7+
            var counts = new int[buckets];
            int peak = 1;
            foreach (var e in _deck.Entries)
            {
                if (!_db.TryGet(e.CardId, out var def)) continue;
                int b = Mathf.Clamp(def.Cost, 0, buckets - 1);
                counts[b] += e.Count;
                if (counts[b] > peak) peak = counts[b];
            }
            for (int i = 0; i < buckets; i++)
            {
                var col = new VisualElement(); col.AddToClassList("curve-col");
                var bar = new VisualElement(); bar.AddToClassList("curve-bar");
                bar.style.height = Length.Percent(100f * counts[i] / peak);
                var label = new Label(i < buckets - 1 ? i.ToString() : "7+");
                label.AddToClassList("curve-label");
                col.Add(bar); col.Add(label);
                _curve.Add(col);
            }
        }

        // ---- token helpers (class names mirror deckbuilder.uss) ----
        private static readonly string[] AllRarity = { "rarity--common", "rarity--rare", "rarity--epic", "rarity--legendary" };
        private static readonly string[] AllEdge = { "edge--neutral", "edge--emberward", "edge--tidecaller", "edge--gravekeep", "edge--verdant" };

        private static string RarityClass(Rarity r) => r switch
        {
            Rarity.RARE => "rarity--rare",
            Rarity.EPIC => "rarity--epic",
            Rarity.LEGENDARY => "rarity--legendary",
            _ => "rarity--common",
        };

        private static string ClassEdgeClass(CardClass c) => c switch
        {
            CardClass.EMBERWARD => "edge--emberward",
            CardClass.TIDECALLER => "edge--tidecaller",
            CardClass.GRAVEKEEP => "edge--gravekeep",
            CardClass.VERDANT => "edge--verdant",
            _ => "edge--neutral",
        };

        private static void SwapPrefixedClass(VisualElement e, string prefix, string keep)
        {
            var all = prefix == "rarity--" ? AllRarity : AllEdge;
            foreach (var c in all) e.EnableInClassList(c, c == keep);
        }

        /// <summary>Enum name as a player reads it: EMBERWARD → Emberward.</summary>
        private static string Pretty(CardClass c)
        {
            string raw = c.ToString();
            return raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();
        }

        private static Color ClassColorToken(CardClass c) => c switch
        {
            CardClass.EMBERWARD => new Color(229 / 255f, 96 / 255f, 42 / 255f),
            CardClass.TIDECALLER => new Color(31 / 255f, 138 / 255f, 143 / 255f),
            CardClass.GRAVEKEEP => new Color(126 / 255f, 107 / 255f, 158 / 255f),
            CardClass.VERDANT => new Color(75 / 255f, 154 / 255f, 84 / 255f),
            _ => new Color(138 / 255f, 143 / 255f, 152 / 255f),
        };
    }
}
