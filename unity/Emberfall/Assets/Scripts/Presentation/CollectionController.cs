using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The whole card pool, with no deck panel (Sam, 2026-08-07). Reached from the roster's
    /// View Collection button.
    ///
    /// <para><b>Why this is a separate screen rather than a mode of the deckbuilder.</b> The
    /// deckbuilder's wall is character-scoped by construction — its <c>BuildPool</c> admits only the
    /// open character's pairing plus neutral — so it structurally cannot show you the game. This
    /// screen has no character and no deck, which is the entire difference between them.</para>
    ///
    /// <para><b>It shows the full collectible pool, NOT what you own.</b> There is no card-ownership
    /// model on the client: <c>ICollectionStore</c> persists characters, decks and profile, and
    /// nothing anywhere holds an owned-card count. That is the documented state of the game rather
    /// than an omission here — <see cref="DeckValidator"/> records it as "NOT enforced in M2:
    /// ownership/collection counts (full-pool alpha)" and reserves L6 for when it lands. When L6
    /// arrives, this screen filters on it and the count label below stops meaning "the game" and
    /// starts meaning "yours". Until then, do not describe this screen as a collection of owned
    /// cards in player-facing copy.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// CollectionScreen.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CollectionController : MonoBehaviour
    {
        /// <summary>Where Back returns to. Set by the caller; the roster is the only door today.</summary>
        public static string ReturnScene = "CharacterRoster";

        private ICardDatabase _db;
        private readonly List<CardDefinition> _pool = new List<CardDefinition>();
        private CardFaceBaker _faceBaker;

        private VisualElement _root;
        private VisualElement _grid;
        private Label _countLabel;
        private Label _emptyLabel;
        private CardWall _wall;
        private CardQueryBar _query;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[Collection] No rootVisualElement — is the UXML assigned?"); return; }

            try
            {
                _db = CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Collection] could not load the card database: {ex.Message}");
                return;
            }

            _grid       = _root.Q<VisualElement>("collection-grid");
            _countLabel = _root.Q<Label>("collection-count");
            _emptyLabel = _root.Q<Label>("collection-empty");
            if (_grid == null)
            {
                Debug.LogError("[Collection] 'collection-grid' not found in the UXML. If the whole " +
                               "screen is empty, check CollectionScreen.uxml for a double dash " +
                               "inside a comment — that fails the import silently.");
                return;
            }

            BuildPool();
            InitFaceBaker();

            _wall = new CardWall(_grid, _faceBaker);
            _wall.SetPool(_pool);

            // No TileClicked handler on purpose: there is no deck to add to, and in-match inspect
            // was cut (board layout rulings, 2026-07-23). A tap does nothing until there is
            // something for it to do.

            _query = new CardQueryBar(_root);
            _query.Changed += ApplyQuery;

            var reset = _root.Q<Button>("query-reset");
            if (reset != null) reset.clicked += () => _query.Reset();

            var back = _root.Q<Button>("back-button");
            if (back != null) back.RegisterCallback<ClickEvent>(_ => Go(ReturnScene));

            ApplyQuery();
        }

        // Every collectible card, every class. The one line that makes this screen what it is.
        private void BuildPool()
        {
            _pool.Clear();
            foreach (var def in _db.AllCollectible())
                _pool.Add(def);
        }

        // The real card, baked to a texture — one card front implementation
        // (BoardController.CreatePresenter), photographed, never a second UI Toolkit
        // re-implementation of the card face drifting from it.
        private void InitFaceBaker()
        {
            var bakerGo = new GameObject("CardFaceBaker");
            bakerGo.transform.SetParent(transform, false);
            _faceBaker = bakerGo.AddComponent<CardFaceBaker>();
            _faceBaker.Initialize(_db);
        }

        private void ApplyQuery()
        {
            _wall.SetQuery(_query.Filter, _query.Sort);

            int shown = _wall.Visible.Count;
            if (_countLabel != null)
                _countLabel.text = shown == _pool.Count
                    ? $"{_pool.Count} cards"
                    : $"{shown} of {_pool.Count} cards";

            // A wall that goes blank with no text reads as a broken screen rather than an empty
            // result, and this one can go blank easily — the facets are ANDed, so "Legendary" plus
            // "Weapon" plus cost 0 is one tap away from matching nothing.
            if (_emptyLabel != null)
                _emptyLabel.EnableInClassList("col-empty--on", shown == 0);
        }

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the
        /// Editor). Mirrors <c>MainMenuController.Go</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[Collection] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }
}
