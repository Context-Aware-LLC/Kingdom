using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Sort + filter controls over a card pool, as a module both the Collection screen and the
    /// deckbuilder can host (Sam, 2026-08-07). Emits a <see cref="CardFilter"/> and a
    /// <see cref="CardSort"/>; it never touches a pool or a tile itself, so the thing it drives is
    /// interchangeable — <see cref="CardWall"/> today.
    ///
    /// <para><b>The predicate layer already existed.</b> Everything below is controls: text, class,
    /// rarity, type and cost all map onto facets <see cref="CardQuery"/> has supported since EF-035,
    /// and the sort keys are its own enum. Nothing here reimplements matching.</para>
    ///
    /// <para><b>Binds what is present.</b> Every control is looked up by name and every absent one is
    /// simply a facet this host does not offer — so a screen wanting only a search box declares only
    /// the search box, and no second implementation appears. Expected names: <c>query-search</c>,
    /// <c>query-classes</c>, <c>query-rarities</c>, <c>query-types</c>, <c>query-costs</c>,
    /// <c>query-sort</c>, <c>query-sort-dir</c>.</para>
    ///
    /// <para><b>Chips are generated from the enums, never authored in UXML.</b> A ninth class or a
    /// fifth rarity appears here the moment it appears in <c>Enums.cs</c>. Authoring them by hand is
    /// how a set of things silently stops matching the set it describes.</para>
    /// </summary>
    public sealed class CardQueryBar
    {
        private const string ChipClass = "db-chip";
        private const string ChipOnClass = "db-chip--on";

        // Cost is SINGLE-select while every other facet is multi. Not a UI preference: CardFilter
        // expresses cost as MinCost/MaxCost, a range, so {1, 3, 5} has no representation. One cost
        // maps exactly (Min = Max = n), and "7+" maps to Min = 7 with no ceiling. Multi-select here
        // would have to either lie or silently widen to the enclosing range.
        private const int CostChipMax = 7;      // 0..6, then a 7+ bucket

        private readonly TextField _search;
        private readonly DropdownField _sort;
        private readonly Button _sortDir;

        private readonly HashSet<CardClass> _classes = new HashSet<CardClass>();
        private readonly HashSet<Rarity> _rarities = new HashSet<Rarity>();
        private readonly HashSet<CardType> _types = new HashSet<CardType>();
        private int _cost = -1;                 // -1 = any
        private bool _descending;

        private readonly List<CardSortKey> _sortKeys = new List<CardSortKey>();

        /// <summary>Raised whenever any control changes. Hosts re-query on this and nothing else.</summary>
        public event Action Changed;

        public CardQueryBar(VisualElement root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            _search  = root.Q<TextField>("query-search");
            _sort    = root.Q<DropdownField>("query-sort");
            _sortDir = root.Q<Button>("query-sort-dir");

            if (_search != null)
                _search.RegisterValueChangedCallback(_ => Changed?.Invoke());

            BuildChips(root.Q<VisualElement>("query-classes"), AllClasses(), Label, _classes);
            BuildChips(root.Q<VisualElement>("query-rarities"), AllRarities(), Label, _rarities);
            BuildChips(root.Q<VisualElement>("query-types"), AllTypes(), Label, _types);
            BuildCostChips(root.Q<VisualElement>("query-costs"));
            BuildSort();
        }

        /// <summary>The query the controls currently describe. Null filter facets mean unconstrained.</summary>
        public CardFilter Filter
        {
            get
            {
                var f = new CardFilter();

                string text = _search?.value;
                if (!string.IsNullOrWhiteSpace(text)) f.Text = text;

                if (_classes.Count  > 0) f.Classes  = new List<CardClass>(_classes);
                if (_rarities.Count > 0) f.Rarities = new List<Rarity>(_rarities);
                if (_types.Count    > 0) f.Types    = new List<CardType>(_types);

                if (_cost >= 0)
                {
                    f.MinCost = _cost;
                    if (_cost < CostChipMax) f.MaxCost = _cost;   // 7+ has no ceiling
                }

                return f;
            }
        }

        public CardSort Sort
        {
            get
            {
                if (_sort == null || _sortKeys.Count == 0)
                    return _descending ? new CardSort(CardSort.Default.Key, true) : CardSort.Default;

                int i = _sort.index;
                if (i < 0 || i >= _sortKeys.Count) i = 0;
                return new CardSort(_sortKeys[i], _descending);
            }
        }

        /// <summary>Clear every facet and return to the default order.</summary>
        public void Reset()
        {
            _classes.Clear();
            _rarities.Clear();
            _types.Clear();
            _cost = -1;
            _descending = false;
            if (_search != null) _search.SetValueWithoutNotify(string.Empty);
            if (_sort != null) _sort.index = 0;
            SyncChipStates();
            UpdateSortDirLabel();
            Changed?.Invoke();
        }

        // ---- Chip construction ----------------------------------------------------------------
        private void BuildChips<T>(VisualElement host, IReadOnlyList<T> values,
                                   Func<T, string> label, HashSet<T> selected)
        {
            if (host == null) return;
            host.Clear();

            foreach (var value in values)
            {
                var captured = value;
                var chip = new Button { text = label(value) };
                chip.AddToClassList(ChipClass);
                chip.userData = captured;
                chip.clicked += () =>
                {
                    if (!selected.Remove(captured)) selected.Add(captured);
                    chip.EnableInClassList(ChipOnClass, selected.Contains(captured));
                    Changed?.Invoke();
                };
                host.Add(chip);
                _chips.Add(chip);
            }
        }

        private void BuildCostChips(VisualElement host)
        {
            if (host == null) return;
            host.Clear();

            for (int c = 0; c <= CostChipMax; c++)
            {
                int captured = c;
                var chip = new Button { text = c < CostChipMax ? c.ToString() : $"{CostChipMax}+" };
                chip.AddToClassList(ChipClass);
                chip.userData = captured;
                chip.clicked += () =>
                {
                    _cost = _cost == captured ? -1 : captured;   // clicking the live one clears it
                    SyncCostChips(host);
                    Changed?.Invoke();
                };
                host.Add(chip);
            }
        }

        private void BuildSort()
        {
            if (_sort != null)
            {
                _sortKeys.Clear();
                var choices = new List<string>();
                foreach (CardSortKey key in Enum.GetValues(typeof(CardSortKey)))
                {
                    _sortKeys.Add(key);
                    choices.Add(key.ToString());
                }
                _sort.choices = choices;
                _sort.index = Math.Max(0, _sortKeys.IndexOf(CardSort.Default.Key));
                _sort.RegisterValueChangedCallback(_ => Changed?.Invoke());
            }

            if (_sortDir != null)
            {
                UpdateSortDirLabel();
                _sortDir.clicked += () =>
                {
                    _descending = !_descending;
                    UpdateSortDirLabel();
                    Changed?.Invoke();
                };
            }
        }

        private void UpdateSortDirLabel()
        {
            if (_sortDir != null) _sortDir.text = _descending ? "Desc" : "Asc";
        }

        private void SyncChipStates()
        {
            SyncGroup(_classes);
            SyncGroup(_rarities);
            SyncGroup(_types);
        }

        private void SyncGroup<T>(HashSet<T> selected)
        {
            // Chips hold their value in userData, so the live set is re-readable without keeping a
            // parallel list of elements per facet.
            foreach (var chip in _chips)
                if (chip.userData is T value)
                    chip.EnableInClassList(ChipOnClass, selected.Contains(value));
        }

        private void SyncCostChips(VisualElement host)
        {
            foreach (var child in host.Children())
                if (child.userData is int c)
                    child.EnableInClassList(ChipOnClass, c == _cost);
        }

        private readonly List<VisualElement> _chips = new List<VisualElement>();

        // ---- Enum sets offered as chips --------------------------------------------------------
        //
        // HERO_POWER is not collectible and Tribe.NONE is the absence of a tribe, so neither is a
        // facet a player would tick. Tribes and Keywords get no chips at all in this pass: free text
        // already unions keyword, type and tribe names, so "bulwark" and "dragon" both work typed —
        // and the Keyword enum carries reserved wire slots (SILENCE, BACKDRAFT, REINFORCE) that are
        // illegal as carrier keywords, which would render as chips matching nothing.
        private static IReadOnlyList<CardClass> AllClasses()
        {
            var list = new List<CardClass>();
            foreach (CardClass c in Enum.GetValues(typeof(CardClass))) list.Add(c);
            return list;
        }

        private static IReadOnlyList<Rarity> AllRarities()
        {
            var list = new List<Rarity>();
            foreach (Rarity r in Enum.GetValues(typeof(Rarity))) list.Add(r);
            return list;
        }

        private static IReadOnlyList<CardType> AllTypes() =>
            new List<CardType> { CardType.MINION, CardType.SPELL, CardType.WEAPON };

        private static string Label<T>(T value)
        {
            string s = value.ToString();
            if (s.Length <= 1) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1).ToLowerInvariant().Replace('_', ' ');
        }
    }
}
