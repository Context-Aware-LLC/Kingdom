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
    /// Scene-to-scene handoff for the character explicitly chosen at the Find Game door. The id is
    /// consumed once by <see cref="NetworkedGameBootstrap"/>; the deck itself is reloaded from disk
    /// there so a stale object can never cross a scene boundary.
    /// </summary>
    internal static class MatchDeckSelection
    {
        private static string _characterId = "";
        private static string _deckId = "";

        public static bool Choosing { get; private set; }

        public static void BeginChoosing()
        {
            Choosing = true;
            _characterId = "";
            _deckId = "";
        }

        public static void Cancel()
        {
            Choosing = false;
            _characterId = "";
            _deckId = "";
        }

        public static void Select(string characterId, string deckId)
        {
            if (string.IsNullOrEmpty(characterId))
                throw new ArgumentException("A match deck selection needs a character id.", nameof(characterId));
            if (string.IsNullOrEmpty(deckId))
                throw new ArgumentException("A match deck selection needs a deck id.", nameof(deckId));
            _characterId = characterId;
            _deckId = deckId;
            Choosing = false;
        }

        public static MatchDeckChoice Take()
        {
            var selected = new MatchDeckChoice(_characterId, _deckId);
            _characterId = "";
            _deckId = "";
            return selected;
        }
    }

    internal readonly struct MatchDeckChoice
    {
        public readonly string CharacterId;
        public readonly string DeckId;

        public MatchDeckChoice(string characterId, string deckId)
        {
            CharacterId = characterId;
            DeckId = deckId;
        }
    }

    /// <summary>
    /// The character doorway into the deckbuilder. It reads the local roster fresh from disk, shows
    /// each ordered class pairing with its default deck and queue standing, and is the only runtime
    /// surface that creates or deletes characters.
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
        public static string ReturnScene = "MainMenu";

        private VisualElement _root;
        private PlayerAccount _account;
        private ICardDatabase _db;
        private string _returnScene = "MainMenu";

        /// <summary>This account's ownership record (L6), read once per visit — mirrors
        /// CollectionController._owned. Every <see cref="QueueEligibility.Check"/> call on this
        /// screen must pass it: without it L6 never evaluates, so a deck that stopped being
        /// ownable (a card dusted, an account restored) would keep its gold "queue ready" border
        /// here while match-join refuses it — the exact disagreement DeckQuery.ForCharacter's own
        /// doc comment says this screen must not produce.</summary>
        private IOwnedCardCounts _owned;
        private IUiSpriteProvider _uiSprites;
        private IArtProvider _art;

        private VisualElement _rosterList;
        private ScrollView _rosterScroll;
        private Label _rosterIntro;
        private Label _emptyState;
        private Label _status;

        // "New Character" no longer opens an overlay here: creation is its own SCENE
        // (Sam, 2026-08-23). See CharacterCreateController.
        private Button _newCharacterButton;

        private VisualElement _deleteModal;
        private Label _deleteMessage;
        private Label _deleteError;
        private Character _pendingDelete;
        private int _pendingDeleteDeckCount;

        private VisualElement _characterMenu;
        private VisualElement _characterMenuCard;
        private Label _characterMenuTitle;
        private Character _menuRowCharacter;

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
        private VisualElement _deckNewPane;
        private Label _deckNewNote;
        private Button _deckNewGenerated;
        private VisualElement _deckDeletePane;
        private Label _deckDeleteMessage;
        private TextField _deckRenameField;
        private Button _deckSelectButton;
        private Button _deckSetDefaultButton;
        private Button _deckDeleteButton;
        private Button _deckModalCancel;
        private Button _deckModalConfirm;

        /// <summary>The deck the open dialog is about, and the character it belongs to. Held rather
        /// than re-read per pane: the dialog is modal, so nothing can change them underneath it.</summary>
        private Character _menuCharacter;
        private DeckList _menuDeck;

        /// <summary>How many decks that character has — what decides whether Delete is offered.</summary>
        private int _menuSiblingCount;

        /// <summary>
        /// True while the open dialog is about an <b>orphan</b> — a deck whose bound Character is
        /// gone, so <see cref="_menuCharacter"/> is null and every character-shaped affordance in
        /// this dialog is meaningless. The one action it offers is Delete
        /// (collection-deckbuilder.md §5 O-4), so the dialog opens straight on that pane.
        /// </summary>
        private bool _menuOrphan;

        /// <summary>The back and the showcase card highlighted in their pickers, committed only by Save.</summary>
        private string _pendingCardBackId = "";
        private int _pendingShowcaseCardId;

        private void OnEnable()
        {
            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "MainMenu" : ReturnScene;
            ReturnScene = "MainMenu";
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[CharacterRoster] No rootVisualElement — is the UXML assigned?");
                return;
            }

            CacheElements();
            WireChrome();
            WireDeleteDialog();
            WireCharacterMenu();
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
                Debug.LogError($"[CharacterRoster] could not open this device's collection: {ex.Message}");
                SetStatus("Collection unavailable — see the console.", isError: true);
                _newCharacterButton?.SetEnabled(false);
                return;
            }

            TryPreselectLastPlayed();
            RebuildRoster();
        }

        private void CacheElements()
        {
            _rosterList = _root.Q<VisualElement>("roster-list");
            _rosterScroll = _root.Q<ScrollView>("roster-scroll");
            // A strip re-solves itself when its own width changes, which misses the case that
            // matters most for RowsPerScreen: a window that got shorter without getting narrower.
            // The strips' widths are identical then, so none of them fire, and plates keep a height
            // budget from a viewport that no longer exists.
            _rosterScroll?.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                foreach (VisualElement row in _rosterList.Children())
                {
                    VisualElement strip = row.Q<VisualElement>(className: "deck-strip");
                    if (strip != null) QueueStripLayout(strip);
                }
            });
            _rosterIntro = _root.Q<Label>("roster-intro");
            _emptyState = _root.Q<Label>("empty-state");
            _status = _root.Q<Label>("status");

            _newCharacterButton = _root.Q<Button>("new-character");
            _deleteModal = _root.Q<VisualElement>("delete-modal");
            _deleteMessage = _root.Q<Label>("delete-message");
            _deleteError = _root.Q<Label>("delete-error");

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
            _deckNewPane = _root.Q<VisualElement>("deck-new-pane");
            _deckNewNote = _root.Q<Label>("deck-new-note");
            _deckNewGenerated = _root.Q<Button>("deck-new-generated");
            _deckDeletePane = _root.Q<VisualElement>("deck-delete-pane");
            _deckDeleteMessage = _root.Q<Label>("deck-delete-message");
            _deckRenameField = _root.Q<TextField>("deck-rename-field");
            _deckSelectButton = _root.Q<Button>("deck-select");
            _deckSetDefaultButton = _root.Q<Button>("deck-set-default");
            _deckDeleteButton = _root.Q<Button>("deck-delete");
            _deckModalCancel = _root.Q<Button>("deck-modal-cancel");
            _deckModalConfirm = _root.Q<Button>("deck-modal-confirm");
        }

        private void WireChrome()
        {
            _root.Q<Button>("back-button")?.RegisterCallback<ClickEvent>(_ =>
            {
                MatchDeckSelection.Cancel();
                Go(_returnScene);
            });

            // Creation is its own scene (Sam, 2026-08-23) — the same call the Store got, for the
            // same reason: full-screen, opaque, a destination. It returns HERE rather than to
            // _returnScene, because the character it just made is a roster row and the player
            // needs to see it land; MatchDeckSelection is deliberately left alone so a queue pick
            // survives the detour, exactly as it does for the collection above.
            _newCharacterButton?.RegisterCallback<ClickEvent>(_ =>
            {
                CharacterCreateController.ReturnScene = "CharacterRoster";
                Go("CharacterCreate");
            });

            // The whole card pool, no deck panel (Sam, 2026-08-07). Deliberately does NOT cancel
            // MatchDeckSelection: browsing the collection mid-queue-pick is a detour, not an
            // abandonment, and Back returns here with the pick still live.
            _root.Q<Button>("view-collection")?.RegisterCallback<ClickEvent>(_ =>
            {
                CollectionController.ReturnScene = _returnScene == "Collection"
                    ? "MainMenu"
                    : "CharacterRoster";
                Go("Collection");
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

        private void WireDeleteDialog()
        {
            _root.Q<Button>("cancel-delete")?.RegisterCallback<ClickEvent>(_ => CloseDeleteDialog());
            _root.Q<Button>("confirm-delete")?.RegisterCallback<ClickEvent>(_ => ConfirmDelete());
        }

        // ---- Character menu (Sam, 2026-08-07) ---------------------------------------------------
        //
        // Clicking a character's name opens a small card under it. Today it holds one action, and
        // that is the point: Delete used to be a standing button on every row, one stray tap from
        // the decks beside it. Behind a deliberate click it costs no width and no accidents.

        private void WireCharacterMenu()
        {
            _characterMenu = _root.Q<VisualElement>("character-menu");
            _characterMenuCard = _root.Q<VisualElement>("character-menu-card");
            _characterMenuTitle = _root.Q<Label>("character-menu-title");

            _root.Q<VisualElement>("character-menu-scrim")?
                .RegisterCallback<ClickEvent>(_ => CloseCharacterMenu());

            _root.Q<Button>("character-menu-delete")?.RegisterCallback<ClickEvent>(_ =>
            {
                Character subject = _menuRowCharacter;
                CloseCharacterMenu();
                if (subject != null) OpenDeleteDialog(subject);
            });
        }

        /// <summary>
        /// Show the menu under <paramref name="anchor"/>, clamped to stay inside the screen.
        ///
        /// <para>Coordinates are panel-space (<c>worldBound</c>) converted into the overlay's own
        /// box, because the overlay is inset to <c>#root</c> and not to the panel — reading
        /// <c>worldBound</c> and writing it straight to <c>left</c>/<c>top</c> offsets the card by
        /// the root's padding, which looks like a small alignment bug and is not one.</para>
        ///
        /// <para>The vertical clamp waits a tick: the card's height depends on its content and is
        /// not resolved until it has been laid out visible. The horizontal one does not, because
        /// <c>.character-menu-card</c> fixes the width.</para>
        /// </summary>
        private void OpenCharacterMenu(Character character, VisualElement anchor)
        {
            if (_characterMenu == null || _characterMenuCard == null || character == null) return;

            _menuRowCharacter = character;
            if (_characterMenuTitle != null) _characterMenuTitle.text = character.Name;

            _characterMenu.RemoveFromClassList("character-menu--hidden");

            Rect overlay = _characterMenu.worldBound;
            Rect target = anchor.worldBound;
            float left = target.x - overlay.x;
            float top = target.yMax - overlay.y + MenuAnchorGap;

            if (overlay.width > 0f)
                left = Mathf.Max(0f, Mathf.Min(left, overlay.width - _characterMenuCard.resolvedStyle.width));

            _characterMenuCard.style.left = left;
            _characterMenuCard.style.top = top;

            // Flip above the name rather than run off the bottom of the list.
            _characterMenuCard.schedule.Execute(() =>
            {
                float height = _characterMenuCard.resolvedStyle.height;
                if (height <= 0f || overlay.height <= 0f) return;
                if (top + height <= overlay.height) return;
                _characterMenuCard.style.top = Mathf.Max(0f, target.y - overlay.y - height - MenuAnchorGap);
            });
        }

        /// <summary>The gap between the name and the card under it — enough to read as attached to
        /// the name rather than growing out of it.</summary>
        private const float MenuAnchorGap = 6f;

        private void CloseCharacterMenu()
        {
            _characterMenu?.AddToClassList("character-menu--hidden");
            _menuRowCharacter = null;
        }

        // ---- Deck dialog (Sam, 2026-08-07): a plate opens options, not the editor --------------

        /// <summary>Which pane of the deck dialog is showing. Cancel means "back to the menu" from
        /// anywhere but <see cref="Menu"/>, where it means "close" — one button, because a dialog
        /// with both a Back and a Close is a dialog nobody reads.</summary>
        private enum DeckPane { Menu, New, Rename, Image, CardBack, Delete }

        private DeckPane _deckPane = DeckPane.Menu;

        private void WireDeckDialog()
        {
            _root.Q<Button>("deck-new-generated")?.RegisterCallback<ClickEvent>(_ => NewDeck(StarterDeck.Generated));
            _root.Q<Button>("deck-new-blank")?.RegisterCallback<ClickEvent>(_ => NewDeck(StarterDeck.Blank));
            _deckSelectButton?.RegisterCallback<ClickEvent>(_ => PickForMatch());
            _root.Q<Button>("deck-edit")?.RegisterCallback<ClickEvent>(_ => OpenDeckForEdit(_menuCharacter.Id, _menuDeck.Id));
            _root.Q<Button>("deck-image")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Image));
            _root.Q<Button>("deck-rename")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Rename));
            _root.Q<Button>("deck-change-back")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.CardBack));
            _deckSetDefaultButton?.RegisterCallback<ClickEvent>(_ => SetDefaultDeck());
            _deckDeleteButton?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Delete));
            _deckModalCancel?.RegisterCallback<ClickEvent>(_ =>
            {
                // An orphan's dialog has no menu to go back to — its only pane IS Delete — so Cancel
                // there means close, the same as it does on the two self-committing panes.
                if (_menuOrphan || _deckPane == DeckPane.Menu || _deckPane == DeckPane.New) CloseDeckDialog();
                else ShowDeckPane(DeckPane.Menu);
            });
            _deckModalConfirm?.RegisterCallback<ClickEvent>(_ => ConfirmDeckPane());
            _deckRenameField?.RegisterValueChangedCallback(_ => SetDeckError(""));
        }

        private void OpenDeckDialog(Character character, DeckList deck, DeckSummary summary, int siblingCount)
        {
            _menuCharacter = character;
            _menuDeck = deck;
            _menuSiblingCount = siblingCount;
            _pendingCardBackId = deck.CardBackId;
            _pendingShowcaseCardId = deck.ShowcaseCardId;

            if (_deckModalAccount != null)
                _deckModalAccount.text = _account.DisplayNameOr("");
            BuildClassBadges(character);

            if (_deckModalSub != null)
            {
                string standing = summary.Queue.IsEligible ? "Queue ready" : $"Draft · {summary.Queue.Reason}";
                string mark = summary.IsDefault ? $"{character.Name}'s default · " : "";
                _deckModalSub.text = $"{mark}{deck.TotalCount} / {DeckValidator.DeckSize} · {standing}";
            }

            // Set-as-default is meaningless on the deck that already is one. Delete has a floor of
            // one deck per character — a character with none has nothing for the queue to reach for
            // and nothing to open — so the last one refuses rather than confirming, and says why
            // here instead of in a console warning the player will never see.
            bool alreadyDefault = summary.IsDefault || siblingCount == 1;
            _deckSetDefaultButton?.SetEnabled(!alreadyDefault);
            if (_deckSetDefaultButton != null)
                _deckSetDefaultButton.tooltip = alreadyDefault
                    ? $"Already {character.Name}'s default deck"
                    : $"The deck {character.Name} takes into a match";

            bool canDelete = siblingCount > 1;
            _deckDeleteButton?.SetEnabled(canDelete);
            if (_deckDeleteButton != null)
                _deckDeleteButton.tooltip = canDelete
                    ? ""
                    : $"{character.Name}'s only deck. Make another first.";

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

        /// <summary>
        /// What an orphaned deck's plate opens: the reason it cannot be played, and Delete.
        ///
        /// <para><b>Delete and nothing else</b> (collection-deckbuilder.md §5 O-4, RESOLVED Sam
        /// 2026-07-26). §2.11 gives every unselectable chooser row an <c>Edit</c> that opens the
        /// deckbuilder, but the deckbuilder reaches a decklist only <i>through</i> a Character
        /// (§2.6a) and an orphan has none — so an Edit here would be a button that can only fail.
        /// Rebinding the deck to some other Character is deliberately not offered either: it is the
        /// silent repair the rest of §3 refuses, and preserving a deck past its Character is
        /// export's job. That is why this opens on the Delete pane rather than on a menu whose
        /// every other entry would be dead.</para>
        ///
        /// <para>The reason shown is <see cref="QueueCheck.Reason"/> — the same L5 string the queue
        /// gate produces, never restated here.</para>
        /// </summary>
        private void OpenOrphanDialog(DeckList deck, QueueCheck queue)
        {
            _menuOrphan = true;
            _menuCharacter = null;
            _menuDeck = deck;
            _menuSiblingCount = 0;
            _menuQueueReady = false;

            if (_deckModalAccount != null)
                _deckModalAccount.text = _account.DisplayNameOr("");
            BuildClassBadges(null);

            Show(_deckSelectButton, false);
            ShowDeckPane(DeckPane.Delete);
            if (_deckModalSub != null) _deckModalSub.text = queue.Reason;
            _deckModal?.RemoveFromClassList("delete-modal--hidden");
        }

        private void BuildClassBadges(Character character)
        {
            if (_deckModalClasses == null) return;
            _deckModalClasses.Clear();
            if (character == null) return;
            AddClassBadge(character.PrimaryClass);
            AddClassBadge(character.SecondaryClass);
        }

        private void AddClassBadge(CardClass c)
        {
            var badge = new Label(Pretty(c));
            badge.AddToClassList("deck-modal-class-badge");
            badge.AddToClassList("deck-modal-class-badge--" + ClassSlug(c));
            _deckModalClasses.Add(badge);
        }

        private void ShowDeckPane(DeckPane pane)
        {
            // New is the one pane with no deck behind it yet, so the guard is on the panes that
            // read _menuDeck rather than on entry.
            if (_menuDeck == null && pane != DeckPane.New) return;
            _deckPane = pane;
            SetDeckError("");

            // A card grid reads better with room a button stack would only stretch to fill —
            // see .delete-dialog--wide (Sam, 2026-08-09).
            _deckDialog?.EnableInClassList("delete-dialog--wide",
                pane == DeckPane.Image || pane == DeckPane.CardBack);

            Show(_deckNewPane, pane == DeckPane.New);
            Show(_deckMenuPane, pane == DeckPane.Menu);
            Show(_deckImagePane, pane == DeckPane.Image);
            Show(_deckRenamePane, pane == DeckPane.Rename);
            Show(_deckBackPane, pane == DeckPane.CardBack);
            Show(_deckDeletePane, pane == DeckPane.Delete);

            // Only the sub-line's facts belong to the deck as a whole; the panes retitle above it.
            if (_deckModalTitle != null)
                _deckModalTitle.text = pane switch
                {
                    DeckPane.New => $"New deck for {_menuCharacter?.Name}",
                    DeckPane.Rename => "Rename deck",
                    DeckPane.Image => "Deck image",
                    DeckPane.CardBack => "Card back",
                    // An orphan opens on this pane, so the deck's own name has to be the title —
                    // there is no menu pane before it to have said which deck this is about.
                    DeckPane.Delete => _menuOrphan ? _menuDeck.Name : "Delete deck?",
                    _ => _menuDeck.Name,
                };
            Show(_deckModalSub, pane == DeckPane.Menu || _menuOrphan);

            bool selfCommitting = pane == DeckPane.Menu || pane == DeckPane.New;
            if (_deckModalCancel != null) _deckModalCancel.text = selfCommitting ? "Close" : "Back";
            Show(_deckModalConfirm, !selfCommitting);
            if (_deckModalConfirm != null)
            {
                _deckModalConfirm.text = pane == DeckPane.Delete ? "Delete deck" : "Save";
                _deckModalConfirm.EnableInClassList("roster-button--danger", pane == DeckPane.Delete);
                _deckModalConfirm.EnableInClassList("roster-button--cta", pane != DeckPane.Delete);
            }

            if (pane == DeckPane.New)
                RefreshNewDeckPane();
            if (pane == DeckPane.Rename)
                _deckRenameField?.SetValueWithoutNotify(_menuDeck.Name);
            if (pane == DeckPane.Image)
                BuildShowcaseGrid();
            if (pane == DeckPane.CardBack)
                BuildCardBackGrid();
            if (pane == DeckPane.Delete && _deckDeleteMessage != null)
                _deckDeleteMessage.text = _menuOrphan
                    ? "The character this deck was built for is gone, so it can no longer be " +
                      $"played or edited. Deleting it clears it from this list; its {_menuDeck.TotalCount} " +
                      "cards stay in your collection."
                    : $"'{_menuDeck.Name}' and its {_menuDeck.TotalCount} cards are gone for good. " +
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
                Debug.LogError($"[CharacterRoster] could not save '{_menuDeck.Name}': {ex.Message}");
                SetDeckError("The deck could not be saved — see the console.");
                return;
            }

            CloseDeckDialog();
            RebuildRoster();
        }

        /// <summary>Make this deck the character's default — what the roster marks and what the queue
        /// reaches for. Writes the deck before the pointer, so the id can never name something that
        /// is not on disk yet.</summary>
        private void SetDefaultDeck()
        {
            if (_menuCharacter == null || _menuDeck == null) return;
            if (_menuCharacter.DefaultDeckId == _menuDeck.Id) return;

            try
            {
                _account.Save(_menuDeck);
                _menuCharacter.DefaultDeckId = _menuDeck.Id;
                _account.Save(_menuCharacter);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] could not set '{_menuDeck.Name}' as default: {ex.Message}");
                SetDeckError("The default could not be saved — see the console.");
                return;
            }

            Debug.Log($"[CharacterRoster] '{_menuDeck.Name}' is now {_menuCharacter.Name}'s default deck.");
            CloseDeckDialog();
            RebuildRoster();
        }

        /// <summary>
        /// Delete the deck this dialog is about.
        ///
        /// <para><b>A dangling default is left dangling.</b> Deleting the deck a character defaults
        /// to does NOT clear <see cref="Character.DefaultDeckId"/> — that is the documented contract
        /// on the field itself ("a dangling id is surfaced, never repaired"), because silently
        /// re-pointing it means the character opens on a deck the player never chose. The strip says
        /// so instead, on the next rebuild.</para>
        /// </summary>
        private void DeleteOpenDeck()
        {
            if (_menuDeck == null) return;

            // An orphan is exempt from both guards below, and for the same reason: they exist to
            // keep a Character from being left with nothing to play, and an orphan has no Character
            // to protect. Deleting the last one strands nobody — it is the whole point of the row.
            if (_menuOrphan) { DeleteOrphan(); return; }

            if (_menuCharacter == null) return;
            if (_menuSiblingCount <= 1)
            {
                // Unreachable through the disabled button; if the count ever disagrees with the
                // store, refuse rather than leave a character with nothing to play.
                SetDeckError($"{_menuCharacter.Name} needs at least one deck.");
                return;
            }

            string goneName = _menuDeck.Name;
            try
            {
                _account.DeleteDeck(_menuDeck.Id);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] could not delete '{goneName}': {ex.Message}");
                SetDeckError("The deck could not be deleted — see the console.");
                return;
            }

            Debug.Log($"[CharacterRoster] deleted '{goneName}' from {_menuCharacter.Name}.");
            CloseDeckDialog();
            RebuildRoster();
        }

        /// <summary>
        /// Delete an orphan. The deck store is the only thing touched: there is no character record
        /// to keep in step, and nothing points at this deck any more — that is what made it an
        /// orphan.
        /// </summary>
        private void DeleteOrphan()
        {
            string goneName = _menuDeck.Name;
            try
            {
                _account.DeleteDeck(_menuDeck.Id);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] could not delete orphaned deck '{goneName}': {ex.Message}");
                SetDeckError("The deck could not be deleted — see the console.");
                return;
            }

            Debug.Log($"[CharacterRoster] deleted orphaned deck '{goneName}'.");
            CloseDeckDialog();
            RebuildRoster();
        }

        private void CloseDeckDialog()
        {
            _deckModal?.AddToClassList("delete-modal--hidden");
            _menuCharacter = null;
            _menuDeck = null;
            _menuOrphan = false;
            _menuSiblingCount = 0;
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

            IReadOnlyList<Character> roster;
            IReadOnlyList<DeckList> decks;
            try
            {
                roster = _account.Roster();
                decks = _account.Decks();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] roster read failed: {ex.Message}");
                SetStatus("Roster unavailable — see the console.", isError: true);
                return;
            }

            // The menu is anchored to a Label that is about to be destroyed, and holds a Character
            // the rebuild may have just deleted. Close it before either becomes true.
            CloseCharacterMenu();

            // Every strip about to be discarded takes its pending layout tick with it. A detached
            // element's scheduler may never run, so without this the set only grows.
            _stripLayoutQueued.Clear();
            _rosterList.Clear();
            if (_emptyState != null)
                _emptyState.style.display = roster.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (Character character in roster)
                _rosterList.Add(BuildCharacterRow(character, decks));

            // Last, and only when there are any. An orphan belongs to no character, so it can sit in
            // no character's row (DeckQuery.Orphans) — this screen is the one surface that shows it
            // at all, and leaving it out is what made a crash-stranded deck invisible rather than
            // gone.
            IReadOnlyList<DeckList> orphans = DeckQuery.Orphans(decks, roster);
            if (orphans.Count > 0)
                _rosterList.Add(BuildOrphanRow(orphans));

            RefreshMatchBar();
            SetStatus("");
        }

        /// <summary>
        /// One row for both doors (Sam, 2026-08-07). Find Game used to render its own text-button
        /// variant of this screen. It renders exactly this now — same rows, same plates, same
        /// options behind a plate, same Delete. Everything the match door adds sits outside the
        /// row: one extra action in the deck dialog, and a Continue bar under the list.
        /// </summary>
        private VisualElement BuildCharacterRow(Character character, IReadOnlyList<DeckList> decks)
        {
            var row = new VisualElement();
            row.AddToClassList("character-row");
            row.AddToClassList("character-row--" + ClassSlug(character.PrimaryClass));

            // The name sits ABOVE the decks, not beside them (Sam, 2026-08-07). It used to be a
            // fixed-width column on the left, which cost the strip a fifth of the row for a single
            // word and was enough to push a third deck onto a second line. A header band costs one
            // line of height once and hands the strip the row's whole width.
            //
            // It is still the character's name and nothing else. The pairing, the hero power and the
            // queue pill all went the same way, for the same reason: the class is already in the
            // row's coloured spine, and the cards in each deck are the class made visible.
            //
            // The name IS the control now — it opens the character's menu, and the row's standing
            // Delete button is gone with it (Sam, 2026-08-07). This reverses the older note that a
            // character has no destination a click could go to: the click opens a menu about the
            // character, not a screen for it, which is the thing that did not exist before.
            var header = new VisualElement();
            header.AddToClassList("character-row-header");

            var identity = new VisualElement();
            identity.AddToClassList("character-identity");

            var name = new Label(character.Name);
            name.AddToClassList("character-name");
            name.tooltip = $"{Pretty(character.PrimaryClass)} → {Pretty(character.SecondaryClass)}";
            name.RegisterCallback<ClickEvent>(_ => OpenCharacterMenu(character, name));
            identity.Add(name);

            header.Add(identity);

            row.Add(header);
            row.Add(BuildDeckStrip(character, decks));
            return row;
        }

        /// <summary>
        /// The decks no character claims, in one row under the roster.
        ///
        /// <para><b>Why they are here at all.</b> Nothing produces an orphan on purpose — deleting a
        /// character destroys its decks — so what reaches this row is crash residue: a death between
        /// the two writes character creation makes, or a damaged decks.json. Left out of the roster
        /// it is invisible state that costs the player storage and can never be cleared; drawn here
        /// it is one row that explains itself and offers the way out.</para>
        ///
        /// <para>The row deliberately does <b>not</b> look like a character. There is no name, no
        /// class spine, no "+" to add another (nothing could own it) and no Delete-character button.
        /// No plate carries the gold queue border, because an orphan fails L5 by construction and
        /// none of them can ever be selected.</para>
        /// </summary>
        private VisualElement BuildOrphanRow(IReadOnlyList<DeckList> orphans)
        {
            var row = new VisualElement();
            row.AddToClassList("character-row");
            row.AddToClassList("character-row--orphan");

            var header = new VisualElement();
            header.AddToClassList("character-row-header");

            var identity = new VisualElement();
            identity.AddToClassList("character-identity");

            var name = new Label(orphans.Count == 1 ? "1 deck with no character"
                                                    : $"{orphans.Count} decks with no character");
            name.AddToClassList("character-name");
            name.AddToClassList("character-name--orphan");
            identity.Add(name);

            var note = new Label("The character each was built for is gone. They cannot be played " +
                                 "or edited — open one to clear it.");
            note.AddToClassList("orphan-note");
            identity.Add(note);

            header.Add(identity);

            var strip = new VisualElement();
            strip.AddToClassList("deck-strip");
            strip.RegisterCallback<GeometryChangedEvent>(_ => QueueStripLayout(strip));
            foreach (DeckList orphan in orphans)
                strip.Add(BuildOrphanPlate(orphan));

            row.Add(header);
            row.Add(strip);
            return row;
        }

        /// <summary>
        /// One orphan's plate. Same picture as any other deck — it is still the player's deck — but
        /// never queue-ready, never a default, and both taps lead to the same place.
        ///
        /// <para><b>Double tap does not open the deckbuilder.</b> Everywhere else it does; here the
        /// editor has no character to open on and would fail. Sending both gestures to the same
        /// dialog is what keeps this row from being a dead end.</para>
        /// </summary>
        private VisualElement BuildOrphanPlate(DeckList deck)
        {
            // The roster form of the gate: the deck's CharacterId resolves to nobody, which IS the
            // L5 violation. Read from the same gate the queue reads, so the sentence on this plate
            // can never drift from the one that refused it.
            QueueCheck queue = QueueEligibility.Check(deck, _account.Roster(), _db, _owned);

            var plate = new VisualElement();
            plate.AddToClassList("deck-plate");
            plate.AddToClassList("deck-plate--orphan");

            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            art.tooltip = $"{deck.Name} — {queue.Reason}";
            ShowcaseArt(deck, art);
            plate.Add(art);

            plate.RegisterCallback<ClickEvent>(_ => OpenOrphanDialog(deck, queue));
            return plate;
        }

        /// <summary>
        /// This character's decks, one card-shaped plate each, with a trailing "+" that makes another.
        ///
        /// <para>This is the deckbuilder's old switcher bar, moved (Sam, 2026-08-07). Picking a deck
        /// is a thing you do <b>before</b> you open an editor, so the editor no longer offers it — the
        /// deckbuilder now opens on exactly one deck and has no way to change which.</para>
        /// </summary>
        private VisualElement BuildDeckStrip(Character character, IReadOnlyList<DeckList> decks)
        {
            var strip = new VisualElement();
            strip.AddToClassList("deck-strip");
            strip.RegisterCallback<GeometryChangedEvent>(_ => QueueStripLayout(strip));

            CharacterDecks owned = HealDefaultDeck(character, DeckQuery.ForCharacter(character, decks, _db, _owned));

            foreach (DeckSummary summary in owned.Decks)
                strip.Add(BuildDeckPlate(character, summary, owned.Count, !owned.DefaultResolved));

            strip.Add(BuildNewDeckPlate(character));

            return strip;
        }

        /// <summary>
        /// Roll a character with no resolvable default onto the deck it is already opening on, and
        /// say nothing about it (Sam, 2026-08-07).
        ///
        /// <para><b>This deliberately overrides <c>Character.DefaultDeckId</c>'s original
        /// "surfaced, never repaired" rule</b>, and that rule's own text has been updated to match —
        /// a contract the code contradicts is worse than either version of it. The rule's reason was
        /// that re-pointing means "a deck the player never chose". Sam's counter, and it is the
        /// better read: the player did not choose the deleted one either, and deleting your default
        /// IS the request to move off it. What they never asked for is an error message above their
        /// decks about a deck that no longer exists.</para>
        ///
        /// <para>Repairs on the read path, which is normally the wrong place for a write. It is here
        /// because the damage predates the fix — collections in the wild already carry dangling
        /// pointers — and a dangle that is neither repaired nor reported is invisible state that
        /// keeps drawing the wrong star. Idempotent: the second visit finds nothing to do.</para>
        /// </summary>
        private CharacterDecks HealDefaultDeck(Character character, CharacterDecks owned)
        {
            if (owned.DefaultResolved || owned.Count == 0) return owned;

            DeckList landing = owned.Opening?.Deck;
            if (landing == null) return owned;

            try
            {
                character.DefaultDeckId = landing.Id;
                _account.Save(character);
            }
            catch (Exception ex)
            {
                // Draw it as the default anyway — the caller falls back on !DefaultResolved — and
                // keep the failure out of the player's way. It costs a star, not a deck.
                Debug.LogWarning($"[CharacterRoster] could not re-point {character.Name}'s default " +
                                 $"deck to '{landing.Name}': {ex.Message}");
                return owned;
            }

            Debug.Log($"[CharacterRoster] {character.Name} had no default deck; rolled onto '{landing.Name}'.");
            return DeckQuery.ForCharacter(character, _account.Decks(), _db, _owned);
        }

        private VisualElement BuildDeckPlate(Character character, DeckSummary summary, int siblingCount,
                                             bool treatAsDefault = false)
        {
            DeckList deck = summary.Deck;
            // treatAsDefault covers the healed-but-unsaved case; DeckQuery sorts the opening deck
            // first, so it applies to that one only.
            bool isDefault = summary.IsDefault || (treatAsDefault && ReferenceEquals(deck, FirstDeckOf(character)));

            var plate = new VisualElement();
            plate.AddToClassList("deck-plate");
            plate.EnableInClassList("deck-plate--default", isDefault);
            plate.EnableInClassList("deck-plate--selected",
                MatchDeckSelection.Choosing && _pickedDeckId == deck.Id);

            // The deck IS its image (Sam, 2026-08-07). No name on the plate: two decks tell each
            // other apart by the card the player chose to front them with, the same way a physical
            // deck is known by its sleeve rather than a label.
            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            art.tooltip = summary.Queue.IsEligible
                ? $"{deck.Name} — {deck.TotalCount} / {DeckValidator.DeckSize}"
                : $"{deck.Name} — {summary.Queue.Reason}";
            ShowcaseArt(deck, art);
            plate.Add(art);

            // Queue standing is the GOLD BORDER and nothing else (Sam, 2026-08-07). It replaced a
            // "17 / 30" caption, which replaced a Ready/Draft chip — each pass trading words for
            // something the eye reads without stopping. A deck with a gold edge is ready to play.
            art.EnableInClassList("deck-plate-art--ready", summary.Queue.IsEligible);

            // The default deck, marked on the art rather than in a caption — gold is spoken for.
            if (isDefault)
            {
                var star = new Label("★");
                star.AddToClassList("deck-plate-star");
                art.Add(star);
            }

            BindDeckTaps(plate, character, deck, summary, siblingCount);
            return plate;
        }

        /// <summary>The deck a character opens on, for the case where the stored pointer could not
        /// be repaired. Cheap enough per plate at roster scale, and it re-reads rather than caching
        /// so it cannot disagree with what the strip drew.</summary>
        private DeckList FirstDeckOf(Character character)
            => DeckQuery.ForCharacter(character, _account.Decks(), _db, _owned).Opening?.Deck;

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
        private void BindDeckTaps(VisualElement plate, Character character, DeckList deck,
                                  DeckSummary summary, int siblingCount)
        {
            IVisualElementScheduledItem pendingSingle = null;

            plate.RegisterCallback<ClickEvent>(evt =>
            {
                pendingSingle?.Pause();
                pendingSingle = null;

                if (evt.clickCount >= 2)
                {
                    OpenDeckForEdit(character.Id, deck.Id);
                    return;
                }

                pendingSingle = plate.schedule
                    .Execute(() => OpenDeckDialog(character, deck, summary, siblingCount))
                    .StartingIn(DoubleTapWindowMs);
            });
        }

        // ---- Plate sizing (Sam, 2026-08-07) ------------------------------------------------
        //
        // A plate is a SQUARE rendering of the art at the largest sensible size for the space it
        // has — not a card-shaped thumbnail at a fixed 108x152. UI Toolkit has neither media
        // queries nor aspect-ratio, so both halves of that are a layout pass: the strip measures
        // itself, decides how many squares fit, and grows them to fill the row.

        /// <summary>An absolute floor, and nothing else (Sam, 2026-08-07). It used to be the point
        /// below which a plate stopped reading as a picture, which made sense while the strip could
        /// wrap; a strip that never wraps has to keep shrinking instead, so clamping UP here would
        /// simply push the last plate out of the row. A strip small enough to reach this has more
        /// decks than the row can show well — a content problem, not a layout one.</summary>
        private const float MinPlate = 64f;

        /// <summary>An absolute guard, not the working ceiling — <see cref="RowsPerScreen"/> is what
        /// actually stops a plate growing. This only catches a viewport tall enough to make a single
        /// deck absurd.</summary>
        private const float MaxPlate = 560f;

        /// <summary>
        /// How many character rows a screenful should show (Sam, 2026-08-07). This is the real
        /// ceiling on plate size: a plate is as large as it can be while two rows still fit, so the
        /// list reads as a short scroll through a few big characters rather than a long one through
        /// many small ones. A character with enough decks to wrap past one plate-row is taller than
        /// its share, which is correct — it has more to show.
        /// </summary>
        private const float RowsPerScreen = 2f;

        /// <summary>Everything in a row that is not the square, top to bottom: the name header, the
        /// strip's padding, the plate's padding and border, and the gap to the next row. Must be
        /// kept in step with character-roster.uss.
        ///
        /// <para>112f until 2026-08-30 (#324), then 90f, now 70f. This is subtracted from
        /// <c>viewport / rows</c>, and on the 411-unit landscape panel of the day it was consuming
        /// 55% of a two-row screen and leaving the deck picture the remainder. The band it measures
        /// is tightened rather than the row count reduced, because one character row per screen
        /// stops the list reading as a list.</para>
        ///
        /// <para>Current sum: header padding-top 8 + an h2 name line ~27 + strip padding 4 top and
        /// 8 bottom + plate padding 8 and border 4 + row margin 8 = 67, rounded up to 70 for the
        /// font's line box. The strip's and the plate's vertical padding were halved in the same
        /// change; both live in character-roster.uss and both carry a comment pointing back here.
        /// </para>
        ///
        /// <para>WHY IT HAD TO COME DOWN AGAIN — HISTORY, IN PRE-#349 UNITS. At 90f the solver sat
        /// permanently on <see cref="MinPlate"/>: the roster viewport was 411 − 48 root padding −
        /// 64 header − 32 panel padding − 32 for an EMPTY status label = 235, so
        /// <c>235/2 − 90 = 27</c>, well under the 64 floor, and the rendered picture was ~43. Two
        /// rows also came to exactly the viewport, which is why row 2 clipped instead of scrolling.
        /// Collapsing the empty status band and halving the panel's vertical padding put the
        /// viewport at 283; <c>283/2 − 70 = 71.5</c> clears the floor, and two rows at 138.5 leave 6
        /// spare. The root's vertical padding came down 24 -> 8 each on 2026-08-31: <c>315/2 − 70 =
        /// 87.5</c>.</para>
        ///
        /// <para><b>REDO IT WITH 1080, NOT 411 (#349).</b> The panel stopped being 411 units tall on
        /// 2026-09-01; every subtraction above is an authored px and none of them moved, so the
        /// viewport is now roughly <c>1080 − 48 − 64 − 32 = 936</c> and <c>936/2 − 70 = 398</c>. The
        /// solver is nowhere near <see cref="MinPlate"/> any more and this constant is no longer
        /// under any pressure — but <see cref="MinPlate"/> ITSELF is still 64, which was a floor in
        /// dp and is ~24dp of real glass now. THIS SCREEN HAS NOT BEEN RE-MEASURED. Do that before
        /// treating any figure in this paragraph as settled.</para>
        ///
        /// <para>NONE OF THOSE SUBTRACTIONS IS AN INPUT. <c>viewport</c> is read off the ScrollView's
        /// resolvedStyle, so this arithmetic is here to be checked against, not to be kept true by
        /// editing it — only the terms of the SUM above (header, strip and plate padding, border,
        /// row margin) have to move when the sheet does.</para></summary>
        private const float RowChrome = 70f;

        /// <summary>Must match .deck-plate's margin-right (--space-2). Every plate carries one,
        /// including the last, so the width solve charges the strip for <c>plates</c> of them.
        /// <para>12f until 2026-08-31, when the strip's own horizontal padding came down with it.
        /// Charged once per plate, so it is the term that grows fastest with deck count.</para></summary>
        private const float PlateGutter = 8f;

        /// <summary>.deck-plate padding (--space-1) plus its 2px border, both sides — the difference
        /// between the plate's outer width and the square inside it. The same 12dp is a term in
        /// <see cref="RowChrome"/>, because the plate pays it on both axes; halving the padding
        /// moves both constants.</summary>
        private const float PlateChrome = 12f;

        /// <summary>A row solved to land exactly on the available width is a floating-point tie;
        /// see CardWall.Layout for the wrap-one-early failure this
        /// avoids.</summary>
        private const float RowSafetyMargin = 2f;

        private readonly HashSet<VisualElement> _stripLayoutQueued = new HashSet<VisualElement>();

        /// <summary>
        /// Defer to the next scheduler tick rather than resizing inline. The trigger is the strip's
        /// own GeometryChangedEvent, which Yoga fires mid-layout-pass — writing widths from inside
        /// it edits the tree while Yoga is still walking it, so only the children it has not
        /// visited pick up the new values, and the mutation re-fires the event. Same hazard, same
        /// fix, as CardWall's own deferred layout.
        /// </summary>
        private void QueueStripLayout(VisualElement strip)
        {
            if (!_stripLayoutQueued.Add(strip)) return;
            strip.schedule.Execute(() =>
            {
                _stripLayoutQueued.Remove(strip);
                LayoutDeckStrip(strip);
            });
        }

        private void LayoutDeckStrip(VisualElement strip)
        {
            float available = strip.resolvedStyle.width - RowSafetyMargin;
            // `!(x > 0f)`, not `x <= 0f`: an unresolved resolvedStyle is NaN and every comparison
            // against NaN is false, so `<= 0f` passes it through to the solve. See the long note on
            // CharacterCreateController.LayoutHeroGrid's guard, where that cost a 1.3 GB OOM kill.
            if (!(available > 0f)) return;

            int plates = 0;
            foreach (VisualElement child in strip.Children())
                if (child.ClassListContains("deck-plate")) plates++;
            if (plates == 0) return;

            // ONE LINE, ALWAYS (Sam, 2026-08-07). The strip used to solve how many plates fit at a
            // preferred size and let flex-wrap take the remainder onto a second line — so a third
            // deck did not shrink the other two, it dropped below them. Every plate on the row gets
            // an equal share of the width instead, and the row grows narrower plates rather than
            // taller rows. .deck-strip's flex-wrap: nowrap is the other half of this; neither works
            // alone.
            float fromWidth = (available - plates * PlateGutter) / plates;

            // Height still has the final say when there is width to spare. Without it a wide window
            // makes a lone deck so tall that one character fills the screen, and the list stops
            // reading as a list.
            float fromHeight = MaxPlate;
            float viewport = _rosterScroll?.resolvedStyle.height ?? 0f;
            if (viewport > 0f) fromHeight = viewport / RowsPerScreen - RowChrome;

            // Clamped at the bottom only. Clamping up to a comfortable minimum is what would put a
            // plate back outside the row, which is the wrap in a different costume.
            float side = Mathf.Max(MinPlate, Mathf.Min(fromWidth, fromHeight, MaxPlate));

            foreach (VisualElement child in strip.Children())
            {
                if (!child.ClassListContains("deck-plate")) continue;
                child.style.width = side;

                VisualElement art = child.Q<VisualElement>(className: "deck-plate-art");
                if (art == null) continue;
                art.style.width = side - PlateChrome;
                art.style.height = side - PlateChrome;   // square: the whole point of the pass
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

        private VisualElement BuildNewDeckPlate(Character character)
        {
            var plate = new Button();
            plate.AddToClassList("deck-plate");
            plate.AddToClassList("deck-plate--new");
            plate.tooltip = $"Start another deck for {character.Name}";
            plate.clicked += () => OpenNewDeckDialog(character);

            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            var plus = new Label("+");
            plus.AddToClassList("deck-plate-plus");
            art.Add(plus);
            plate.Add(art);

            return plate;
        }

        /// <summary>"+" asks which kind first, rather than assuming (Sam, 2026-08-07).</summary>
        private void OpenNewDeckDialog(Character character)
        {
            if (_account == null || character == null) return;

            _menuCharacter = character;
            _menuDeck = null;
            _menuSiblingCount = 0;

            ShowDeckPane(DeckPane.New);
            _deckModal?.RemoveFromClassList("delete-modal--hidden");
        }

        /// <summary>
        /// Offer Generated only when the pairing's pool can actually supply a legal 30 — the thin-pool
        /// case is real (see <see cref="DefaultDeckGenerator.CanGenerate"/>), and a button that throws
        /// is worse than one that is not there.
        /// </summary>
        private void RefreshNewDeckPane()
        {
            bool canGenerate = _menuCharacter != null
                               && DefaultDeckGenerator.CanGenerate(_menuCharacter, _db, _owned);
            _deckNewGenerated?.SetEnabled(canGenerate);
            if (_deckNewNote != null)
                _deckNewNote.text = canGenerate
                    ? "A generated deck is legal and playable straight away. A blank one is yours to build."
                    : $"{_menuCharacter?.Name}'s pairing doesn't have enough cards for a full deck yet, " +
                      "so only a blank draft is available.";
        }

        /// <summary>
        /// Create a deck for the character the "+" dialog is about and stay on this screen.
        ///
        /// <para><b>Both shapes are offered, and neither is assumed.</b> The deckbuilder's old "+"
        /// always made an empty deck — correct there, because a player who reached for it was already
        /// inside an editor. Here it is also the way somebody at the Find Game door gets something
        /// they can queue with, so refusing to generate would make "+" a dead end on that screen.</para>
        ///
        /// <para><b>It does not open the deckbuilder.</b> A generated deck needs no editing, and the
        /// next thing a blank one wants is a name, which is one plate-click away.</para>
        /// </summary>
        private void NewDeck(StarterDeck kind)
        {
            Character character = _menuCharacter;
            if (_account == null || character == null) return;

            DeckList created;
            try
            {
                created = _account.CreateDeck(character, _db, UnusedDeckName(character), kind);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] could not create a {kind} deck for {character.Name}: {ex.Message}");
                SetDeckError("The new deck could not be saved — see the console.");
                return;
            }

            // A character whose default does not resolve — never set, or pointing at a deck that
            // was deleted — takes this one. That is not the repair Character.DefaultDeckId forbids:
            // this is the write path, and a deck being created is exactly when a character with no
            // default should get one. Without it, deleting the default and making another left the
            // roster reporting a missing default for a character with a single, obvious deck.
            if (!DeckQuery.ForCharacter(character, _account.Decks(), _db, _owned).DefaultResolved)
            {
                character.DefaultDeckId = created.Id;
                _account.Save(character);
            }

            Debug.Log($"[CharacterRoster] created {kind} deck '{created.Name}' " +
                      $"({created.TotalCount} cards) for {character.Name}.");
            CloseDeckDialog();
            RebuildRoster();
        }

        /// <summary>"New Deck", then "New Deck 2" — a duplicate name is legal but indistinguishable
        /// in a strip of otherwise identical card backs.</summary>
        private string UnusedDeckName(Character character)
        {
            const string root = "New Deck";
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeckList deck in DeckQuery.BoundTo(character.Id, _account.Decks()))
                taken.Add(deck.Name);

            if (!taken.Contains(root)) return root;
            for (int n = 2; ; n++)
            {
                string candidate = root + " " + n;
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        // Both doors lead to one controller (#152) in one scene (#154): handing it a character is
        // what puts it in deckbuild mode, and View Collection above hands it none. Both call
        // Go("Collection"); the only difference between the two doors is the pending character.
        private void OpenDeckForEdit(string characterId, string deckId)
        {
            CollectionController.ReturnScene = "CharacterRoster";
            CollectionController.PendingCharacterId = characterId;
            CollectionController.PendingDeckId = deckId;
            Go("Collection");
        }

        // ---- Match pick: select a deck, then continue (Sam, 2026-08-07) --------------------

        /// <summary>
        /// The plate the player has chosen to play, or empty. Held on the controller rather than
        /// pushed into <see cref="MatchDeckSelection"/> as it is clicked: that static is the
        /// scene-to-scene handoff <c>NetworkedGameBootstrap</c> consumes, and writing it before
        /// Continue would mean backing out to the main menu left a match choice armed.
        /// </summary>
        private string _pickedCharacterId = "";
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
            if (_menuCharacter == null || _menuDeck == null) return;
            if (!_menuQueueReady)
            {
                SetDeckError("This deck is not queue ready yet.");
                return;
            }

            _pickedCharacterId = _menuCharacter.Id;
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

            Character character = CharacterQuery.ById(_account.Roster(), _pickedCharacterId);
            DeckList deck = null;
            foreach (DeckList d in _account.Decks())
                if (d.Id == _pickedDeckId) { deck = d; break; }

            // RebuildRoster ends by clearing the status line, so the rebuild has to happen BEFORE
            // the message that explains it — the other way round the player sees a cleared pick and
            // no reason for it.
            if (character == null || deck == null)
            {
                ClearPick();
                RebuildRoster();
                SetStatus("That deck is no longer there. Pick another.", isError: true);
                return;
            }

            QueueCheck queue = QueueEligibility.Check(deck, character, _db, _owned);
            if (!queue.IsEligible)
            {
                ClearPick();
                RebuildRoster();
                SetStatus($"{deck.Name} is not queue ready: {queue.Reason}", isError: true);
                return;
            }

            MatchDeckSelection.Select(character.Id, deck.Id);
            Go("BoardNetworked");
        }

        private void ClearPick()
        {
            _pickedCharacterId = "";
            _pickedDeckId = "";
        }

        /// <summary>
        /// Pre-arm the match pick with whatever deck was last actually submitted into a match (Sam,
        /// 2026-08-09), so Find Game can go straight to Continue instead of forcing a fresh Select
        /// every time. Re-validated against disk exactly like a manual pick, because the last-played
        /// character or deck may have been deleted since, or the deck may have fallen out of queue
        /// eligibility (a card dusted, a rule tightened) — <see cref="PlayerProfile.LastPlayedDeckId"/>
        /// carries no eligibility guarantee of its own. Finding nothing pickable leaves the screen
        /// exactly as it was before this existed: empty, waiting on a manual Select.
        /// </summary>
        private void TryPreselectLastPlayed()
        {
            if (!MatchDeckSelection.Choosing) return;

            string characterId = _account.LastPlayedCharacterId;
            string deckId = _account.LastPlayedDeckId;
            if (string.IsNullOrEmpty(characterId) || string.IsNullOrEmpty(deckId)) return;

            Character character = CharacterQuery.ById(_account.Roster(), characterId);
            if (character == null) return;

            DeckList deck = null;
            foreach (DeckList d in _account.Decks())
                if (d.Id == deckId && d.CharacterId == character.Id) { deck = d; break; }
            if (deck == null) return;

            if (!QueueEligibility.Check(deck, character, _db, _owned).IsEligible) return;

            _pickedCharacterId = character.Id;
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

        private void OpenDeleteDialog(Character character)
        {
            if (_account == null || character == null) return;

            try
            {
                _pendingDelete = character;
                _pendingDeleteDeckCount = _account.DecksLostWith(character.Id).Count;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] delete preview failed: {ex.Message}");
                SetStatus("Could not count the decks that would be deleted.", isError: true);
                return;
            }

            // The deck clause is the whole point of this dialog (§2.6, §3): the cascade is only honest
            // if the player is told what it takes with it. A count of zero has nothing to warn about,
            // and "also deletes 0 decks" reads as a bug rather than an all-clear — so that case drops
            // the clause instead of printing it.
            string warning = _pendingDeleteDeckCount switch
            {
                0 => $"Deleting {character.Name} cannot be undone.",
                1 => $"Deleting {character.Name} also deletes its only deck. This cannot be undone.",
                _ => $"Deleting {character.Name} also deletes its {_pendingDeleteDeckCount} decks. " +
                     "This cannot be undone.",
            };
            if (_deleteMessage != null) _deleteMessage.text = warning;
            if (_deleteError != null) _deleteError.text = "";
            _deleteModal?.RemoveFromClassList("delete-modal--hidden");
        }

        private void ConfirmDelete()
        {
            if (_account == null || _pendingDelete == null) return;

            try
            {
                int destroyed = _account.DeleteCharacter(_pendingDelete.Id);
                if (destroyed != _pendingDeleteDeckCount)
                    Debug.LogWarning($"[CharacterRoster] delete preview counted {_pendingDeleteDeckCount} decks, " +
                                     $"but the cascade removed {destroyed}.");
                CloseDeleteDialog();
                RebuildRoster();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] character delete failed: {ex.Message}");
                if (_deleteError != null)
                    _deleteError.text = "Delete failed — nothing else was attempted. See the console.";
            }
        }

        private void CloseDeleteDialog()
        {
            _deleteModal?.AddToClassList("delete-modal--hidden");
            _pendingDelete = null;
            _pendingDeleteDeckCount = 0;
        }

        private void SetStatus(string message, bool isError = false)
        {
            if (_status == null) return;
            string text = message ?? "";
            _status.text = text;
            // An empty status band is `display: none` and costs the plate solver nothing. See the
            // note on .roster-status: this label sits beside the ScrollView, so any height it holds
            // comes out of the viewport LayoutDeckStrip divides between rows.
            _status.EnableInClassList("roster-status--on", text.Length > 0);
            _status.EnableInClassList("roster-status--error", isError);
        }

        private static string Pretty(CardClass c)
        {
            string raw = c.ToString().ToLowerInvariant();
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        private static string ClassSlug(CardClass c) => c.ToString().ToLowerInvariant();

        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[CharacterRoster] Scene '{sceneName}' is not in the build list. " +
                               "Run Emberfall/Build/Sync Scene List.");
                return;
            }

            Debug.Log($"[CharacterRoster] → {sceneName}");
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }
}
