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
    /// The whole card pool, with no deck panel (Sam, 2026-08-07). Reached from the roster's
    /// View Collection button.
    ///
    /// <para><b>Why this is a separate screen rather than a mode of the deckbuilder.</b> The
    /// deckbuilder's wall is character-scoped by construction — its <c>BuildPool</c> admits only the
    /// open character's pairing plus neutral — so it structurally cannot show you the game. This
    /// screen has no character and no deck, which is the entire difference between them.</para>
    ///
    /// <para><b>It shows what you own when the account knows, and the whole pool when it doesn't.</b>
    /// That instruction — filter on L6, and switch the count label from meaning "the game" to meaning
    /// "yours" — was the retirement trigger this comment carried, and NOR-238 discharged it. What it
    /// could not have anticipated is that <i>both</i> readings stayed live, because ownership has three
    /// states and not two:</para>
    ///
    /// <list type="bullet">
    /// <item>No ownership record (<see cref="PlayerAccount.OwnedCards"/> returns null) — nobody has told
    /// this account what it owns. The wall shows the full collectible pool and the label says "the
    /// game", exactly as it did before L6. This is every machine today.</item>
    /// <item>A record — the wall shows only cards with at least one copy, and the label says "yours".</item>
    /// </list>
    ///
    /// <para><b>So the player-facing copy rule still holds in the first state:</b> do not describe this
    /// screen as a collection of owned cards while it is showing the pool. The label is the only thing
    /// that says which of the two you are looking at, so it is not decoration — read
    /// <see cref="ApplyQuery"/> before changing its wording.</para>
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

        /// <summary>This account's ownership record, or null when it has none. See the class remarks.</summary>
        private IOwnedCardCounts _owned;

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

            LoadOwnership();
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

        /// <summary>
        /// This account's ownership record, or null. Never fatal to this screen: a browse surface that
        /// refuses to open because an ownership file is unreadable helps nobody, and the gates that
        /// matter — <see cref="DeckValidator"/> L6 here, and the server at match-join — read the same
        /// store and still refuse. Logged as an error rather than swallowed, because the visible symptom
        /// of the fallback is a screen that looks completely normal.
        /// </summary>
        private void LoadOwnership()
        {
            try
            {
                _owned = PlayerAccount.Local.OwnedCards();
            }
            catch (Exception ex)
            {
                _owned = null;
                Debug.LogError($"[Collection] could not read this account's card ownership " +
                               $"({ex.GetType().Name}: {ex.Message}); showing the full card pool instead " +
                               "of your collection. The count label says which one you are looking at.");
            }
        }

        // Every card this account owns a copy of — or, when it has no ownership record, every
        // collectible card in the game. The one method that makes this screen what it is, and the
        // reason the class remarks insist the count label is load-bearing: these two pools look
        // identical on screen and mean completely different things.
        //
        // A card recorded at 0 copies is NOT owned. Zero is a real recorded value — every copy dusted,
        // or a refund — and it is exactly the case a truthiness check on "is it in the record" would
        // get wrong, so this asks for the count.
        private void BuildPool()
        {
            _pool.Clear();
            foreach (var def in _db.AllCollectible())
                if (_owned == null || _owned.CopiesOwned(def.Id) > 0)
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

            // The label is the ONLY thing that distinguishes "your collection" from "every card in the
            // game" — the wall is identical either way (see the class remarks). "in your collection"
            // rather than a bare count whenever the account has a record, so nobody has to be told
            // which screen they are on.
            if (_countLabel != null)
            {
                string noun = _owned != null ? "in your collection" : "cards";
                _countLabel.text = shown == _pool.Count
                    ? $"{_pool.Count} {noun}"
                    : $"{shown} of {_pool.Count} {noun}";
            }

            // A wall that goes blank with no text reads as a broken screen rather than an empty
            // result, and this one can go blank easily — the facets are ANDed, so "Legendary" plus
            // "Weapon" plus cost 0 is one tap away from matching nothing.
            //
            // Two ways to be empty now, and blaming the filters for the other one is a lie the player
            // cannot get past: an account that owns nothing has an empty pool with no filter set, and
            // "Nothing matches those filters" would send them tapping Reset forever.
            if (_emptyLabel != null)
            {
                _emptyLabel.text = _pool.Count == 0 && _owned != null
                    ? "You don't own any cards yet."
                    : "Nothing matches those filters.";
                _emptyLabel.EnableInClassList("col-empty--on", shown == 0);
            }
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
