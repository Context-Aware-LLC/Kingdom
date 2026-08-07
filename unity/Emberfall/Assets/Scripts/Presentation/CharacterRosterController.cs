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

            var select = new Button();
            select.AddToClassList("character-select");
            select.tooltip = $"Open {character.Name}'s decks";
            select.clicked += () => OpenCharacter(character.Id);

            var name = new Label(character.Name);
            name.AddToClassList("character-name");
            select.Add(name);

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
            select.Add(pairing);

            var power = new Label($"Hero power: {Pretty(character.PrimaryClass)}");
            power.AddToClassList("character-detail");
            select.Add(power);

            var deck = new Label(defaultDeck == null
                ? "Default deck: missing"
                : $"Default deck: {defaultDeck.Name} · {defaultDeck.TotalCount} / 30");
            deck.AddToClassList("character-detail");
            select.Add(deck);

            var eligibility = new Label(queue.IsEligible ? "Queue ready" : $"Draft · {queue.Reason}");
            eligibility.AddToClassList("queue-pill");
            eligibility.AddToClassList(queue.IsEligible ? "queue-pill--ready" : "queue-pill--draft");
            select.Add(eligibility);

            var delete = new Button { text = "Delete" };
            delete.AddToClassList("delete-character");
            delete.tooltip = $"Delete {character.Name} and every deck bound to it";
            delete.clicked += () => OpenDeleteDialog(character);

            row.Add(select);
            row.Add(delete);
            return row;
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
