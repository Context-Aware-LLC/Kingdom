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
        private Label _deckModalAccount;
        private VisualElement _deckModalClasses;
        private Label _deckModalTitle;
        private Label _deckModalSub;
        private Label _deckModalError;
        private VisualElement _deckMenuPane;
        private VisualElement _deckRenamePane;
        private VisualElement _deckBackPane;
        private VisualElement _deckBackGrid;
        private Label _deckBackNote;
        private VisualElement _deckImagePane;
        private VisualElement _deckImageGrid;
        private Label _deckImageNote;
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

        /// <summary>The back and the showcase card highlighted in their pickers, committed only by Save.</summary>
        private string _pendingCardBackId = "";
        private int _pendingShowcaseCardId;

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

            // Top-level destination — same corner settings gear as the main menu (Sam, 2026-09-05).
            MatchSettingsController.Mount();

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
                SetStatus("Collection unavailable — see the console.", isError: true);
                return;
            }

            TryPreselectLastPlayed();
            RebuildRoster();
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
            _deckModalAccount = _root.Q<Label>("deck-modal-account");
            _deckModalClasses = _root.Q<VisualElement>("deck-modal-classes");
            _deckModalTitle = _root.Q<Label>("deck-modal-title");
            _deckModalSub = _root.Q<Label>("deck-modal-sub");
            _deckModalError = _root.Q<Label>("deck-modal-error");
            _deckMenuPane = _root.Q<VisualElement>("deck-menu-pane");
            _deckRenamePane = _root.Q<VisualElement>("deck-rename-pane");
            _deckBackPane = _root.Q<VisualElement>("deck-back-pane");
            _deckBackGrid = _root.Q<VisualElement>("deck-back-grid");
            _deckBackNote = _root.Q<Label>("deck-back-note");
            _deckImagePane = _root.Q<VisualElement>("deck-image-pane");
            _deckImageGrid = _root.Q<VisualElement>("deck-image-grid");
            _deckImageNote = _root.Q<Label>("deck-image-note");
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
                MatchDeckSelection.Cancel();
                ScreenNav.Go(_returnScene);
            });

            if (MatchDeckSelection.Choosing)
            {
                if (_rosterIntro != null)
                    _rosterIntro.text = "Choose one of your queue-ready decks, then Continue.";
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
        private enum DeckPane { Menu, Rename, Image, CardBack, Delete }

        private DeckPane _deckPane = DeckPane.Menu;

        private void WireDeckDialog()
        {
            _deckSelectButton?.RegisterCallback<ClickEvent>(_ => PickForMatch());
            _root.Q<Button>("deck-edit")?.RegisterCallback<ClickEvent>(_ => OpenDeckForEdit(_menuDeck.Id));
            _root.Q<Button>("deck-image")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Image));
            _root.Q<Button>("deck-rename")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Rename));
            _root.Q<Button>("deck-change-back")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.CardBack));
            _deckDeleteButton?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Delete));
            _deckModalCancel?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_deckPane == DeckPane.Menu) CloseDeckDialog();
                else ShowDeckPane(DeckPane.Menu);
            });
            _deckModalConfirm?.RegisterCallback<ClickEvent>(_ => ConfirmDeckPane());
            _deckRenameField?.RegisterValueChangedCallback(_ => SetDeckError(""));
        }

        private void OpenDeckDialog(DeckList deck, DeckSummary summary)
        {
            _menuDeck = deck;
            _pendingCardBackId = deck.CardBackId;
            _pendingShowcaseCardId = deck.ShowcaseCardId;

            if (_deckModalAccount != null)
                _deckModalAccount.text = _account.DisplayNameOr("");
            BuildClassBadges(deck);

            if (_deckModalSub != null)
            {
                string standing = summary.Queue.IsEligible ? "Queue ready" : $"Draft · {summary.Queue.Reason}";
                _deckModalSub.text = $"{deck.TotalCount} / {DeckValidator.DeckSize} · {standing}";
            }

            // Delete has no floor any more (ADR-0009). The guard it used to carry — "{name} needs at
            // least one deck" — protected a character from being left with nothing to play, and an
            // account with no decks is simply a new account: the create scene is one tap away and the
            // empty state says so.
            _deckDeleteButton?.SetEnabled(true);

            // Select is the match door's one addition to this dialog.
            bool choosing = MatchDeckSelection.Choosing;
            bool alreadyPicked = choosing && _pickedDeckId == deck.Id;
            _menuQueueReady = summary.Queue.IsEligible;

            Show(_deckSelectButton, choosing);
            if (choosing && _deckSelectButton != null)
            {
                _deckSelectButton.SetEnabled(summary.Queue.IsEligible && !alreadyPicked);
                _deckSelectButton.text = alreadyPicked ? "Selected" : "Select this deck";
                _deckSelectButton.tooltip = summary.Queue.IsEligible ? "" : summary.Queue.Reason;
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
            SetDeckError("");

            // A card grid reads better with room a button stack would only stretch to fill —
            // see .delete-dialog--wide (Sam, 2026-08-09).
            _deckDialog?.EnableInClassList("delete-dialog--wide",
                pane == DeckPane.Image || pane == DeckPane.CardBack);

            Show(_deckMenuPane, pane == DeckPane.Menu);
            Show(_deckImagePane, pane == DeckPane.Image);
            Show(_deckRenamePane, pane == DeckPane.Rename);
            Show(_deckBackPane, pane == DeckPane.CardBack);
            Show(_deckDeletePane, pane == DeckPane.Delete);

            // Only the sub-line's facts belong to the deck as a whole; the panes retitle above it.
            if (_deckModalTitle != null)
                _deckModalTitle.text = pane switch
                {
                    DeckPane.Rename => "Rename deck",
                    DeckPane.Image => "Deck image",
                    DeckPane.CardBack => "Card back",
                    DeckPane.Delete => "Delete deck?",
                    _ => _menuDeck.Name,
                };
            Show(_deckModalSub, pane == DeckPane.Menu);

            bool selfCommitting = pane == DeckPane.Menu;
            if (_deckModalCancel != null) _deckModalCancel.text = selfCommitting ? "Close" : "Back";
            Show(_deckModalConfirm, !selfCommitting);
            if (_deckModalConfirm != null)
            {
                _deckModalConfirm.text = pane == DeckPane.Delete ? "Delete deck" : "Save";
                _deckModalConfirm.EnableInClassList("roster-button--danger", pane == DeckPane.Delete);
                _deckModalConfirm.EnableInClassList("roster-button--cta", pane != DeckPane.Delete);
            }

            if (pane == DeckPane.Rename)
                _deckRenameField?.SetValueWithoutNotify(_menuDeck.Name);
            if (pane == DeckPane.Image)
                BuildShowcaseGrid();
            if (pane == DeckPane.CardBack)
                BuildCardBackGrid();
            if (pane == DeckPane.Delete && _deckDeleteMessage != null)
                _deckDeleteMessage.text =
                    $"'{_menuDeck.Name}' and its {_menuDeck.TotalCount} cards are gone for good. " +
                    "The cards themselves stay in your collection.";
        }

        /// <summary>
        /// Every distinct card in the deck, as pickable art. Empty for an empty deck, which is the
        /// honest answer: a deck with no cards has nothing of its own to be recognised by.
        /// </summary>
        private void BuildShowcaseGrid()
        {
            if (_deckImageGrid == null) return;
            _deckImageGrid.Clear();

            CardDefinition current =
                _pendingShowcaseCardId > 0 && _db.TryGet(_pendingShowcaseCardId, out CardDefinition picked)
                    ? picked
                    : ShowcaseOf(_menuDeck);
            foreach (DeckEntry entry in _menuDeck.Entries)
            {
                if (!_db.TryGet(entry.CardId, out CardDefinition def)) continue;
                CardDefinition choice = def;

                var option = new Button();
                option.AddToClassList("deck-back-option");
                option.EnableInClassList("deck-back-option--on", current != null && current.Id == choice.Id);
                option.tooltip = choice.Name;
                option.clicked += () =>
                {
                    _pendingShowcaseCardId = choice.Id;
                    BuildShowcaseGrid();     // selection is the only state, so redrawing is the update
                };

                var art = new VisualElement();
                art.AddToClassList("deck-plate-art");
                _art.LoadArt(choice.ArtId, sprite =>
                {
                    if (sprite != null) art.style.backgroundImage = new StyleBackground(sprite);
                });
                option.Add(art);
                _deckImageGrid.Add(option);
            }

            if (_deckImageNote != null)
                _deckImageNote.text = _menuDeck.Entries.Count == 0
                    ? "Put some cards in this deck first — its picture comes from one of them."
                    : "";
        }

        private void BuildCardBackGrid()
        {
            if (_deckBackGrid == null) return;
            _deckBackGrid.Clear();

            foreach (CardBackOption back in CardBackCatalog.All)
            {
                CardBackOption choice = back;
                var option = new Button();
                option.AddToClassList("deck-back-option");
                option.EnableInClassList("deck-back-option--on",
                    CardBackCatalog.Resolve(_pendingCardBackId).Id == choice.Id);
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

                var name = new Label(choice.Name);
                name.AddToClassList("deck-plate-name");
                option.Add(name);

                _deckBackGrid.Add(option);
            }

            if (_deckBackNote != null)
                _deckBackNote.text = CardBackCatalog.HasChoice
                    ? ""
                    : "This is the only card back in the game so far. More arrive with each set.";
        }

        private void ConfirmDeckPane()
        {
            if (_menuDeck == null) return;

            if (_deckPane == DeckPane.Delete) { DeleteOpenDeck(); return; }

            if (_deckPane == DeckPane.Rename)
            {
                string name = (_deckRenameField?.value ?? "").Trim();
                if (name.Length == 0)
                {
                    SetDeckError("A deck needs a name.");
                    return;
                }
                _menuDeck.Name = name;
            }
            else if (_deckPane == DeckPane.Image)
            {
                _menuDeck.ShowcaseCardId = _pendingShowcaseCardId;
            }
            else if (_deckPane == DeckPane.CardBack)
            {
                _menuDeck.CardBackId = _pendingCardBackId;
            }

            try
            {
                _account.Save(_menuDeck);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeckRoster] could not save '{_menuDeck.Name}': {ex.Message}");
                SetDeckError("The deck could not be saved — see the console.");
                return;
            }

            CloseDeckDialog();
            RebuildRoster();
        }

        /// <summary>
        /// Delete the deck this dialog is about. Nothing is protected from this any more: the last
        /// deck goes exactly like the first, leaving the empty state and the "+" that clears it.
        /// </summary>
        private void DeleteOpenDeck()
        {
            if (_menuDeck == null) return;

            string goneName = _menuDeck.Name;
            try
            {
                _account.DeleteDeck(_menuDeck.Id);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeckRoster] could not delete '{goneName}': {ex.Message}");
                SetDeckError("The deck could not be deleted — see the console.");
                return;
            }

            // The pick the player was holding may be the deck they just deleted. Clearing it here
            // rather than letting ContinueToMatch discover it keeps the Continue bar honest.
            if (_pickedDeckId == _menuDeck.Id) ClearPick();

            Debug.Log($"[DeckRoster] deleted '{goneName}'.");
            CloseDeckDialog();
            RebuildRoster();
        }

        private void CloseDeckDialog()
        {
            _deckModal?.AddToClassList("delete-modal--hidden");
            _menuDeck = null;
            _menuQueueReady = false;
            _pendingCardBackId = "";
            _pendingShowcaseCardId = 0;
            _deckPane = DeckPane.Menu;
        }

        private void SetDeckError(string message)
        {
            if (_deckModalError != null) _deckModalError.text = message ?? "";
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
        /// class picker.
        ///
        /// <para><b>It WRAPS, and the strip it replaced deliberately did not</b> (Sam, 2026-08-07:
        /// "one line, always"). That ruling was about a single character's decks: a third deck must
        /// not drop below the other two and make one character taller than its share of the roster.
        /// There are no characters to be fair between now — this is the whole account, up to
        /// <see cref="DeckCreation.MaxPerAccount"/> plates plus the "+", and forcing nine of them
        /// onto one line would shrink every picture past the point of being one.</para>
        /// </summary>
        private VisualElement BuildDeckGrid(IReadOnlyList<DeckSummary> rows)
        {
            var grid = new VisualElement();
            grid.AddToClassList("deck-grid");
            grid.RegisterCallback<GeometryChangedEvent>(_ => QueueGridLayout(grid));

            foreach (DeckSummary summary in rows)
                grid.Add(BuildDeckPlate(summary));

            grid.Add(BuildNewDeckPlate(rows.Count));

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
            string pairing = $"{CardClassTokens.Label(deck.PrimaryClass)} → {CardClassTokens.Label(deck.SecondaryClass)}";
            art.tooltip = summary.Queue.IsEligible
                ? $"{deck.Name} · {pairing} — {deck.TotalCount} / {DeckValidator.DeckSize}"
                : $"{deck.Name} · {pairing} — {summary.Queue.Reason}";
            ShowcaseArt(deck, art);
            DeckClassPortrait.Mount(art, _uiSprites,
                deck.PrimaryClass, deck.SecondaryClass,
                "deck-class-portrait--plate");
            plate.Add(art);

            // Queue standing is the GOLD BORDER and nothing else (Sam, 2026-08-07). It replaced a
            // "17 / 30" caption, which replaced a Ready/Draft chip — each pass trading words for
            // something the eye reads without stopping. A deck with a gold edge is ready to play.
            art.EnableInClassList("deck-plate-art--ready", summary.Queue.IsEligible);

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

        /// <summary>An absolute guard, not the working ceiling — <see cref="RowsPerScreen"/> and
        /// <see cref="TargetPlate"/> are what actually stop a plate growing. This only catches a
        /// viewport tall enough to make a single deck absurd.</summary>
        private const float MaxPlate = 560f;

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
        /// one line is the shape the wrap exists to avoid; four keeps the full roster to three rows
        /// on any panel, which is a grid the eye takes in at once.</summary>
        private const int MaxColumns = 4;

        /// <summary>
        /// How many plate rows a screenful should show. The real ceiling on plate size: a plate is
        /// as large as it can be while this many rows still fit.
        ///
        /// <para><b>3, not 2</b> (2026-09-08, Sam) — matching <see cref="MaxColumns"/>'s own doc
        /// comment, which already says four columns keeps a FULL roster (<see
        /// cref="DeckCreation.MaxPerAccount"/> plus the "+", 9 tiles) to three rows. At 2 the height
        /// solve sized plates as if only two rows needed to fit, so the third row of a full roster
        /// ran off the bottom of the viewport into a scroll no plate count actually needed.</para>
        /// </summary>
        private const float RowsPerScreen = 3f;

        /// <summary>Everything a plate row costs beyond the art itself: the plate's padding and
        /// border, its bottom margin, and the grid's own vertical padding. Must be kept in step
        /// with character-roster.uss.
        ///
        /// <para>Smaller than the 70f the character rows needed, and the difference is exactly what
        /// the container cost: a name header (~27) and its padding (8) are gone. What is left is
        /// plate padding 8 + border 4 + row margin 8 + grid padding 4/8 = 32.</para>
        ///
        /// <para>NONE OF THAT IS AN INPUT. <c>viewport</c> is read off the ScrollView's
        /// resolvedStyle, so this arithmetic is here to be checked against, not to be kept true by
        /// editing it — only the terms of the SUM have to move when the sheet does.</para></summary>
        private const float RowChrome = 32f;

        /// <summary>Must match .deck-plate's margin (--space-2), charged once per column.</summary>
        private const float PlateGutter = 8f;

        /// <summary>.deck-plate padding (--space-1) plus its 2px border, both sides — the difference
        /// between the plate's outer width and the art inside it. The same 12dp is a term in
        /// <see cref="RowChrome"/>, because the plate pays it on both axes.</summary>
        private const float PlateChrome = 12f;

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
            float available = grid.resolvedStyle.width - RowSafetyMargin;
            // `!(x > 0f)`, not `x <= 0f`: an unresolved resolvedStyle is NaN and every comparison
            // against NaN is false, so `<= 0f` passes it through to the solve. That distinction cost
            // a 1.3 GB OOM kill on the create screen's since-deleted portrait solver; this guard is
            // the survivor of it.
            if (!(available > 0f)) return;

            int plates = 0;
            foreach (VisualElement child in grid.Children())
                if (child.ClassListContains("deck-plate")) plates++;
            if (plates == 0) return;

            // Never more columns than there are plates: two decks on a wide panel should be two
            // large plates, not two small ones with a four-column grid's worth of empty beside them.
            int columns = Mathf.Clamp(Mathf.FloorToInt(available / (TargetPlate + PlateGutter)),
                                      1, Mathf.Min(MaxColumns, plates));
            float fromWidth = (available - columns * PlateGutter) / columns;

            // Height still has the final say when there is width to spare. Without it a wide window
            // makes a lone deck so tall that one plate fills the screen.
            float fromHeight = MaxPlate;
            float viewport = _rosterScroll?.resolvedStyle.height ?? 0f;
            if (viewport > 0f)
                fromHeight = (viewport / RowsPerScreen - RowChrome) / PlateArtHeightRatio + PlateChrome;

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

        /// <summary>
        /// The trailing "+". Disabled rather than hidden at the cap: a plate that vanishes at eight
        /// decks reads as a bug, and the tooltip is the only place the limit can be stated at the
        /// moment it applies.
        /// </summary>
        private VisualElement BuildNewDeckPlate(int deckCount)
        {
            bool room = deckCount < DeckCreation.MaxPerAccount;

            var plate = new Button();
            plate.AddToClassList("deck-plate");
            plate.AddToClassList("deck-plate--new");
            plate.SetEnabled(room);
            plate.tooltip = room
                ? "Start a new deck"
                : $"{DeckCreation.MaxPerAccount} decks is the limit. Delete one to make room.";
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
                    SetStatus($"{DeckCreation.MaxPerAccount} decks is the limit. Delete one to make room.",
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
            SetStatus("Your saved decks could not be loaded.", isError: true);
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
        /// Mark the open deck as the one to play. It does NOT enter the queue — that is Continue's
        /// job. A button labelled Select, pressed from inside an options dialog, should not be the
        /// thing that drops the player into matchmaking.
        /// </summary>
        private void PickForMatch()
        {
            if (_menuDeck == null) return;
            if (!_menuQueueReady)
            {
                SetDeckError("This deck is not queue ready yet.");
                return;
            }

            _pickedDeckId = _menuDeck.Id;
            CloseDeckDialog();
            RebuildRoster();
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
                SetStatus("That deck is no longer there. Pick another.", isError: true);
                return;
            }

            QueueCheck queue = QueueEligibility.Check(deck, _db, _owned);
            if (!queue.IsEligible)
            {
                ClearPick();
                RebuildRoster();
                SetStatus($"{deck.Name} is not queue ready: {queue.Reason}", isError: true);
                return;
            }

            MatchDeckSelection.Select(deck.Id);
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
