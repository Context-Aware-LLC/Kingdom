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

        private VisualElement _deckModal;
        private Label _deckModalTitle;
        private Label _deckModalSub;
        private Label _deckModalError;
        private VisualElement _deckMenuPane;
        private VisualElement _deckRenamePane;
        private VisualElement _deckBackPane;
        private VisualElement _deckBackGrid;
        private Label _deckBackNote;
        private TextField _deckRenameField;
        private Button _deckModalCancel;
        private Button _deckModalConfirm;

        /// <summary>The deck the open dialog is about, and the character it belongs to. Held rather
        /// than re-read per pane: the dialog is modal, so nothing can change them underneath it.</summary>
        private Character _menuCharacter;
        private DeckList _menuDeck;

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

            _deckModal = _root.Q<VisualElement>("deck-modal");
            _deckModalTitle = _root.Q<Label>("deck-modal-title");
            _deckModalSub = _root.Q<Label>("deck-modal-sub");
            _deckModalError = _root.Q<Label>("deck-modal-error");
            _deckMenuPane = _root.Q<VisualElement>("deck-menu-pane");
            _deckRenamePane = _root.Q<VisualElement>("deck-rename-pane");
            _deckBackPane = _root.Q<VisualElement>("deck-back-pane");
            _deckBackGrid = _root.Q<VisualElement>("deck-back-grid");
            _deckBackNote = _root.Q<Label>("deck-back-note");
            _deckRenameField = _root.Q<TextField>("deck-rename-field");
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
            if (_rosterIntro != null && MatchDeckSelection.Choosing)
                _rosterIntro.text = "Choose one of your queue-ready decks to enter the match.";
            if (MatchDeckSelection.Choosing)
                _root.AddToClassList("character-roster--match-pick");
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
        private enum DeckPane { Menu, Rename, CardBack }

        private DeckPane _deckPane = DeckPane.Menu;

        private void WireDeckDialog()
        {
            _root.Q<Button>("deck-edit")?.RegisterCallback<ClickEvent>(_ => EditOpenDeck());
            _root.Q<Button>("deck-rename")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.Rename));
            _root.Q<Button>("deck-change-back")?.RegisterCallback<ClickEvent>(_ => ShowDeckPane(DeckPane.CardBack));
            _deckModalCancel?.RegisterCallback<ClickEvent>(_ =>
            {
                if (_deckPane == DeckPane.Menu) CloseDeckDialog();
                else ShowDeckPane(DeckPane.Menu);
            });
            _deckModalConfirm?.RegisterCallback<ClickEvent>(_ => ConfirmDeckPane());
            _deckRenameField?.RegisterValueChangedCallback(_ => SetDeckError(""));
        }

        private void OpenDeckDialog(Character character, DeckList deck, DeckSummary summary)
        {
            _menuCharacter = character;
            _menuDeck = deck;
            _pendingCardBackId = deck.CardBackId;

            if (_deckModalSub != null)
            {
                string standing = summary.Queue.IsEligible ? "Queue ready" : $"Draft · {summary.Queue.Reason}";
                string mark = summary.IsDefault ? $"{character.Name}'s default · " : "";
                _deckModalSub.text = $"{mark}{deck.TotalCount} / {DeckValidator.DeckSize} · {standing}";
            }

            ShowDeckPane(DeckPane.Menu);
            _deckModal?.RemoveFromClassList("delete-modal--hidden");
        }

        private void ShowDeckPane(DeckPane pane)
        {
            if (_menuDeck == null) return;
            _deckPane = pane;
            SetDeckError("");

            Show(_deckMenuPane, pane == DeckPane.Menu);
            Show(_deckRenamePane, pane == DeckPane.Rename);
            Show(_deckBackPane, pane == DeckPane.CardBack);

            // Only the sub-line's facts belong to the deck as a whole; the panes retitle above it.
            if (_deckModalTitle != null)
                _deckModalTitle.text = pane switch
                {
                    DeckPane.Rename => "Rename deck",
                    DeckPane.CardBack => "Card back",
                    _ => _menuDeck.Name,
                };
            Show(_deckModalSub, pane == DeckPane.Menu);

            if (_deckModalCancel != null) _deckModalCancel.text = pane == DeckPane.Menu ? "Close" : "Back";
            Show(_deckModalConfirm, pane != DeckPane.Menu);

            if (pane == DeckPane.Rename)
                _deckRenameField?.SetValueWithoutNotify(_menuDeck.Name);
            if (pane == DeckPane.CardBack)
                BuildCardBackGrid();
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
            SetStatus("");
        }

        private VisualElement BuildCharacterRow(Character character, IReadOnlyList<DeckList> decks)
        {
            if (MatchDeckSelection.Choosing) return BuildMatchCharacterRow(character, decks);

            DeckList defaultDeck = CharacterQuery.DefaultDeckOf(character, decks);
            QueueCheck queue = QueueEligibility.Check(defaultDeck, character, _db);

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

            var power = new Label($"Hero power: {Pretty(character.PrimaryClass)}");
            power.AddToClassList("character-detail");
            identity.Add(power);

            // Character-level standing is still the DEFAULT deck's standing — that is the deck the
            // queue reaches for. Each plate carries its own beside it.
            var eligibility = new Label(queue.IsEligible ? "Queue ready" : $"Draft · {queue.Reason}");
            eligibility.AddToClassList("queue-pill");
            eligibility.AddToClassList(queue.IsEligible ? "queue-pill--ready" : "queue-pill--draft");
            identity.Add(eligibility);

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
            // "surfaced, never repaired"). The deckbuilder used to do the surfacing; the pointer is a
            // roster-level fact and the roster is what draws it now.
            if (!owned.DefaultResolved && !string.IsNullOrEmpty(character.DefaultDeckId))
            {
                var hint = new Label($"{character.Name} has no default deck — the one it pointed at was deleted.");
                hint.AddToClassList("deck-strip-hint");
                strip.Add(hint);
            }

            foreach (DeckSummary summary in owned.Decks)
                strip.Add(BuildDeckPlate(character, summary));

            strip.Add(BuildNewDeckPlate(character));
            return strip;
        }

        private VisualElement BuildDeckPlate(Character character, DeckSummary summary)
        {
            DeckList deck = summary.Deck;

            var plate = new Button();
            plate.AddToClassList("deck-plate");
            plate.EnableInClassList("deck-plate--default", summary.IsDefault);
            plate.tooltip = summary.IsDefault
                ? $"{deck.Name} — {character.Name}'s default deck"
                : deck.Name;
            plate.clicked += () => OpenDeckDialog(character, deck, summary);

            var art = new VisualElement();
            art.AddToClassList("deck-plate-art");
            Sprite back = CardBackCatalog.SpriteFor(deck.CardBackId, _uiSprites);
            if (back != null) art.style.backgroundImage = new StyleBackground(back);
            plate.Add(art);

            var name = new Label(summary.IsDefault ? "★ " + deck.Name : deck.Name);
            name.AddToClassList("deck-plate-name");
            plate.Add(name);

            var count = new Label($"{deck.TotalCount} / {DeckValidator.DeckSize}");
            count.AddToClassList("deck-plate-count");
            plate.Add(count);

            var tag = new Label(summary.Queue.IsEligible ? "Ready" : "Draft");
            tag.AddToClassList("deck-plate-tag");
            tag.AddToClassList(summary.Queue.IsEligible ? "deck-plate-tag--ready" : "deck-plate-tag--draft");
            tag.tooltip = summary.Queue.IsEligible ? "" : summary.Queue.Reason;
            plate.Add(tag);

            return plate;
        }

        private VisualElement BuildNewDeckPlate(Character character)
        {
            var plate = new Button();
            plate.AddToClassList("deck-plate");
            plate.AddToClassList("deck-plate--new");
            plate.tooltip = $"Start another deck for {character.Name}";
            plate.clicked += () => NewDeck(character);

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

        /// <summary>
        /// Create an empty deck for this character and stay here.
        ///
        /// <para><b>Empty, not generated</b> — the deckbuilder's rule, kept when this moved:
        /// <see cref="DefaultDeckGenerator"/> exists to hand a brand-new character something playable
        /// on day one, and running it here would hand a player who asked for a blank deck 30 cards to
        /// dismantle first.</para>
        ///
        /// <para><b>And it does not open the deckbuilder.</b> The old "+" was inside the editor, so
        /// opening the new deck was the only thing it could do. From here the next thing a player
        /// wants is usually to name it, which is a plate away.</para>
        /// </summary>
        private void NewDeck(Character character)
        {
            if (_account == null || character == null) return;

            try
            {
                _account.Save(new DeckList
                {
                    Id = PlayerAccount.NewId(),
                    CharacterId = character.Id,   // L5: bound at birth, never inferred later
                    Name = UnusedDeckName(character),
                });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterRoster] could not create a deck for {character.Name}: {ex.Message}");
                SetStatus("The new deck could not be saved — see the console.", isError: true);
                return;
            }

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

        private VisualElement BuildMatchCharacterRow(Character character, IReadOnlyList<DeckList> decks)
        {
            CharacterDecks choices = DeckQuery.ForCharacter(character, decks, _db);
            var row = new VisualElement();
            row.AddToClassList("character-row");
            row.AddToClassList("character-row--match-pick");
            row.AddToClassList("character-row--" + ClassSlug(character.PrimaryClass));

            var details = new VisualElement();
            details.AddToClassList("match-character-details");
            var name = new Label(character.Name);
            name.AddToClassList("character-name");
            details.Add(name);
            var pairing = new Label($"{Pretty(character.PrimaryClass)} → {Pretty(character.SecondaryClass)}");
            pairing.AddToClassList("character-detail");
            details.Add(pairing);
            var power = new Label($"Hero power: {Pretty(character.PrimaryClass)}");
            power.AddToClassList("character-detail");
            details.Add(power);
            row.Add(details);

            var deckChoices = new VisualElement();
            deckChoices.AddToClassList("match-deck-choices");
            int ready = 0;
            for (int i = 0; i < choices.Decks.Count; i++)
            {
                DeckSummary summary = choices.Decks[i];
                DeckList deck = summary.Deck;
                string defaultMark = summary.IsDefault ? " · Default" : "";
                if (summary.Queue.IsEligible)
                {
                    var choose = new Button { text = $"{deck.Name}{defaultMark} · {deck.TotalCount} / 30" };
                    choose.AddToClassList("match-deck-choice");
                    choose.tooltip = $"Play {character.Name} with {deck.Name}";
                    choose.clicked += () => ChooseForMatch(character, deck);
                    deckChoices.Add(choose);
                    ready++;
                }
                else
                {
                    var draft = new VisualElement();
                    draft.AddToClassList("match-deck-draft");
                    var draftName = new Label($"{deck.Name}{defaultMark} · {deck.TotalCount} / 30");
                    draftName.AddToClassList("match-deck-draft-name");
                    draft.Add(draftName);
                    var reason = new Label($"Draft · {summary.Queue.Reason}");
                    reason.AddToClassList("match-deck-draft-reason");
                    draft.Add(reason);
                    var edit = new Button { text = "Edit" };
                    edit.AddToClassList("match-deck-edit");
                    edit.clicked += () => OpenDeckForEdit(character.Id, deck.Id);
                    draft.Add(edit);
                    deckChoices.Add(draft);
                }
            }

            if (ready == 0)
            {
                var none = new Label("No queue-ready decks. Edit a draft to continue.");
                none.AddToClassList("match-deck-empty");
                deckChoices.Add(none);
            }
            row.Add(deckChoices);
            return row;
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

        private void ChooseForMatch(Character character, DeckList deck)
        {
            QueueCheck queue = QueueEligibility.Check(deck, character, _db);
            if (!queue.IsEligible)
            {
                SetStatus($"{deck.Name} is not queue ready: {queue.Reason}", isError: true);
                return;
            }

            MatchDeckSelection.Select(character.Id, deck.Id);
            Go("BoardNetworked");
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
            if (_powerHint != null)
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
