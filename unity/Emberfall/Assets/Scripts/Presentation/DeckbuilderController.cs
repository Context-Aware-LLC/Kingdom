using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// C-G0-5 UI Toolkit spike — the S7 deckbuilder slice (ui/collection-deckbuilder.md §2).
    /// De-risks the five things the real screen lives or dies on: ListView virtualization of the
    /// full card pool, live L1–L5 legality (via <see cref="DeckValidator"/>) gating Save, single-
    /// source theme.uss token theming, the 22px text floor, and the responsive split view.
    ///
    /// Keeper-quality: binds REAL <see cref="CardDefinition"/> data (same JsonCardDatabase path as
    /// GameBootstrap) and reuses the shared, server-side-portable legality predicate — not stubs.
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// DeckbuilderScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DeckbuilderController : MonoBehaviour
    {
        [SerializeField] private CardClass _heroClass = CardClass.EMBERWARD;

        private ICardDatabase _db;
        private DeckList _deck;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();

        // Cached elements.
        private VisualElement _root;
        private ListView _collectionList;
        private ListView _deckList;
        private VisualElement _curve;
        private VisualElement _legalityBox;
        private Label _countLabel;
        private Label _legalityPill;
        private Label _heroLabel;
        private Button _saveButton;

        private void OnEnable()
        {
            _db = CardDatabaseLoader.Load();
            _deck = new DeckList { HeroClass = _heroClass, Name = $"{_heroClass} Deck" };
            BuildPool();

            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Deckbuilder] No rootVisualElement — is the UXML assigned?"); return; }

            CacheElements();
            WireCollectionList();
            WireDeckList();
            WireChrome();
            Refresh();
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
                    if (DeckValidator.CanAdd(_deck, def)) { AddCopy(def); progressed = true; }
                }
            }
        }
#endif

        // ---- Pool: class ∪ neutral, sorted by cost then name (§2 left list) ----
        private void BuildPool()
        {
            _pool.Clear();
            foreach (var def in _db.AllCollectible())
                if (def.Class == CardClass.NEUTRAL || def.Class == _deck.HeroClass)
                    _pool.Add(def);
            _pool.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost)
                                                  : string.CompareOrdinal(a.Name, b.Name));
        }

        private void CacheElements()
        {
            _collectionList = _root.Q<ListView>("collection-list");
            _deckList       = _root.Q<ListView>("deck-list");
            _curve          = _root.Q<VisualElement>("curve-strip");
            _legalityBox    = _root.Q<VisualElement>("legality-box");
            _countLabel     = _root.Q<Label>("count-label");
            _legalityPill   = _root.Q<Label>("legality-pill");
            _heroLabel      = _root.Q<Label>("hero-label");
            _saveButton     = _root.Q<Button>("save-button");

            if (_heroLabel != null)
            {
                _heroLabel.text = _heroClass.ToString();
                _heroLabel.style.color = ClassColorToken(_heroClass);
            }
        }

        // ---- Collection ListView (virtualized) ----
        private void WireCollectionList()
        {
            _collectionList.fixedItemHeight = 54;
            _collectionList.selectionType = SelectionType.None;
            _collectionList.itemsSource = _pool;
            _collectionList.makeItem = MakeCardRow;
            _collectionList.bindItem = BindCollectionRow;
            _collectionList.RegisterCallback<ClickEvent>(OnCollectionClick);
        }

        private void BindCollectionRow(VisualElement row, int index)
        {
            var def = _pool[index];
            row.userData = def;
            SetCardRow(row, def, _deck.CountOf(def.Id), DeckValidator.MaxCopies(def.Rarity));
            row.EnableInClassList("card-row--disabled", !DeckValidator.CanAdd(_deck, def));
        }

        private void OnCollectionClick(ClickEvent evt)
        {
            // Walk up to the .card-row that carries the CardDefinition (ListView recycles rows).
            var e = evt.target as VisualElement;
            while (e != null && !(e.userData is CardDefinition)) e = e.parent;
            if (!(e?.userData is CardDefinition def)) return;
            AddCopy(def);
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
            if (!DeckValidator.CanAdd(_deck, def)) return;   // L1 ceiling / L2/L3 cap / L4 off-class
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
            _saveButton?.RegisterCallback<ClickEvent>(_ =>
                Debug.Log($"[Deckbuilder] Save '{_deck.Name}' ({_deck.TotalCount} cards) — valid, persisting."));
            _root.Q<Button>("done-button")?.RegisterCallback<ClickEvent>(_ =>
                Debug.Log("[Deckbuilder] Done → S6."));
        }

        // ---- The live legality loop: single source, runs on every mutation ----
        private void Refresh()
        {
            _collectionList.RefreshItems();
            _deckList.itemsSource = _deck.Entries;
            _deckList.RefreshItems();
            RebuildCurve();

            var result = DeckValidator.Validate(_deck, _db);

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

            _saveButton?.SetEnabled(result.IsValid);   // Save gated by the predicate (§1)
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
