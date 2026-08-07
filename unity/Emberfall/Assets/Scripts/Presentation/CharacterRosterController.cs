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
        private static readonly CardClass[] PairableClasses =
        {
            CardClass.EMBERWARD, CardClass.TIDECALLER, CardClass.GRAVEKEEP, CardClass.VERDANT,
        };

        private static readonly List<string> StarterChoices = new List<string>
        {
            "Generated — queue ready",
            "Blank — start a draft",
        };

        private VisualElement _root;
        private PlayerAccount _account;
        private ICardDatabase _db;
        private IUiSpriteProvider _uiSprites;

        private VisualElement _rosterList;
        private Label _rosterCount;
        private Label _rosterIntro;
        private Label _emptyState;
        private Label _status;

        private Label _createTitle;
        private TextField _nameField;
        private DropdownField _primaryField;
        private DropdownField _secondaryField;
        private DropdownField _starterField;
        private Label _starterHelp;
        private Label _powerHint;
        private Label _createError;
        private Button _createButton;

        private VisualElement _deleteModal;
        private Label _deleteMessage;
        private Label _deleteError;
        private Character _pendingDelete;
        private int _pendingDeleteDeckCount;

        private VisualElement _matchBar;
        private Button _matchContinue;

        private VisualElement _deckModal;
        private Label _deckModalTitle;
        private Label _deckModalSub;
        private Label _deckModalError;
        private VisualElement _deckMenuPane;
        private VisualElement _deckRenamePane;
        private VisualElement _deckBackPane;
        private VisualElement _deckBackGrid;
        private Label _deckBackNote;
        private VisualElement _deckNewPane;
        private Label _deckNewNote;
        private Button _deckNewGenerated;
        private VisualElement _deckDeletePane;
        private Label _deckDeleteMessage;
        private TextField _deckRenameField;
        private Button _deckSelectButton;
        private Button _deckEditButton;
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

        /// <summary>The back highlighted in the picker, committed only by Save.</summary>
        private string _pendingCardBackId = "";

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[CharacterRoster] No rootVisualElement — is the UXML assigned?");
                return;
            }

            CacheElements();
            WireChrome();
            WireCreateForm();
            WireDeleteDialog();
            WireDeckDialog();

            _uiSprites = CardBackCatalog.NewProvider();

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] could not open this device's collection: {ex.Message}");
                SetStatus("Collection unavailable — see the console.", isError: true);
                SetCreateEnabled(false);
                return;
            }

            RebuildRoster();
        }

        private void CacheElements()
        {
            _rosterList = _root.Q<VisualElement>("roster-list");
            _rosterCount = _root.Q<Label>("roster-count");
            _rosterIntro = _root.Q<Label>("roster-intro");
            _emptyState = _root.Q<Label>("empty-state");
            _status = _root.Q<Label>("status");

            _createTitle = _root.Q<Label>("create-title");
            _nameField = _root.Q<TextField>("character-name");
            _primaryField = _root.Q<DropdownField>("primary-class");
            _secondaryField = _root.Q<DropdownField>("secondary-class");
            _starterField = _root.Q<DropdownField>("starter-deck");
            _starterHelp = _root.Q<Label>("starter-help");
            _powerHint = _root.Q<Label>("power-hint");
            _createError = _root.Q<Label>("create-error");
            _createButton = _root.Q<Button>("create-character");

            _deleteModal = _root.Q<VisualElement>("delete-modal");
            _deleteMessage = _root.Q<Label>("delete-message");
            _deleteError = _root.Q<Label>("delete-error");

            _matchBar = _root.Q<VisualElement>("match-bar");
            _matchContinue = _root.Q<Button>("match-continue");

            _deckModal = _root.Q<VisualElement>("deck-modal");
            _deckModalTitle = _root.Q<Label>("deck-modal-title");
            _deckModalSub = _root.Q<Label>("deck-modal-sub");
            _deckModalError = _root.Q<Label>("deck-modal-error");
            _deckMenuPane = _root.Q<VisualElement>("deck-menu-pane");
            _deckRenamePane = _root.Q<VisualElement>("deck-rename-pane");
            _deckBackPane = _root.Q<VisualElement>("deck-back-pane");
            _deckBackGrid = _root.Q<VisualElement>("deck-back-grid");
            _deckBackNote = _root.Q<Label>("deck-back-note");
            _deckNewPane = _root.Q<VisualElement>("deck-new-pane");
            _deckNewNote = _root.Q<Label>("deck-new-note");
            _deckNewGenerated = _root.Q<Button>("deck-new-generated");
            _deckDeletePane = _root.Q<VisualElement>("deck-delete-pane");
            _deckDeleteMessage = _root.Q<Label>("deck-delete-message");
            _deckRenameField = _root.Q<TextField>("deck-rename-field");
            _deckSelectButton = _root.Q<Button>("deck-select");
            _deckEditButton = _root.Q<Button>("deck-edit");
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
                Go("MainMenu");
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

        private void WireCreateForm()
        {
            var classes = new List<string>(PairableClasses.Length);
            foreach (CardClass c in PairableClasses) classes.Add(Pretty(c));

            if (_primaryField != null)
            {
                _primaryField.choices = classes;
                _primaryField.index = 0;
                _primaryField.RegisterValueChangedCallback(_ =>
                {
                    RebuildSecondaryChoices();
                    RefreshPowerHint();
                    SetCreateError("");
                });
            }

            if (_secondaryField != null)
            {
                _secondaryField.choices = new List<string>(classes);
                _secondaryField.RegisterValueChangedCallback(_ => SetCreateError(""));
            }

            if (_starterField != null)
            {
                _starterField.choices = StarterChoices;
                _starterField.index = 0;
                _starterField.RegisterValueChangedCallback(_ => SetCreateError(""));
            }

            _nameField?.RegisterValueChangedCallback(_ => SetCreateError(""));
            _root.Q<Button>("swap-classes")?.RegisterCallback<ClickEvent>(_ => SwapClasses());
            _createButton?.RegisterCallback<ClickEvent>(_ => CreateCharacter());

            RebuildSecondaryChoices();
            RefreshPowerHint();
        }

        private void WireDeleteDialog()
        {
            _root.Q<Button>("cancel-delete")?.RegisterCallback<ClickEvent>(_ => CloseDeleteDialog());
            _root.Q<Button>("confirm-delete")?.RegisterCallback<ClickEvent>(_ => ConfirmDelete());
        }

        // ---- Deck dialog (Sam, 2026-08-07): a plate opens options, not the editor --------------

        /// <summary>Which pane of the deck dialog is showing. Cancel means "back to the menu" from
        /// anywhere but <see cref="Menu"/>, where it means "close" — one button, because a dialog
        /// with both a Back and a Close is a dialog nobody reads.</summary>
        private enum DeckPane { Menu, New, Rename, CardBack, Delete }

        private DeckPane _deckPane = DeckPane.Menu;

        private void WireDeckDialog()
        {
            _root.Q<Button>("deck-new-generated")?.RegisterCallback<ClickEvent>(_ => NewDeck(StarterDeck.Generated));
            _root.Q<Button>("deck-new-blank")?.RegisterCallback<ClickEvent>(_ => NewDeck(StarterDeck.Blank));
            _deckSelectButton?.RegisterCallback<ClickEvent>(_ => PickForMatch());
            _root.Q<Button>("deck-edit")?.RegisterCallback<ClickEvent>(_ => EditOpenDeck());
            _root.Q<Button>("deck-rename")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Rename));
            _root.Q<Button>("deck-change-back")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.CardBack));
            _deckSetDefaultButton?.RegisterCallback<ClickEvent>(_ => SetDefaultDeck());
            _deckDeleteButton?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Delete));
            _deckModalCancel?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_deckPane == DeckPane.Menu || _deckPane == DeckPane.New) CloseDeckDialog();
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

            // Select is the match door's one addition to this dialog, and while it is showing it
            // — not Edit — is the primary action: picking is why the player opened the screen.
            bool choosing = MatchDeckSelection.Choosing;
            bool alreadyPicked = choosing && _pickedDeckId == deck.Id;
            _menuQueueReady = summary.Queue.IsEligible;

            Show(_deckSelectButton, choosing);
            _deckEditButton?.EnableInClassList("roster-button--cta", !choosing);
            _deckEditButton?.EnableInClassList("roster-button--ghost", choosing);
            if (choosing && _deckSelectButton != null)
            {
                _deckSelectButton.SetEnabled(summary.Queue.IsEligible && !alreadyPicked);
                _deckSelectButton.text = alreadyPicked ? "Selected" : "Select this deck";
                _deckSelectButton.tooltip = summary.Queue.IsEligible ? "" : summary.Queue.Reason;
            }

            ShowDeckPane(DeckPane.Menu);
            _deckModal?.RemoveFromClassList("delete-modal--hidden");
        }

        private void ShowDeckPane(DeckPane pane)
        {
            // New is the one pane with no deck behind it yet, so the guard is on the panes that
            // read _menuDeck rather than on entry.
            if (_menuDeck == null && pane != DeckPane.New) return;
            _deckPane = pane;
            SetDeckError("");

            Show(_deckNewPane, pane == DeckPane.New);
            Show(_deckMenuPane, pane == DeckPane.Menu);
            Show(_deckRenamePane, pane == DeckPane.Rename);
            Show(_deckBackPane, pane == DeckPane.CardBack);
            Show(_deckDeletePane, pane == DeckPane.Delete);

            // Only the sub-line's facts belong to the deck as a whole; the panes retitle above it.
            if (_deckModalTitle != null)
                _deckModalTitle.text = pane switch
                {
                    DeckPane.New => $"New deck for {_menuCharacter?.Name}",
                    DeckPane.Rename => "Rename deck",
                    DeckPane.CardBack => "Card back",
                    DeckPane.Delete => "Delete deck?",
                    _ => _menuDeck.Name,
                };
            Show(_deckModalSub, pane == DeckPane.Menu);

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
            if (pane == DeckPane.CardBack)
                BuildCardBackGrid();
            if (pane == DeckPane.Delete && _deckDeleteMessage != null)
                _deckDeleteMessage.text =
                    $"'{_menuDeck.Name}' and its {_menuDeck.TotalCount} cards are gone for good. " +
                    "The cards themselves stay in your collection.";
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
            if (_menuCharacter == null || _menuDeck == null) return;
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

        private void EditOpenDeck()
        {
            if (_menuCharacter == null || _menuDeck == null) return;
            string characterId = _menuCharacter.Id, deckId = _menuDeck.Id;
            CloseDeckDialog();
            OpenDeckForEdit(characterId, deckId);
        }

        private void CloseDeckDialog()
        {
            _deckModal?.AddToClassList("delete-modal--hidden");
            _menuCharacter = null;
            _menuDeck = null;
            _menuSiblingCount = 0;
            _menuQueueReady = false;
            _pendingCardBackId = "";
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

            _rosterList.Clear();
            if (_rosterCount != null)
                _rosterCount.text = roster.Count == 1 ? "1 character" : $"{roster.Count} characters";
            if (_emptyState != null)
                _emptyState.style.display = roster.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (Character character in roster)
                _rosterList.Add(BuildCharacterRow(character, decks));

            RefreshCreatePolicy(roster.Count > 0);
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

            var identity = new VisualElement();
            identity.AddToClassList("character-identity");

            var name = new Label(character.Name);
            name.AddToClassList("character-name");
            identity.Add(name);

            var pairing = new VisualElement();
            pairing.AddToClassList("character-pairing");
            var primary = new Label(Pretty(character.PrimaryClass));
            primary.AddToClassList("pair-class");
            primary.AddToClassList("class--" + ClassSlug(character.PrimaryClass));
            var arrow = new Label("→");
            arrow.AddToClassList("pair-arrow");
            var secondary = new Label(Pretty(character.SecondaryClass));
            secondary.AddToClassList("pair-class");
            secondary.AddToClassList("class--" + ClassSlug(character.SecondaryClass));
            pairing.Add(primary);
            pairing.Add(arrow);
            pairing.Add(secondary);
            identity.Add(pairing);

            // "Hero power: Verdant" used to sit here, and a Queue ready / Draft pill under it. Both
            // cut (Sam, 2026-08-07): the hero power is always the primary class, which the pairing
            // right above already states and the player already knows, and the pill duplicated in
            // words what the deck plates carry per deck.

            var delete = new Button { text = "Delete" };
            delete.AddToClassList("delete-character");
            delete.tooltip = $"Delete {character.Name} and every deck bound to it";
            delete.clicked += () => OpenDeleteDialog(character);

            row.Add(identity);
            row.Add(BuildDeckStrip(character, decks));
            row.Add(delete);
            return row;
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

            CharacterDecks owned = DeckQuery.ForCharacter(character, decks, _db);

            // A DefaultDeckId naming a deleted deck is left dangling on purpose (Character.cs:
            // "surfaced, never repaired") — but only where the dangle means something. With a single
            // deck there is nothing to be ambiguous about: it is the only thing the queue can reach
            // for, so it is drawn as the default and nothing is reported (Sam, 2026-08-07).
            bool soleDeckIsDefault = owned.Count == 1 && !owned.DefaultResolved;
            if (!owned.DefaultResolved && !soleDeckIsDefault && !string.IsNullOrEmpty(character.DefaultDeckId))
            {
                var hint = new Label("Default deck was deleted. Pick another.");
                hint.AddToClassList("deck-strip-hint");
                strip.Add(hint);
            }

            foreach (DeckSummary summary in owned.Decks)
                strip.Add(BuildDeckPlate(character, summary, owned.Count, soleDeckIsDefault));

            strip.Add(BuildNewDeckPlate(character));

            return strip;
        }

        private VisualElement BuildDeckPlate(Character character, DeckSummary summary, int siblingCount,
                                             bool treatAsDefault = false)
        {
            DeckList deck = summary.Deck;
            bool isDefault = summary.IsDefault || treatAsDefault;

            var plate = new Button();
            plate.AddToClassList("deck-plate");
            plate.EnableInClassList("deck-plate--default", isDefault);
            plate.EnableInClassList("deck-plate--selected",
                MatchDeckSelection.Choosing && _pickedDeckId == deck.Id);
            plate.tooltip = isDefault
                ? $"{deck.Name} — {character.Name}'s default deck"
                : deck.Name;
            plate.clicked += () => OpenDeckDialog(character, deck, summary, siblingCount);

            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            Sprite back = CardBackCatalog.SpriteFor(deck.CardBackId, _uiSprites);
            if (back != null) art.style.backgroundImage = new StyleBackground(back);
            plate.Add(art);

            var name = new Label(isDefault ? "★ " + deck.Name : deck.Name);
            name.AddToClassList("deck-plate-name");
            plate.Add(name);

            // Queue standing used to be a green or amber chip with a word in it. It is the colour
            // of the count now (Sam, 2026-08-07) — "30 / 30" in green reads as ready without
            // spending a word on it, and the reason a draft is a draft is a hover away rather than
            // a warning on the page.
            var count = new Label($"{deck.TotalCount} / {DeckValidator.DeckSize}");
            count.AddToClassList("deck-plate-count");
            count.EnableInClassList("deck-plate-count--ready", summary.Queue.IsEligible);
            plate.Add(count);

            if (!summary.Queue.IsEligible) plate.tooltip = summary.Queue.Reason;

            return plate;
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

            var name = new Label("New deck");
            name.AddToClassList("deck-plate-name");
            plate.Add(name);

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
            bool canGenerate = _menuCharacter != null && DefaultDeckGenerator.CanGenerate(_menuCharacter, _db);
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
            if (!DeckQuery.ForCharacter(character, _account.Decks(), _db).DefaultResolved)
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

        private void OpenCharacter(string characterId)
        {
            DeckbuilderController.PendingCharacterId = characterId;
            DeckbuilderController.PendingDeckId = "";
            Go("DeckbuilderSpike");
        }

        private void OpenDeckForEdit(string characterId, string deckId)
        {
            DeckbuilderController.PendingCharacterId = characterId;
            DeckbuilderController.PendingDeckId = deckId;
            Go("DeckbuilderSpike");
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

            QueueCheck queue = QueueEligibility.Check(deck, character, _db);
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

        private void RefreshCreatePolicy(bool hasCharacters)
        {
            if (_createTitle != null)
                _createTitle.text = hasCharacters ? "Create another character" : "Create your first character";

            bool offersChoice = _account != null && _account.OffersStarterChoice();
            if (_starterField != null)
            {
                _starterField.style.display = offersChoice ? DisplayStyle.Flex : DisplayStyle.None;
                _starterField.index = 0;
            }
            if (_starterHelp != null)
            {
                _starterHelp.text = offersChoice
                    ? "Choose a playable generated deck or a blank draft."
                    : "Your first character starts with a playable generated deck.";
            }
        }

        private void RebuildSecondaryChoices()
        {
            if (_secondaryField == null) return;

            CardClass primary = SelectedPrimary();
            var choices = new List<string>(PairableClasses.Length - 1);
            foreach (CardClass c in PairableClasses)
                if (c != primary) choices.Add(Pretty(c));

            string previous = _secondaryField.value;
            _secondaryField.choices = choices;
            int keep = previous == null ? -1 : choices.IndexOf(previous);
            _secondaryField.index = keep >= 0 ? keep : 0;
        }

        private void RefreshPowerHint()
        {
            if (_powerHint is not null)
                _powerHint.text = $"Hero power: {Pretty(SelectedPrimary())}";
        }

        private void SwapClasses()
        {
            CardClass oldPrimary = SelectedPrimary();
            CardClass oldSecondary = SelectedSecondary();

            if (_primaryField != null)
                _primaryField.index = Array.IndexOf(PairableClasses, oldSecondary);
            RebuildSecondaryChoices();
            if (_secondaryField != null)
                _secondaryField.index = _secondaryField.choices.IndexOf(Pretty(oldPrimary));
            RefreshPowerHint();
            SetCreateError("");
        }

        private CardClass SelectedPrimary()
        {
            int index = _primaryField?.index ?? 0;
            return PairableClasses[Mathf.Clamp(index, 0, PairableClasses.Length - 1)];
        }

        private CardClass SelectedSecondary()
        {
            CardClass primary = SelectedPrimary();
            string selected = _secondaryField?.value;
            foreach (CardClass c in PairableClasses)
                if (c != primary && Pretty(c) == selected) return c;

            foreach (CardClass c in PairableClasses)
                if (c != primary) return c;
            return CardClass.NEUTRAL;
        }

        private void CreateCharacter()
        {
            if (_account == null || _db == null) return;

            NameCheck name = PlayerName.Check(_nameField?.value ?? "");
            if (!name.IsValid)
            {
                SetCreateError(name.Reason);
                return;
            }

            CardClass primary = SelectedPrimary();
            CardClass secondary = SelectedSecondary();
            bool offersChoice = _account.OffersStarterChoice();
            StarterDeck starter = offersChoice && _starterField?.index == 1
                ? StarterDeck.Blank
                : StarterDeck.Generated;

            if (starter == StarterDeck.Generated && !PlayerAccount.CanCreate(primary, secondary, _db))
            {
                SetCreateError($"{Pretty(primary)} + {Pretty(secondary)} doesn't have enough cards for a deck yet.");
                return;
            }

            try
            {
                Character created = _account.CreateCharacter(
                    name.Normalized, primary, secondary, _db, starter);
                Debug.Log($"[CharacterRoster] created character id={created.Id} " +
                          $"pairing={created.PrimaryClass}/{created.SecondaryClass} " +
                          $"starter={starter}");
                if (_nameField != null) _nameField.SetValueWithoutNotify("");
                OpenCharacter(created.Id);
            }
            catch (Exception ex) when (ex is DeckGenerationException || ex is ArgumentException)
            {
                Debug.LogError($"[CharacterRoster] character creation failed: {ex.Message}");
                SetCreateError(ex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] character creation could not be saved: {ex.Message}");
                SetCreateError("Character could not be saved — see the console.");
            }
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

            string noun = _pendingDeleteDeckCount == 1 ? "deck" : "decks";
            if (_deleteMessage != null)
                _deleteMessage.text = $"Deleting {character.Name} also deletes " +
                                      $"{_pendingDeleteDeckCount} {noun}. This cannot be undone.";
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

        private void SetCreateEnabled(bool enabled)
        {
            _createButton?.SetEnabled(enabled);
            _nameField?.SetEnabled(enabled);
            _primaryField?.SetEnabled(enabled);
            _secondaryField?.SetEnabled(enabled);
            _starterField?.SetEnabled(enabled);
        }

        private void SetCreateError(string message)
        {
            if (_createError != null) _createError.text = message ?? "";
        }

        private void SetStatus(string message, bool isError = false)
        {
            if (_status == null) return;
            _status.text = message ?? "";
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
