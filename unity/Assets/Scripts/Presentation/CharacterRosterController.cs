using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using Kingdom.Net.Client.Collection;
using Kingdom.Net.Client.Telemetry;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Scene-to-scene handoff for the deck explicitly chosen at the Find Game door. The id is
    /// consumed once by <see cref="NetworkedGameBootstrap"/>; the deck itself is reloaded from disk
    /// there so a stale object can never cross a scene boundary.
    ///
    /// <para>It carried a character id alongside the deck id until ADR-0009. The deck carries its own
    /// pairing now, so the deck id is the whole of the selection — and there is no longer a pair of
    /// ids that can disagree with each other across a scene load.</para>
    /// </summary>
    internal static class MatchDeckSelection
    {
        private static string _deckId = "";

        public static bool Choosing { get; private set; }

        public static void BeginChoosing()
        {
            Choosing = true;
            _deckId = "";
        }

        /// <summary>
        /// Open the deck roster as the deck-selection step. Find Game and the end of a match both
        /// land here, so a player who just finished a game is one pick away from the next.
        /// </summary>
        public static void OpenPicker()
        {
            BeginChoosing();
            CharacterRosterController.ReturnScene = ScreenNav.MainMenu;
            ScreenNav.Go(ScreenNav.CharacterRoster);
        }

        /// <summary>
        /// The result screen's Play Again: queue <paramref name="deckId"/> again and load the board,
        /// skipping the roster. It is the same deck the last match submitted, so it was queue ready a
        /// match ago; <see cref="NetworkedGameBootstrap"/> re-validates it exactly as it does a
        /// roster pick, and a deck that has stopped being playable fails there with the usual copy.
        /// </summary>
        public static void Rematch(string deckId)
        {
            Select(deckId);
            FirstSessionFunnel.Queue(FunnelQueueSource.ResultView);
            ScreenNav.Go(ScreenNav.BoardNetworked);
        }

        public static void Cancel()
        {
            Choosing = false;
            _deckId = "";
        }

        public static void Select(string deckId)
        {
            if (string.IsNullOrEmpty(deckId))
                throw new ArgumentException("A match deck selection needs a deck id.", nameof(deckId));
            _deckId = deckId;
            Choosing = false;
        }

        /// <summary>The chosen deck id, cleared by the read. Empty when nothing was selected.</summary>
        public static string Take()
        {
            string selected = _deckId;
            _deckId = "";
            return selected;
        }
    }

    /// <summary>
    /// The player's decks, and the doorway into the deckbuilder. It reads them fresh from disk and
    /// draws one rectangular plate each, with a trailing "+" that opens the class picker.
    ///
    /// <para><b>It was a roster of CHARACTERS until 2026-09-04 (ADR-0009).</b> Each character was a
    /// row, and its decks a strip inside that row. The container is gone — a deck carries its own
    /// class pairing now — so the screen lost a nesting level: one flat grid of decks, no rows, no
    /// names above them, no per-character "+", no character menu, no delete-character dialog.</para>
    ///
    /// <para><b>Three guards died with the container, and none of them is being ported.</b> The floor
    /// of one deck per character protected the CONTAINER, not the player (Sam: <i>"there is a guard
    /// against deleting a character's last deck? why that makes no sense"</i>) — with decks at the
    /// top level, an account with zero decks is just a new account. The default-deck star answered
    /// "which of this character's decks do I open on", a question a flat list does not ask (#343).
    /// The orphan row drew decks whose character had vanished, a state that is now unreachable
    /// rather than merely unqueried.</para>
    ///
    /// <para><b>What arrived in their place is a CEILING</b>: <see cref="DeckCreation.MaxPerAccount"/>
    /// decks per account, drafts included. There was no cap before, only that floor.</para>
    ///
    /// <para>Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// CharacterRosterScreen.uxml and whose Panel Settings is assigned.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CharacterRosterController : MonoBehaviour
    {
        /// <summary>
        /// Where Back returns for this visit. Collection sets this when the drawer's management
        /// doorway is used; every other entry leaves the Main Menu default. Consumed on enable.
        /// </summary>
        public static string ReturnScene = ScreenNav.MainMenu;

        private VisualElement _root;
        private PlayerAccount _account;
        private ICardDatabase _db;
        private string _returnScene = "MainMenu";

        /// <summary>This account's ownership record (L6), read once per visit — mirrors
        /// CollectionController._owned. Every <see cref="QueueEligibility.Check"/> call on this
        /// screen must pass it: without it L6 never evaluates, so a deck that stopped being
        /// ownable (a card dusted, an account restored) would keep its gold "queue ready" border
        /// here while match-join refuses it — the exact disagreement DeckQuery.ForAccount's own
        /// doc comment says this screen must not produce.</summary>
        private IOwnedCardCounts _owned;
        private IUiSpriteProvider _uiSprites;
        private IArtProvider _art;

        private VisualElement _rosterList;
        private ScrollView _rosterScroll;
        private Label _rosterIntro;
        private Label _emptyState;
        private Label _status;

        private VisualElement _matchBar;
        private Button _matchContinue;

        private VisualElement _deckModal;
        private VisualElement _deckDialog;
        private VisualElement _deckModalClasses;
        private Label _deckModalTitle;
        private Label _deckModalSub;
        private Label _deckModalError;
        private VisualElement _deckMenuPane;
        private VisualElement _deckRenamePane;
        private VisualElement _deckBackPane;
        private VisualElement _deckBackGrid;
        private VisualElement _deckDeletePane;
        private Label _deckDeleteMessage;
        private TextField _deckRenameField;
        private Button _deckSelectButton;
        private Button _deckDeleteButton;
        private Button _deckModalCancel;
        private Button _deckModalConfirm;

        /// <summary>The deck the open dialog is about. Held rather than re-read per pane: the dialog
        /// is modal, so nothing can change it underneath.</summary>
        private DeckList _menuDeck;

        /// <summary>The back highlighted in its picker, committed only by Save.</summary>
        private int _pendingCardBackId;
        private CollectionSnapshot _cardBackOwnership;
        private int _deckRequestVersion;
        private bool _savingDeck;

        private void OnEnable()
        {
            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "MainMenu" : ReturnScene;
            ReturnScene = ScreenNav.MainMenu;
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[DeckRoster] No rootVisualElement — is the UXML assigned?");
                return;
            }
            UiLocalizer.Attach(_root);

            // Top-level destination — same corner settings gear as the main menu (Sam, 2026-09-05).
            MatchSettingsController.Mount();
            FriendsDrawerController.Mount();

            CacheElements();
            WireChrome();
            WireDeckDialog();

            _uiSprites = CardBackCatalog.NewProvider();
#if UNITY_EDITOR
            _art = new EditorArtProvider();
#else
            _art = new AddressableArtProvider();
#endif

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
                _owned = _account.OwnedCards();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeckRoster] could not open this device's collection: {ex.Message}");
                SetStatus(Loc.Get("deck_create_error_collection_unavailable"), isError: true);
                return;
            }

            TryPreselectLastPlayed();
            RebuildRoster();

            // First look (#403), below both early returns for the reason CharacterCreateController
            // gives: a player reading "Collection unavailable" has a problem, not a lesson. Find
            // Game arrives here to choose a deck, which is a different lesson from managing them.
            new FirstRunTidbit(_root).Show(MatchDeckSelection.Choosing
                ? TidbitTrigger.MatchPickArrival
                : TidbitTrigger.RosterArrival);
        }

        private void CacheElements()
        {
            _rosterList = _root.Q<VisualElement>("roster-list");
            _rosterScroll = _root.Q<ScrollView>("roster-scroll");
            // The grid re-solves when its own width changes, which misses the case that matters
            // most for RowsPerScreen: a window that got shorter without getting narrower. The
            // grid's width is identical then, so it never fires, and plates keep a height budget
            // from a viewport that no longer exists.
            _rosterScroll?.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                VisualElement grid = _rosterList?.Q<VisualElement>(className: "deck-grid");
                if (grid != null) QueueGridLayout(grid);
            });
            _rosterIntro = _root.Q<Label>("roster-intro");
            _emptyState = _root.Q<Label>("empty-state");
            _status = _root.Q<Label>("status");

            _matchBar = _root.Q<VisualElement>("match-bar");
            _matchContinue = _root.Q<Button>("match-continue");

            _deckModal = _root.Q<VisualElement>("deck-modal");
            _deckDialog = _root.Q<VisualElement>("deck-dialog");
            _deckModalClasses = _root.Q<VisualElement>("deck-modal-classes");
            _deckModalTitle = _root.Q<Label>("deck-modal-title");
            _deckModalSub = _root.Q<Label>("deck-modal-sub");
            _deckModalError = _root.Q<Label>("deck-modal-error");
            _deckMenuPane = _root.Q<VisualElement>("deck-menu-pane");
            _deckRenamePane = _root.Q<VisualElement>("deck-rename-pane");
            _deckBackPane = _root.Q<VisualElement>("deck-back-pane");
            _deckBackGrid = _root.Q<VisualElement>("deck-back-grid");
            _deckDeletePane = _root.Q<VisualElement>("deck-delete-pane");
            _deckDeleteMessage = _root.Q<Label>("deck-delete-message");
            _deckRenameField = _root.Q<TextField>("deck-rename-field");
            _deckSelectButton = _root.Q<Button>("deck-select");
            _deckDeleteButton = _root.Q<Button>("deck-delete");
            _deckModalCancel = _root.Q<Button>("deck-modal-cancel");
            _deckModalConfirm = _root.Q<Button>("deck-modal-confirm");
        }

        private void WireChrome()
        {
            // The screen's one way out (2026-09-08): "+ New Deck" and View Collection were cut the
            // same day as redundant/off-mission (see CharacterRosterScreen.uxml's header comment),
            // leaving this as the only button `WireChrome` wires at the top level.
            _root.Q<Button>("back-button")?.RegisterCallback<ClickEvent>(_ =>
            {
                // A receiver backing out of the pick declines; a sender had sent nothing yet.
                ChallengeFlow.PickCancelled();
                MatchDeckSelection.Cancel();
                ScreenNav.Go(_returnScene);
            });

            if (MatchDeckSelection.Choosing)
            {
                if (_rosterIntro != null)
                    _rosterIntro.text = ChallengeFlow.Phase switch
                    {
                        ChallengePhase.PickingToSend   => Loc.Format("deck_roster_intro_challenge_send", ChallengeFlow.NameOf(ChallengeFlow.Code)),
                        ChallengePhase.PickingToAccept => Loc.Format("deck_roster_intro_challenge_accept", ChallengeFlow.NameOf(ChallengeFlow.Code)),
                        _                              => Loc.Get("deck_roster_intro_match_pick"),
                    };
                if (_matchContinue != null && ChallengeFlow.IsPicking)
                    _matchContinue.text = ChallengeFlow.Phase == ChallengePhase.PickingToSend
                        ? Loc.Get("deck_roster_send_challenge_button")
                        : Loc.Get("common_accept");
                _root.AddToClassList("character-roster--match-pick");
            }
            _matchContinue?.RegisterCallback<ClickEvent>(_ => ContinueToMatch());
            // Disabled until a plate is picked. Set here as well as in RefreshMatchBar because a
            // collection that fails to open never reaches a rebuild, and a live Continue on a screen
            // with no rows is a button that looks like the way forward and is not.
            _matchContinue?.SetEnabled(false);
            Show(_matchBar, MatchDeckSelection.Choosing);
        }

        // ---- Deck dialog (Sam, 2026-08-07): a plate opens options, not the editor --------------

        /// <summary>Which pane of the deck dialog is showing. Cancel means "back to the menu" from
        /// anywhere but <see cref="Menu"/>, where it means "close" — one button, because a dialog
        /// with both a Back and a Close is a dialog nobody reads.
        ///
        /// <para><c>New</c> was a pane here until ADR-0009. Creating a deck now begins with a class
        /// pairing, which is a full screen's worth of choice, so "+" loads the create SCENE instead
        /// of opening a pane with two buttons in it.</para></summary>
        private enum DeckPane { Menu, Rename, CardBack, Delete }

        private DeckPane _deckPane = DeckPane.Menu;

        private void WireDeckDialog()
        {
            _deckSelectButton?.RegisterCallback<ClickEvent>(_ => SelectMatchPick());
            _root.Q<Button>("deck-edit")?.RegisterCallback<ClickEvent>(_ => OpenDeckForEdit(_menuDeck.Id));
            _root.Q<Button>("deck-rename")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Rename));
            _root.Q<Button>("deck-change-back")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.CardBack));
            _deckDeleteButton?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Delete));
            _deckModalCancel?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_deckPane == DeckPane.Menu) CloseDeckDialog();
                else ShowDeckPane(DeckPane.Menu);
            });
            _deckModalConfirm?.RegisterCallback<ClickEvent>(_ => ConfirmDeckPane());

            // Outside tap closes it (Sam, 2026-09-26), match pick only. `#deck-modal` is BOTH the
            // full-bleed scrim and the flex container centring `#deck-dialog`, so a click on the
            // dialog or any child bubbles here too — the target check is what tells them apart.
            // Only the Menu pane closes: from Rename/CardBack/Delete a stray tap would throw away
            // typed or pending input, so those keep Back as the one way out. Cancel is disabled
            // while a save is in flight (see ConfirmDeckPane); the scrim honours the same rule.
            _deckModal?.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.target != _deckModal) return;
                if (!MatchDeckSelection.Choosing || _savingDeck || _deckPane != DeckPane.Menu) return;
                CloseDeckDialog();
            });
            _deckRenameField?.RegisterValueChangedCallback(_ => SetDeckError(""));
        }

        private void OpenDeckDialog(DeckList deck, DeckSummary summary)
        {
            _menuDeck = deck;
            _pendingCardBackId = deck.CardBackId;
            BuildClassBadges(deck);

            if (_deckModalSub != null)
                _deckModalSub.text = Loc.Format("common_1_d_2_d", deck.TotalCount, DeckValidator.DeckSize);

            // Delete has no floor any more (ADR-0009). The guard it used to carry — "{name} needs at
            // least one deck" — protected a character from being left with nothing to play, and an
            // account with no decks is simply a new account: the create scene is one tap away and the
            // empty state says so.
            _deckDeleteButton?.SetEnabled(true);

            // Select is the match door's one addition to this dialog.
            bool choosing = MatchDeckSelection.Choosing;
            _menuQueueReady = summary.Queue.IsEligible;

            Show(_deckSelectButton, choosing);
            if (choosing)
            {
                _deckSelectButton.tooltip = summary.Queue.IsEligible ? "" : summary.Queue.Reason;
                RefreshDeckSelectButton();
            }

            ShowDeckPane(DeckPane.Menu);
            _deckModal?.RemoveFromClassList("delete-modal--hidden");
        }

        /// <summary>The deck's own pairing, in the dialog header. It came off the CHARACTER until
        /// ADR-0009; it is the deck's now, and permanent, which is why nothing here offers to
        /// change it.</summary>
        private void BuildClassBadges(DeckList deck)
        {
            if (_deckModalClasses == null) return;
            _deckModalClasses.Clear();
            if (deck == null) return;
            AddClassBadge(deck.PrimaryClass);
            AddClassBadge(deck.SecondaryClass);
        }

        private void AddClassBadge(CardClass c)
        {
            var badge = new Label(CardClassTokens.Label(c));
            badge.AddToClassList("deck-modal-class-badge");
            badge.AddToClassList("deck-modal-class-badge--" + CardClassTokens.Slug(c));
            _deckModalClasses.Add(badge);
        }

        private void ShowDeckPane(DeckPane pane)
        {
            if (_menuDeck == null) return;
            _deckPane = pane;
            ++_deckRequestVersion;
            _deckModalConfirm?.SetEnabled(!_savingDeck);
            SetDeckError("");

            // The card-back grid needs more room than the menu's button stack.
            _deckDialog?.EnableInClassList("delete-dialog--menu", pane == DeckPane.Menu);
            _deckDialog?.EnableInClassList("delete-dialog--wide",
                pane == DeckPane.CardBack);
            _deckDialog?.EnableInClassList("delete-dialog--card-back", pane == DeckPane.CardBack);
            _deckDialog?.EnableInClassList("delete-dialog--rename", pane == DeckPane.Rename);

            Show(_deckMenuPane, pane == DeckPane.Menu);
            Show(_deckRenamePane, pane == DeckPane.Rename);
            Show(_deckBackPane, pane == DeckPane.CardBack);
            Show(_deckDeletePane, pane == DeckPane.Delete);

            // Only the sub-line's facts belong to the deck as a whole; the panes retitle above it.
            if (_deckModalTitle != null)
                _deckModalTitle.text = pane switch
                {
                    DeckPane.Rename => Loc.Get("common_rename_deck"),
                    DeckPane.CardBack => Loc.Get("deck_menu_title_card_back"),
                    DeckPane.Delete => Loc.Get("deck_menu_title_delete"),
                    _ => _menuDeck.Name,
                };
            Show(_deckModalSub, pane == DeckPane.Menu);

            bool selfCommitting = pane == DeckPane.Menu;
            if (_deckModalCancel != null) _deckModalCancel.text = selfCommitting ? Loc.Get("common_close") : Loc.Get("common_back");
            Show(_deckModalConfirm, !selfCommitting);
            if (_deckModalConfirm != null)
            {
                _deckModalConfirm.text = pane == DeckPane.Delete ? Loc.Get("common_delete_deck") : Loc.Get("common_save");
                _deckModalConfirm.EnableInClassList("roster-button--danger", pane == DeckPane.Delete);
                _deckModalConfirm.EnableInClassList("emb-primary", pane != DeckPane.Delete);
            }

            if (pane == DeckPane.Rename)
                _deckRenameField?.SetValueWithoutNotify(_menuDeck.Name);
            if (pane == DeckPane.CardBack)
                LoadCardBacksAsync();
            if (pane == DeckPane.Delete && _deckDeleteMessage != null)
                _deckDeleteMessage.text = Loc.Plural("deck_menu_delete_message", _menuDeck.TotalCount,
                    _menuDeck.Name, _menuDeck.TotalCount);
        }

        private async void LoadCardBacksAsync()
        {
            int request = _deckRequestVersion;
            var account = _account;
            _cardBackOwnership = null;
            _deckBackGrid?.Clear();
            _deckModalConfirm?.SetEnabled(false);
            try
            {
                var snapshot = await CollectionGateway.FetchAsync(RosterGateway.AccessToken);
                if (!isActiveAndEnabled || request != _deckRequestVersion ||
                    !ReferenceEquals(account, PlayerAccount.Local)) return;
                _cardBackOwnership = snapshot;
                if (snapshot.CardBacks == null)
                {
                    SetDeckError(Loc.Get("deck_menu_error_card_backs_load"));
                    return;
                }
                if (!snapshot.OwnsCardBack(_pendingCardBackId))
                {
                    _pendingCardBackId = 0;
                    foreach (var back in CardBackCatalog.All)
                        if (snapshot.OwnsCardBack(back.Id)) { _pendingCardBackId = back.Id; break; }
                }
                BuildCardBackGrid();
                if (_pendingCardBackId == 0) SetDeckError(Loc.Get("deck_menu_error_no_card_backs"));
            }
            catch (Exception ex)
            {
                if (request != _deckRequestVersion || !isActiveAndEnabled) return;
                Debug.LogWarning($"[DeckRoster] card backs could not be loaded: {ex.Message}");
                SetDeckError(Loc.Get("deck_menu_error_card_backs_load"));
            }
        }

        private void OnDisable() => ++_deckRequestVersion;

        private void BuildCardBackGrid()
        {
            if (_deckBackGrid == null) return;
            _deckBackGrid.Clear();

            _deckModalConfirm?.SetEnabled(!_savingDeck && _cardBackOwnership != null &&
                _cardBackOwnership.OwnsCardBack(_pendingCardBackId));
            foreach (CardBackDefinition back in CardBackCatalog.All)
            {
                if (_cardBackOwnership == null || !_cardBackOwnership.OwnsCardBack(back.Id)) continue;
                CardBackDefinition choice = back;
                var option = new Button();
                option.AddToClassList("deck-back-option");
                option.EnableInClassList("deck-back-option--on",
                    CardBackCatalog.Resolve(_pendingCardBackId).Id == choice.Id);
                option.tooltip = choice.Name;
                option.clicked += () =>
                {
                    _pendingCardBackId = choice.Id;
                    BuildCardBackGrid();     // selection is the only state, so redrawing is the update
                };

                var art = new VisualElement();
                art.AddToClassList("deck-plate-art");
                Sprite sprite = _uiSprites?.Get(choice.SpriteKey);
                if (sprite != null) art.style.backgroundImage = new StyleBackground(sprite);
                option.Add(art);

                _deckBackGrid.Add(option);
            }
        }

        private async void ConfirmDeckPane()
        {
            if (_menuDeck == null || _savingDeck) return;
            if (_deckPane == DeckPane.Delete) { DeleteOpenDeck(); return; }
            if (_deckPane == DeckPane.CardBack &&
                (_cardBackOwnership == null || !_cardBackOwnership.OwnsCardBack(_pendingCardBackId))) return;

            // Edit a detached candidate. A refused or failed request must leave the local deck intact.
            var candidate = CollectionJson.ReadDecks(CollectionJson.WriteDecks(new[] { _menuDeck }))[0];
            if (_deckPane == DeckPane.Rename)
            {
                string name = (_deckRenameField?.value ?? "").Trim();
                if (name.Length == 0) { SetDeckError(Loc.Get("deck_menu_error_name_required")); return; }
                candidate.Name = name;
            }
            else if (_deckPane == DeckPane.CardBack) candidate.CardBackId = _pendingCardBackId;

            var account = _account;
            int request = _deckRequestVersion;
            _savingDeck = true;
            _deckModalConfirm?.SetEnabled(false);
            _deckModalCancel?.SetEnabled(false);
            try
            {
                var status = await account.ConfirmDeckEditAsync(RosterGateway.AccessToken, candidate);
                if (!isActiveAndEnabled || request != _deckRequestVersion ||
                    !ReferenceEquals(account, PlayerAccount.Local)) return;
                if (status != RosterWriteStatus.Ok)
                {
                    SetDeckError(status == RosterWriteStatus.Rejected
                        ? Loc.Get("deck_menu_error_card_back_unavailable")
                        : Loc.Get("deck_menu_error_save_connection"));
                    return;
                }
                CloseDeckDialog();
                RebuildRoster();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[DeckRoster] could not save deck: {ex.Message}");
                if (request == _deckRequestVersion && isActiveAndEnabled)
                    SetDeckError(Loc.Get("deck_menu_error_save_generic"));
            }
            finally
            {
                _savingDeck = false;
                _deckModalCancel?.SetEnabled(true);
                _deckModalConfirm?.SetEnabled(true);
            }
        }

        /// <summary>
        /// Delete the deck this dialog is about, once the server has. Nothing is protected from this
        /// any more: the last deck goes exactly like the first, leaving the empty state and the "+"
        /// that clears it. A delete the server did not confirm leaves the deck on the device too.
        /// </summary>
        private async void DeleteOpenDeck()
        {
            if (_menuDeck == null || _savingDeck) return;

            var account = _account;
            int request = _deckRequestVersion;
            string goneId = _menuDeck.Id;
            string goneName = _menuDeck.Name;
            _savingDeck = true;
            _deckModalConfirm?.SetEnabled(false);
            _deckModalCancel?.SetEnabled(false);
            try
            {
                var status = await account.ConfirmDeckDeleteAsync(RosterGateway.AccessToken, goneId);
                if (!isActiveAndEnabled || request != _deckRequestVersion ||
                    !ReferenceEquals(account, PlayerAccount.Local)) return;
                if (status != RosterWriteStatus.Ok && status != RosterWriteStatus.NotFound)
                {
                    Debug.LogWarning($"[DeckRoster] server did not delete '{goneName}' ({status}).");
                    SetDeckError(Loc.Get("deck_menu_error_delete_connection"));
                    return;
                }

                // The pick the player was holding may be the deck they just deleted. Clearing it here
                // rather than letting ContinueToMatch discover it keeps the Continue bar honest.
                if (_pickedDeckId == goneId) ClearPick();

                Debug.Log($"[DeckRoster] deleted '{goneName}'.");
                CloseDeckDialog();
                RebuildRoster();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeckRoster] could not delete '{goneName}': {ex.Message}");
                if (request == _deckRequestVersion && isActiveAndEnabled)
                    SetDeckError(Loc.Get("deck_menu_error_delete"));
            }
            finally
            {
                _savingDeck = false;
                _deckModalCancel?.SetEnabled(true);
                _deckModalConfirm?.SetEnabled(true);
            }
        }

        private void CloseDeckDialog()
        {
            _deckModal?.AddToClassList("delete-modal--hidden");
            _menuDeck = null;
            _menuQueueReady = false;
            _pendingCardBackId = 0;
            _cardBackOwnership = null;
            ++_deckRequestVersion;
            _deckPane = DeckPane.Menu;
        }

        private void SetDeckError(string message)
        {
            if (_deckModalError != null) _deckModalError.text = message ?? "";
            Show(_deckModalError, !string.IsNullOrEmpty(message));
        }

        private static void Show(VisualElement element, bool visible)
        {
            if (element != null)
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RebuildRoster()
        {
            if (_account == null || _rosterList == null) return;

            IReadOnlyList<DeckSummary> rows;
            try
            {
                rows = _account.DeckRows(_db);
            }
            catch (Exception ex)
            {
                ReportDeckReadFailure(ex);
                return;
            }

            // Every grid about to be discarded takes its pending layout tick with it. A detached
            // element's scheduler may never run, so without this the set only grows.
            _gridLayoutQueued.Clear();
            _rosterList.Clear();
            if (_emptyState != null)
                _emptyState.style.display = rows.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            _rosterList.Add(BuildDeckGrid(rows));

            RefreshMatchBar();
            SetStatus("");
        }

        /// <summary>
        /// Every deck on the account, one card-shaped plate each, with a trailing "+" that opens the
        /// class picker while the account has room for another.
        ///
        /// <para><b>It WRAPS, and the strip it replaced deliberately did not</b> (Sam, 2026-08-07:
        /// "one line, always"). That ruling was about a single character's decks: a third deck must
        /// not drop below the other two and make one character taller than its share of the roster.
        /// There are no characters to be fair between now — this is the whole account, up to
        /// <see cref="DeckCreation.MaxPerAccount"/> plates, and forcing nine of them onto one line
        /// would shrink every picture past the point of being one.</para>
        /// </summary>
        private VisualElement BuildDeckGrid(IReadOnlyList<DeckSummary> rows)
        {
            var grid = new VisualElement();
            grid.AddToClassList("deck-grid");
            grid.RegisterCallback<GeometryChangedEvent>(_ => QueueGridLayout(grid));

            foreach (DeckSummary summary in rows)
                grid.Add(BuildDeckPlate(summary));

            // Gone at the cap, not disabled (Sam, 2026-10-21): a dead "+" cost a full roster a
            // whole row, and that row is worth more as bigger pictures of the nine decks.
            if (rows.Count < DeckCreation.MaxPerAccount)
                grid.Add(BuildNewDeckPlate());

            return grid;
        }

        private VisualElement BuildDeckPlate(DeckSummary summary)
        {
            DeckList deck = summary.Deck;

            var plate = new VisualElement();
            plate.AddToClassList("deck-plate");
            plate.EnableInClassList("deck-plate--selected",
                MatchDeckSelection.Choosing && _pickedDeckId == deck.Id);

            // The portrait is the deck control; the name stays in its tooltip.
            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            // The two heroes fill the rectangular control. Showcase art remains underneath as a
            // fallback if the portrait sheet is unavailable, with no extra card image above them.
            string primary = CardClassTokens.Label(deck.PrimaryClass);
            string secondary = CardClassTokens.Label(deck.SecondaryClass);
            art.tooltip = summary.Queue.IsEligible
                ? Loc.Format("deck_plate_tooltip_ready", deck.Name, primary, secondary, deck.TotalCount, DeckValidator.DeckSize)
                : Loc.Format("deck_plate_tooltip_not_ready", deck.Name, primary, secondary, summary.Queue.Reason);
            ShowcaseArt(deck, art);
            DeckClassPortrait.Mount(art, _uiSprites,
                deck.PrimaryClass, deck.SecondaryClass,
                "deck-class-portrait--plate");
            plate.Add(art);

            // Queue standing is a small crown seal in the corner: the wax the deck builder stamped
            // when the deck became queue-ready (whimsy pass, Sam 2026-10-17). It replaced the gold
            // border, cut 2026-09-08, which left readiness only in the tooltip.
            art.EnableInClassList("deck-plate-art--ready", summary.Queue.IsEligible);
            if (summary.Queue.IsEligible)
                SealStamp.Place(art, _uiSprites?.Get(SealStamp.SpriteKey),
                    size: 40f, right: 3f, top: 3f, tiltDegrees: -10f);

            BindDeckTaps(plate, deck, summary);
            return plate;
        }

        /// <summary>How long a single tap waits to see whether it is really the first half of a
        /// double tap. Long enough for a deliberate double, short enough that opening the menu does
        /// not feel like it stalled.</summary>
        private const long DoubleTapWindowMs = 220;

        /// <summary>
        /// Single tap opens the deck's functions, double tap opens the deckbuilder (Sam,
        /// 2026-08-07). The plate carries no button of its own — it is the picture, and the picture
        /// is the control.
        ///
        /// <para><b>The single tap is deferred, not immediate.</b> UI Toolkit delivers a double
        /// click as two ClickEvents, the first with clickCount 1 — so acting on it at once means
        /// every double tap flashes the functions dialog open and then loads a scene out from under
        /// it. Waiting one double-tap window and cancelling is what makes the two gestures
        /// distinct rather than nested.</para>
        /// </summary>
        private void BindDeckTaps(VisualElement plate, DeckList deck, DeckSummary summary)
        {
            IVisualElementScheduledItem pendingSingle = null;

            plate.RegisterCallback<ClickEvent>(evt =>
            {
                if (evt.clickCount >= 2)
                {
                    pendingSingle?.Pause();
                    pendingSingle = null;
                    OpenDeckForEdit(deck.Id);
                    return;
                }

                pendingSingle?.Pause();
                pendingSingle = plate.schedule
                    .Execute(() => OpenDeckDialog(deck, summary))
                    .StartingIn(DoubleTapWindowMs);
            });
        }

        // ---- Plate sizing (Sam, 2026-08-07) ------------------------------------------------
        //
        // Wide rectangles frame just the ordered hero pair (Sam, 2026-09-11). Keep the portrait
        // band's former height-to-width ratio, and size both axes in the grid's layout pass.
        private const float PlateArtHeightRatio = 0.42f;

        /// <summary>An absolute floor, and nothing else (Sam, 2026-08-07).
        /// <para>THIS IS STILL 64 AND HAS NOT BEEN RE-MEASURED SINCE THE PANEL STOPPED BEING 411
        /// UNITS TALL (#349, 2026-09-01). It was a floor in dp and is roughly 24dp of real glass
        /// now. Measure before treating it as a considered number.</para></summary>
        private const float MinPlate = 64f;

        /// <summary>The widest plate whose three rows still end above the bottom controls (Back,
        /// Play and the gear reach 92 up the 1080 panel): three rows of 740 end at about 974 of 988.
        /// Raised from 560 when a full roster went to three columns (Sam, 2026-10-21); 560 would
        /// have capped those plates at the size four columns already gave them.</summary>
        private const float MaxPlate = 740f;

        /// <summary>
        /// The size a plate WANTS to be, and the only input to how many fit across. The grid takes
        /// as many columns as it can at this size and then divides the real width between them, so
        /// this sets the density rather than the size: raising it means fewer, larger plates.
        ///
        /// <para>It exists because the old strip had no such number — it fitted exactly the decks
        /// one character had, however many that was. A flat account grid has no natural column
        /// count to inherit, so one has to be chosen.</para>
        /// </summary>
        private const float TargetPlate = 180f;

        /// <summary>The most columns the grid will use however wide the window gets. Nine plates in
        /// one line is the shape the wrap exists to avoid; four keeps any roster to three rows on any
        /// panel, which is a grid the eye takes in at once.</summary>
        private const int MaxColumns = 4;

        /// <summary>
        /// How many plate rows a screenful should show. The real ceiling on plate size: a plate is
        /// as large as it can be while this many rows still fit.
        ///
        /// <para><b>3, not 2</b> (2026-09-08, Sam) — the most tiles there can be is ten (eight decks
        /// and the "+") and four columns hold that in three rows. At 2 the height solve sized plates
        /// as if only two rows needed to fit, so the third row ran off the bottom of the viewport
        /// into a scroll no plate count actually needed.</para>
        /// </summary>
        private const float RowsPerScreen = 3f;

        /// <summary>Everything a plate row costs beyond the art itself: the plate's padding and
        /// border, its bottom margin, and the grid's own vertical padding. Must be kept in step
        /// with character-roster.uss.
        ///
        /// <para>Smaller than the 70f the character rows needed, and the difference is exactly what
        /// the container cost: a name header (~27) and its padding (8) are gone. What is left is
        /// plate padding 8 + border 12 + grid padding 4/8 = 32, and 8 to spare: the plate's bottom
        /// margin this once also charged is gone from the sheet.</para>
        ///
        /// <para>NONE OF THAT IS AN INPUT. <c>viewport</c> is read off the ScrollView's
        /// resolvedStyle, so this arithmetic is here to be checked against, not to be kept true by
        /// editing it — only the terms of the SUM have to move when the sheet does.</para></summary>
        private const float RowChrome = 40f;

        /// <summary>Must match .deck-plate's margin (--space-2), charged once per column.</summary>
        private const float PlateGutter = 8f;

        /// <summary>.deck-plate padding (--space-1) plus its 6px border, both sides — the difference
        /// between the plate's outer width and the art inside it. The same 20px is a term in
        /// <see cref="RowChrome"/>, because the plate pays it on both axes.</summary>
        private const float PlateChrome = 20f;

        /// <summary>A row solved to land exactly on the available width is a floating-point tie;
        /// see CardWall.Layout for the wrap-one-early failure this avoids.</summary>
        private const float RowSafetyMargin = 2f;

        private readonly HashSet<VisualElement> _gridLayoutQueued = new HashSet<VisualElement>();

        /// <summary>
        /// Defer to the next scheduler tick rather than resizing inline. The trigger is the grid's
        /// own GeometryChangedEvent, which Yoga fires mid-layout-pass — writing widths from inside
        /// it edits the tree while Yoga is still walking it, so only the children it has not
        /// visited pick up the new values, and the mutation re-fires the event. Same hazard, same
        /// fix, as CardWall's own deferred layout.
        /// </summary>
        private void QueueGridLayout(VisualElement grid)
        {
            if (!_gridLayoutQueued.Add(grid)) return;
            grid.schedule.Execute(() =>
            {
                _gridLayoutQueued.Remove(grid);
                LayoutDeckGrid(grid);
            });
        }

        private void LayoutDeckGrid(VisualElement grid)
        {
            // contentRect, not resolvedStyle.width: the plates wrap inside the grid's 8+8 padding.
            // Solving against the padded width sized one plate too many per row, so every row
            // wrapped a column early and a full roster's "+" sat alone on a fourth row.
            float available = grid.contentRect.width - RowSafetyMargin;
            // `!(x > 0f)`, not `x <= 0f`: an unresolved resolvedStyle is NaN and every comparison
            // against NaN is false, so `<= 0f` passes it through to the solve. That distinction cost
            // a 1.3 GB OOM kill on the create screen's since-deleted portrait solver; this guard is
            // the survivor of it.
            if (!(available > 0f)) return;

            int plates = 0;
            foreach (VisualElement child in grid.Children())
                if (child.ClassListContains("deck-plate")) plates++;
            if (plates == 0) return;

            // As few rows as the column cap allows, then the plates spread evenly over them: nine
            // decks are three rows of three rather than 4-4-1, and the freed column goes into the
            // size of every plate (Sam, 2026-10-21). This also never gives more columns than
            // plates: two decks on a wide panel are two large plates, not two small ones beside
            // a four-column grid's worth of empty.
            int maxColumns = Mathf.Clamp(Mathf.FloorToInt(available / (TargetPlate + PlateGutter)),
                                         1, MaxColumns);
            int rowCount = Mathf.CeilToInt(plates / (float)maxColumns);
            int columns = Mathf.CeilToInt(plates / (float)rowCount);
            float fromWidth = (available - columns * PlateGutter) / columns;

            // Height still has the final say when there is width to spare. Without it a wide window
            // makes a lone deck so tall that one plate fills the screen.
            float fromHeight = MaxPlate;
            float viewport = _rosterScroll?.resolvedStyle.height ?? 0f;
            if (viewport > 0f)
            {
                // The ScrollView content otherwise collapses to the plate rows, leaving no height
                // for align-content to distribute. Match its visible height as the window resizes.
                grid.style.minHeight = viewport;
                fromHeight = (viewport / RowsPerScreen - RowChrome) / PlateArtHeightRatio + PlateChrome;
            }

            // Clamped at the bottom only. Clamping up to a comfortable minimum is what would push a
            // plate outside its column.
            float width = Mathf.Max(MinPlate, Mathf.Min(fromWidth, fromHeight, MaxPlate));

            foreach (VisualElement child in grid.Children())
            {
                if (!child.ClassListContains("deck-plate")) continue;
                child.style.width = width;

                VisualElement art = child.Q<VisualElement>(className: "deck-plate-art");
                if (art == null) continue;
                art.style.width = width - PlateChrome;
                art.style.height = (width - PlateChrome) * PlateArtHeightRatio;
            }
        }

        /// <summary>
        /// Paint a deck's chosen card art onto <paramref name="target"/>, falling back in order.
        ///
        /// <para><b>Every deck has a picture from the moment it exists.</b> An unset deck shows its
        /// most expensive card — a passable stand-in for "what this deck is about", and always a
        /// card the player actually owns in it. A deck with no cards at all, or whose art has not
        /// been generated, shows the card back. Nothing renders as a blank rectangle, because the
        /// image is the only thing distinguishing one plate from another.</para>
        ///
        /// <para>The chosen id is NOT repaired when it resolves to nothing (see
        /// <c>DeckList.ShowcaseCardId</c>): a card removed and later put back restores the picture
        /// the player picked, which silently rewriting the id would destroy.</para>
        /// </summary>
        private void ShowcaseArt(DeckList deck, VisualElement target)
        {
            void Paint(Sprite sprite)
            {
                if (sprite != null) target.style.backgroundImage = new StyleBackground(sprite);
            }

            Sprite fallback = CardBackCatalog.SpriteFor(deck.CardBackId, _uiSprites);
            Paint(fallback);

            CardDefinition showcase = ShowcaseOf(deck);
            if (showcase == null || string.IsNullOrEmpty(showcase.ArtId)) return;

            // LoadArt may complete synchronously, so the fallback is painted first and this
            // overwrites it rather than the other way round.
            _art.LoadArt(showcase.ArtId, Paint);
        }

        /// <summary>The card fronting a deck: the player's pick when it still resolves, otherwise
        /// the priciest card in it — ties broken by name so the same deck always shows the same
        /// card rather than reshuffling with the entry order.</summary>
        private CardDefinition ShowcaseOf(DeckList deck)
        {
            if (deck.ShowcaseCardId > 0
                && deck.CountOf(deck.ShowcaseCardId) > 0
                && _db.TryGet(deck.ShowcaseCardId, out CardDefinition chosen))
                return chosen;

            CardDefinition best = null;
            foreach (DeckEntry entry in deck.Entries)
            {
                if (!_db.TryGet(entry.CardId, out CardDefinition def)) continue;
                if (best == null
                    || def.Cost > best.Cost
                    || (def.Cost == best.Cost && string.CompareOrdinal(def.Name, best.Name) < 0))
                    best = def;
            }
            return best;
        }

        /// <summary>The trailing "+". <see cref="BuildDeckGrid"/> leaves it out at the cap.</summary>
        private VisualElement BuildNewDeckPlate()
        {
            var plate = new Button();
            plate.AddToClassList("deck-plate");
            plate.AddToClassList("deck-plate--new");
            plate.tooltip = Loc.Get("deck_roster_new_deck_tooltip");
            plate.clicked += NewDeck;

            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            var plus = new Label("+");
            plus.AddToClassList("deck-plate-plus");
            art.Add(plus);
            plate.Add(art);

            return plate;
        }

        /// <summary>
        /// Open the class picker. A deck's pairing is permanent (ADR-0009 ruling 1), so creating one
        /// begins with choosing it — there is no shape of this that fits in a dialog pane, which is
        /// why "+" loads a scene rather than opening one.
        /// </summary>
        private void NewDeck()
        {
            try
            {
                if (_account == null) return;
                if (!_account.CanCreateDeck())
                {
                    SetStatus(Loc.Format("deck_roster_limit_reached", DeckCreation.MaxPerAccount),
                              isError: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                ReportDeckReadFailure(ex);
                return;
            }

            CharacterCreateController.ReturnScene = ScreenNav.CharacterRoster;
            ScreenNav.Go(ScreenNav.CharacterCreate);
        }

        private void ReportDeckReadFailure(Exception ex)
        {
            Debug.LogError($"[DeckRoster] deck read failed: {ex.Message}");
            _rosterList?.Clear();
            if (_emptyState != null) _emptyState.style.display = DisplayStyle.None;
            SetStatus(Loc.Get("deck_roster_error_load"), isError: true);
        }

        // Both doors lead to one controller (#152) in one scene (#154): handing it a deck is what
        // puts it in deckbuild mode, and View Collection above hands it none. Both call
        // ScreenNav.Go(ScreenNav.Collection); the only difference between the two doors is the pending deck.
        private void OpenDeckForEdit(string deckId)
        {
            CollectionController.ReturnScene = ScreenNav.CharacterRoster;
            CollectionController.PendingDeckId = deckId;
            ScreenNav.Go(ScreenNav.Collection);
        }

        // ---- Match pick: select a deck, then continue (Sam, 2026-08-07) --------------------

        /// <summary>
        /// The plate the player has chosen to play, or empty. Held on the controller rather than
        /// pushed into <see cref="MatchDeckSelection"/> as it is clicked: that static is the
        /// scene-to-scene handoff <c>NetworkedGameBootstrap</c> consumes, and writing it before
        /// Continue would mean backing out to the main menu left a match choice armed.
        /// </summary>
        private string _pickedDeckId = "";

        /// <summary>Whether the deck the dialog is about can be queued with — read once when it
        /// opens, so Select and its refusal cannot disagree.</summary>
        private bool _menuQueueReady;

        /// <summary>
        /// Make the open deck the match pick. Continue enters the queue; this control only changes
        /// which plate is selected. Pressing it on the deck already picked keeps the pick and just
        /// closes the dialog — it does not deselect (Sam, 2026-10-29); picking another deck is the
        /// only way to move it.
        /// </summary>
        private void SelectMatchPick()
        {
            if (_menuDeck == null) return;
            if (_pickedDeckId != _menuDeck.Id)
            {
                if (!_menuQueueReady)
                {
                    SetDeckError(Loc.Get("deck_menu_error_not_queue_ready"));
                    return;
                }
                _pickedDeckId = _menuDeck.Id;
            }

            CloseDeckDialog();
            RebuildRoster();
        }

        private void RefreshDeckSelectButton()
        {
            if (_deckSelectButton == null || _menuDeck == null) return;
            bool selected = _pickedDeckId == _menuDeck.Id;
            _deckSelectButton.text = selected ? Loc.Get("deck_menu_selected_button") : Loc.Get("common_select_this_deck");
            _deckSelectButton.EnableInClassList("deck-action--selected", selected);
            _deckSelectButton.SetEnabled(selected || _menuQueueReady);
        }

        /// <summary>
        /// Enter matchmaking with the selected deck, re-checking eligibility against disk on the way
        /// out. The check is not paranoia: the selection survives a trip to the deckbuilder and back,
        /// so the deck that was queue ready when it was picked may not be by the time Continue is
        /// pressed.
        /// </summary>
        private void ContinueToMatch()
        {
            if (string.IsNullOrEmpty(_pickedDeckId)) return;

            DeckList deck = null;
            foreach (DeckList d in _account.Decks())
                if (d.Id == _pickedDeckId) { deck = d; break; }

            // RebuildRoster ends by clearing the status line, so the rebuild has to happen BEFORE
            // the message that explains it — the other way round the player sees a cleared pick and
            // no reason for it.
            if (deck == null)
            {
                ClearPick();
                RebuildRoster();
                SetStatus(Loc.Get("deck_roster_error_deck_gone"), isError: true);
                return;
            }

            QueueCheck queue = QueueEligibility.Check(deck, _db, _owned);
            if (!queue.IsEligible)
            {
                ClearPick();
                RebuildRoster();
                SetStatus(Loc.Format("deck_roster_error_not_queue_ready", deck.Name, queue.Reason), isError: true);
                return;
            }

            // A friend challenge (#409) does not go to the board yet: this player's half goes out,
            // and ChallengeFlow loads the board when the server's ticket arrives.
            if (ChallengeFlow.IsPicking)
            {
                ChallengeFlow.DeckChosen(deck.Id);
                _matchContinue?.SetEnabled(false);
                return;
            }

            MatchDeckSelection.Select(deck.Id);
            // The funnel's queue intent. Every way here starts at the menu's Find Game or the result
            // screen's tap-through; the result screen's Play Again passes its own source (Rematch).
            FirstSessionFunnel.Queue(FunnelQueueSource.Menu);
            ScreenNav.Go(ScreenNav.BoardNetworked);
        }

        private void ClearPick() => _pickedDeckId = "";

        /// <summary>
        /// Pre-arm the match pick with whatever deck was last actually submitted into a match (Sam,
        /// 2026-08-09), so Find Game can go straight to Continue instead of forcing a fresh Select
        /// every time. Re-validated against disk exactly like a manual pick, because the deck may
        /// have been deleted since, or may have fallen out of queue eligibility (a card dusted, a
        /// rule tightened) — <see cref="PlayerProfile.LastPlayedDeckId"/> carries no eligibility
        /// guarantee of its own. Finding nothing pickable leaves the screen exactly as it was before
        /// this existed: empty, waiting on a manual Select.
        /// </summary>
        private void TryPreselectLastPlayed()
        {
            if (!MatchDeckSelection.Choosing) return;

            string deckId = _account.LastPlayedDeckId;
            if (string.IsNullOrEmpty(deckId)) return;

            DeckList deck = null;
            foreach (DeckList d in _account.Decks())
                if (d.Id == deckId) { deck = d; break; }
            if (deck == null) return;

            if (!QueueEligibility.Check(deck, _db, _owned).IsEligible) return;

            _pickedDeckId = deck.Id;
        }

        /// <summary>
        /// Show the Continue bar while choosing, and let it through only once something is picked.
        ///
        /// <para>It used to name the selection in a label beside the button — "Kael · Aggro", or
        /// "Pick a queue-ready deck." before one was chosen. Cut (Sam, 2026-08-07): the selected
        /// plate already carries a ring, so the label restated in words what the player had just
        /// clicked, and the empty state was an instruction nobody needed. The two store lookups
        /// that existed only to feed it went with it.</para>
        /// </summary>
        private void RefreshMatchBar()
        {
            if (_matchBar == null) return;

            bool choosing = MatchDeckSelection.Choosing;
            Show(_matchBar, choosing);
            if (choosing) _matchContinue?.SetEnabled(!string.IsNullOrEmpty(_pickedDeckId));
        }

        private void SetStatus(string message, bool isError = false)
        {
            if (_status == null) return;
            string text = message ?? "";
            _status.text = text;
            // An empty status band is `display: none` and costs the plate solver nothing. See the
            // note on .roster-status: this label sits beside the ScrollView, so any height it holds
            // comes out of the viewport LayoutDeckGrid divides between rows.
            _status.EnableInClassList("roster-status--on", text.Length > 0);
            _status.EnableInClassList("roster-status--error", isError);
        }

    }
}
