using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The front door — the first screen a player sees (dev-docs/ui/client-surfaces.md:7).
    /// A UI Toolkit meta-screen in the deckbuilder's lineage: theme.uss tokens, a UIDocument
    /// host, elements queried by name and wired in <see cref="OnEnable"/>.
    ///
    /// <para>The five destinations (Find Game, Single Player, Deck Builder, Store, Leaderboards) still
    /// log their intent — navigation is a later pass, and each handler is a named method so a
    /// scene-loader drops straight in. <b>Deck Builder is the one exception</b>: with no characters on
    /// the device there is nothing to build for, so it opens the create-character step instead of
    /// logging. That is the only entry point to character creation until S6 exists.</para>
    ///
    /// <para><b>This screen is also where identity begins.</b> A device that has never been named is
    /// asked once, here, and the answer plus the first character are the first collection files ever
    /// written on the machine (<see cref="PlayerAccount"/>). Everything is local: no display name
    /// travels the wire yet, so the opponent still sees a seat id.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// MainMenuScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainMenuController : MonoBehaviour
    {
        /// <summary>
        /// The classes a character may pair. NEUTRAL is excluded — it is the shared pool every
        /// character already gets, not a class you can be (<c>Character.IsValidPairing</c>). Ordered
        /// as the enum is, so the list does not reshuffle between builds.
        /// </summary>
        private static readonly CardClass[] PairableClasses =
        {
            CardClass.EMBERWARD, CardClass.TIDECALLER, CardClass.GRAVEKEEP, CardClass.VERDANT,
        };

        private VisualElement _root;
        private PlayerAccount _account;
        private LoadingOverlay _loading;

        // Loaded on first use rather than at OnEnable: the card database is only needed to generate a
        // starter deck, and pulling it in makes the front door wait on Addressables for nothing.
        private ICardDatabase _db;

        private Label _identityName;
        private Label _identitySub;

        private VisualElement _nameModal;
        private Label _nameTitle;
        private Label _nameError;
        private TextField _nameField;
        private Button _nameCancel;
        private bool _nameRequired;

        private VisualElement _characterModal;
        private Label _characterTitle;
        private Label _characterError;
        private TextField _characterNameField;
        private DropdownField _characterPrimary;
        private DropdownField _characterSecondary;
        private Button _characterCancel;

        private VisualElement _accountModal;
        private VisualElement _accountList;
        private Label _accountError;

        /// <summary>Frame-0 coverage, the same rig GameBootstrap/NetworkedGameBootstrap use
        /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/loading-screen-plate.md §5): the overlay exists before anything renders. The menu's
        /// own content starts hidden (mm-root--hidden in the UXML) so there is no frame where
        /// the unbound menu shows through before <see cref="RevealMenu"/> runs.</summary>
        private void Awake()
        {
            _loading = LoadingOverlay.Create(Camera.main);
        }

        private void OnEnable()
        {
            // GetComponent<UIDocument>().rootVisualElement is Unity's synthetic per-panel
            // wrapper, NOT the UXML's own top-level element — the "root" element (which carries
            // mm-root--hidden) is one level below it. Grabbing the wrapper here made
            // RevealMenu's RemoveFromClassList a silent no-op: the class it targeted was never
            // on the object it called it on, so the menu stayed visibility:hidden forever.
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[MainMenu] No rootVisualElement — is the UXML assigned?");
                _loading?.NotifyBootFailed("UI not assigned — see console");
                return;
            }

            Wire("btn-find-game", OnFindGame);
            Wire("btn-single-player", OnSinglePlayer);
            Wire("btn-deck-builder", OnDeckBuilder);
            Wire("btn-store", OnStore);
            Wire("btn-leaderboards", OnLeaderboards);

            BindIdentity();
            BindNameModal();
            BindCharacterModal();
            BindAccountModal();

            // A corrupt collection file must not brick the front door. The stores throw rather than
            // reporting an empty collection (so the next save cannot overwrite something
            // recoverable), which means the failure surfaces HERE, on a screen whose other four
            // buttons still work.
            try
            {
                _account = PlayerAccount.Local;
                Debug.Log($"[MainMenu] collection scope → {_account.ScopeDirectory}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not open this device's collection: {ex.Message}");
                SetIdentity("Collection unreadable", "See the console — nothing was overwritten.");
                FinishBootstrap();
                return;
            }

            RefreshIdentity();
            if (_account.NeedsDisplayName) OpenNameModal(required: true);
            FinishBootstrap();
        }

        /// <summary>Bootstrap (identity/account) is done. Reveal the menu once the boot plate has
        /// actually finished fading — not on this signal directly.
        ///
        /// <para>Screen Space Overlay always paints over LoadingOverlay's camera-parented rig,
        /// regardless of the rig's own fade-out timing — revealing on this signal alone would
        /// race the overlay off the screen the instant identity work finishes, which is close
        /// enough to instant that the plate would never be seen at all. <see cref="RevealMenu"/>
        /// only runs once <see cref="LoadingOverlay.OnFadedOut"/> fires.</para></summary>
        private void FinishBootstrap()
        {
            if (_loading != null)
            {
                _loading.OnFadedOut += RevealMenu;
                _loading.NotifyReady();
            }
            else
            {
                RevealMenu();
            }
        }

        /// <summary>Show the menu (and whatever modal OnEnable decided to open under it). Called
        /// from <see cref="FinishBootstrap"/> — directly when there is no boot plate, otherwise
        /// once the plate has finished fading.</summary>
        private void RevealMenu()
        {
            _root.RemoveFromClassList("mm-root--hidden");
        }

        // System.Action explicitly: Emberfall.Core.Contracts has an Action type of its own (a card
        // effect), and both namespaces are in scope here.
        private void Wire(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MainMenu] Button '{name}' not found in UXML."); return; }
            button.RegisterCallback<ClickEvent>(_ => onClick());
        }

        // ── Identity plate ──────────────────────────────────────────────────────────────

        private void BindIdentity()
        {
            _identityName = _root.Q<Label>("identity-name");
            _identitySub = _root.Q<Label>("identity-sub");
            Wire("identity", OnIdentityClicked);
        }

        private void SetIdentity(string name, string sub)
        {
            if (_identityName != null) _identityName.text = name;
            if (_identitySub != null) _identitySub.text = sub;
        }

        /// <summary>
        /// Redraw the plate from disk. Counts come off the real stores rather than being tracked
        /// incrementally — the numbers are then correct after any write, including ones this screen
        /// did not make.
        /// </summary>
        private void RefreshIdentity()
        {
            if (_account == null) return;

            int characters = _account.Roster().Count;
            string sub = characters == 0
                ? "No characters yet — open Deck Builder"
                : characters == 1 ? "1 character" : $"{characters} characters";

            // Which seat, but only once there is more than one to confuse it with. Two accounts on one
            // machine exist to be played against each other, and the failure that wastes the most time
            // is not noticing you are signed in as the other one. Naturally absent in a release build,
            // where the registry only ever holds the grandfathered account.
            int seat = SeatNumberOrZero();
            if (seat > 0) sub = $"Account {seat} · {sub}";

            // "Unnamed player" rather than an invented default: a saved default is indistinguishable
            // from a chosen name, and the prompt would never fire again (PlayerAccount.DisplayNameOr).
            SetIdentity(_account.DisplayNameOr("Unnamed player"), sub);
        }

        private void OnIdentityClicked()
        {
            if (_account == null) return;
            OpenNameModal(required: _account.NeedsDisplayName);
        }

        // ── Name modal ──────────────────────────────────────────────────────────────────

        private void BindNameModal()
        {
            _nameModal = _root.Q<VisualElement>("name-modal");
            _nameTitle = _root.Q<Label>("name-title");
            _nameError = _root.Q<Label>("name-error");
            _nameField = _root.Q<TextField>("name-field");
            _nameCancel = _root.Q<Button>("btn-name-cancel");

            Wire("btn-name-confirm", ConfirmName);
            Wire("btn-name-cancel", () => Hide(_nameModal));

            if (_nameField == null) return;
            // Enter submits. A single-field form where the only way forward is the mouse is a form
            // people abandon.
            _nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) ConfirmName();
            });
            // Clear a stale refusal as soon as the player changes anything, so the message always
            // describes what is currently in the field.
            _nameField.RegisterValueChangedCallback(_ => SetError(_nameError, ""));
        }

        /// <summary>
        /// Open the name prompt. <paramref name="required"/> is first run — there is no name to fall
        /// back to, so Cancel is hidden rather than disabled: a dead button invites clicking.
        /// </summary>
        private void OpenNameModal(bool required)
        {
            _nameRequired = required;
            if (_nameTitle != null)
                _nameTitle.text = required ? "What should we call you?" : "Change your name";
            if (_nameCancel != null)
                _nameCancel.style.display = required ? DisplayStyle.None : DisplayStyle.Flex;
            if (_nameField != null)
            {
                _nameField.SetValueWithoutNotify(_account?.Profile.DisplayName ?? "");
                _nameField.Focus();
            }
            SetError(_nameError, "");
            Show(_nameModal);
        }

        private void ConfirmName()
        {
            if (_account == null) return;

            NameCheck check = _account.SetDisplayName(_nameField?.value ?? "");
            if (!check.IsValid)
            {
                // The refusal reason is authored player-facing in PlayerName — rendered verbatim so
                // there is one wording, not one per form.
                SetError(_nameError, check.Reason);
                return;
            }

            Hide(_nameModal);
            RefreshIdentity();

            // First run continues straight into a character — a named player with no character has
            // nothing to play, and this is the only moment we know they are new. On later renames it
            // must not fire, or every name change would reopen the create form.
            if (_nameRequired && _account.Roster().Count == 0) OpenCharacterModal();
        }

        // ── Character modal ─────────────────────────────────────────────────────────────

        private void BindCharacterModal()
        {
            _characterModal = _root.Q<VisualElement>("character-modal");
            _characterTitle = _root.Q<Label>("character-title");
            _characterError = _root.Q<Label>("character-error");
            _characterNameField = _root.Q<TextField>("character-name-field");
            _characterPrimary = _root.Q<DropdownField>("character-primary");
            _characterSecondary = _root.Q<DropdownField>("character-secondary");
            _characterCancel = _root.Q<Button>("btn-character-cancel");

            Wire("btn-character-confirm", ConfirmCharacter);
            Wire("btn-character-cancel", () => Hide(_characterModal));

            var choices = new List<string>(PairableClasses.Length);
            foreach (CardClass c in PairableClasses) choices.Add(Pretty(c));

            if (_characterPrimary != null)
            {
                _characterPrimary.choices = choices;
                _characterPrimary.index = 0;
                // The secondary list depends on the primary (a pairing must be two distinct classes),
                // so it is rebuilt rather than filtered at confirm time — an impossible option the
                // player can select is a worse form than one they cannot.
                _characterPrimary.RegisterValueChangedCallback(_ => RebuildSecondaryChoices());
            }
            if (_characterSecondary != null)
            {
                _characterSecondary.choices = new List<string>(choices);
                _characterSecondary.RegisterValueChangedCallback(_ => SetError(_characterError, ""));
            }
            if (_characterNameField != null)
                _characterNameField.RegisterValueChangedCallback(_ => SetError(_characterError, ""));

            // Once at bind time, not only on open: until this runs the secondary list is the UXML
            // default (all four classes, including the primary) with no value selected. OpenCharacter
            // Modal rebuilds it anyway, so nothing user-visible depended on it — which is exactly why
            // it would have sat there being wrong.
            RebuildSecondaryChoices();
        }

        private void OpenCharacterModal()
        {
            if (_account == null) return;

            bool first = _account.Roster().Count == 0;
            if (_characterTitle != null)
                _characterTitle.text = first ? "Create your first character" : "Create a character";
            if (_characterCancel != null)
                _characterCancel.text = first ? "Later" : "Cancel";
            if (_characterNameField != null) _characterNameField.SetValueWithoutNotify("");

            RebuildSecondaryChoices();
            SetError(_characterError, "");
            Show(_characterModal);
            _characterNameField?.Focus();
        }

        /// <summary>Every class except the chosen primary — the pairing must be two distinct classes.</summary>
        private void RebuildSecondaryChoices()
        {
            SetError(_characterError, "");
            if (_characterSecondary == null) return;

            CardClass primary = SelectedPrimary();
            var choices = new List<string>(PairableClasses.Length - 1);
            foreach (CardClass c in PairableClasses)
                if (c != primary) choices.Add(Pretty(c));

            string previous = _characterSecondary.value;
            _characterSecondary.choices = choices;
            int keep = previous == null ? -1 : choices.IndexOf(previous);
            _characterSecondary.index = keep >= 0 ? keep : 0;
        }

        private CardClass SelectedPrimary()
        {
            int i = _characterPrimary?.index ?? 0;
            return PairableClasses[Mathf.Clamp(i, 0, PairableClasses.Length - 1)];
        }

        private CardClass SelectedSecondary()
        {
            CardClass primary = SelectedPrimary();
            string chosen = _characterSecondary?.value;
            foreach (CardClass c in PairableClasses)
                if (c != primary && Pretty(c) == chosen) return c;

            // The dropdown is rebuilt from PairableClasses, so this is unreachable unless the list and
            // the enum drift apart. Falling back to the first legal partner keeps the form usable.
            foreach (CardClass c in PairableClasses)
                if (c != primary) return c;
            return CardClass.NEUTRAL;   // refused by IsValidPairing, which is the correct outcome
        }

        private void ConfirmCharacter()
        {
            if (_account == null) return;

            // Character names are held to the same rules as the player's own: both are
            // player-authored strings rendered in a nameplate-sized box, and two rule sets for one
            // constraint is how they drift apart.
            NameCheck check = PlayerName.Check(_characterNameField?.value ?? "");
            if (!check.IsValid) { SetError(_characterError, check.Reason); return; }

            ICardDatabase db;
            try
            {
                db = _db ??= CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] card database load failed: {ex.Message}");
                SetError(_characterError, "Card data could not be loaded — see the console.");
                return;
            }

            CardClass primary = SelectedPrimary();
            CardClass secondary = SelectedSecondary();

            // Asked before creating rather than after failing: a pairing whose pool cannot supply a
            // legal 30 is a card-pool shortfall, and the form should say so instead of throwing.
            if (!PlayerAccount.CanCreate(primary, secondary, db))
            {
                SetError(_characterError,
                    $"{Pretty(primary)} + {Pretty(secondary)} doesn't have enough cards for a deck yet.");
                return;
            }

            try
            {
                Character created = _account.CreateCharacter(check.Normalized, primary, secondary, db);
                Debug.Log($"[MainMenu] created character '{created.Name}' " +
                          $"({created.PrimaryClass}/{created.SecondaryClass}) id={created.Id} " +
                          $"starter deck={created.DefaultDeckId}");
            }
            catch (Exception ex) when (ex is DeckGenerationException || ex is ArgumentException)
            {
                Debug.LogError($"[MainMenu] character creation failed: {ex.Message}");
                SetError(_characterError, ex.Message);
                return;
            }

            Hide(_characterModal);
            RefreshIdentity();
        }

        // ── Account switcher (testing) ──────────────────────────────────────────────────

        /// <summary>
        /// Wire the accounts modal and reveal its entry point — but only in the editor and in
        /// development builds (Sam 2026-07-27: two accounts on one device, strictly for testing).
        ///
        /// <para><b>The gate is on the affordance, not the mechanism.</b> <see cref="DeviceAccounts"/>
        /// compiles and runs in every configuration; a release build simply resolves the one
        /// grandfathered account and never shows this button. Gating the registry itself would leave the
        /// shipped path as the one nobody exercises, which is how an unregistered background survived
        /// four shipped assets.</para>
        /// </summary>
        private void BindAccountModal()
        {
            _accountModal = _root.Q<VisualElement>("account-modal");
            _accountList = _root.Q<VisualElement>("account-list");
            _accountError = _root.Q<Label>("account-error");

            Wire("btn-account-close", () => Hide(_accountModal));
            Wire("btn-account-new", OnNewAccount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Wire("btn-switch-account", OpenAccountModal);
            var entry = _root.Q<Button>("btn-switch-account");
            // The USS default is display:none so a release build stays hidden even if this is reached.
            if (entry != null) entry.style.display = DisplayStyle.Flex;
#endif
        }

        /// <summary>This account's 1-based position, or 0 when it is the only one on the device.</summary>
        private static int SeatNumberOrZero()
        {
            try
            {
                var all = DeviceAccounts.All();
                if (all.Count < 2) return 0;
                string activeId = DeviceAccounts.Active.Id;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Id == activeId) return i + 1;
            }
            catch (Exception ex)
            {
                // A nameplate must not die of an unreadable registry — the modal reports it instead.
                Debug.LogWarning($"[MainMenu] account registry unreadable ({ex.GetType().Name}).");
            }
            return 0;
        }

        private void OpenAccountModal()
        {
            SetError(_accountError, "");
            RebuildAccountList();
            Show(_accountModal);
        }

        /// <summary>
        /// Rebuild the rows from disk. Rows are built here rather than in UXML because the count is
        /// whatever the registry holds — two today, and there is no reason for the screen to be the
        /// thing that caps it.
        /// </summary>
        private void RebuildAccountList()
        {
            if (_accountList == null) return;
            _accountList.Clear();

            IReadOnlyList<LocalAccount> all;
            string activeId;
            try
            {
                all = DeviceAccounts.All();
                activeId = DeviceAccounts.Active.Id;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not read the account registry: {ex.Message}");
                SetError(_accountError, "The account list could not be read — see the console.");
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                LocalAccount account = all[i];
                bool isActive = account.Id == activeId;

                var row = new VisualElement();
                row.AddToClassList("mm-account-row");
                if (isActive) row.AddToClassList("mm-account-row--active");

                var label = new Label(DeviceAccounts.LabelOf(account, i));
                label.AddToClassList("mm-account-row__label");
                row.Add(label);

                if (isActive)
                {
                    var here = new Label("signed in");
                    here.AddToClassList("mm-account-row__sub");
                    row.Add(here);
                }
                else
                {
                    string id = account.Id;   // captured per row, not the loop variable
                    var switchTo = new Button(() => SwitchToAccount(id)) { text = "Switch" };
                    switchTo.AddToClassList("mm-modal__button");
                    switchTo.AddToClassList("mm-account-row__button");
                    row.Add(switchTo);
                }

                _accountList.Add(row);
            }
        }

        private void OnNewAccount()
        {
            try
            {
                LocalAccount created = DeviceAccounts.Create();
                Debug.Log($"[MainMenu] created local account {created.Id}. This seat did NOT move — " +
                          "press Switch on it to sign in here, or pin another process to it with " +
                          "-emberfallAccount.");
                // Rebuild rather than reload: creating an account for the other seat must leave this
                // one exactly where it was (DeviceAccounts.Create).
                RebuildAccountList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not create a local account: {ex.Message}");
                SetError(_accountError, ex.Message);
            }
        }

        private void SwitchToAccount(string accountId)
        {
            try
            {
                DeviceAccounts.SwitchTo(accountId);
                ReloadThisScene();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not switch account: {ex.Message}");
                SetError(_accountError, ex.Message);
            }
        }

        /// <summary>
        /// Reload the menu so every screen rebuilds against the new account.
        ///
        /// <para><b>Deliberately blunt.</b> This screen is holding an account, a card database, a bound
        /// name field and a roster count, and other screens hold more. Re-resolving each one is a list
        /// that will be incomplete the first time somebody adds to it, and the symptom of an incomplete
        /// switch — one player's character written into another's file — is the worst outcome this
        /// feature has. A scene reload cannot be partial.</para>
        /// </summary>
        private static void ReloadThisScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.SceneManager.LoadScene(scene.name);
        }

        // ── Shared modal plumbing ───────────────────────────────────────────────────────

        private static void Show(VisualElement modal) => modal?.RemoveFromClassList("mm-modal--hidden");
        private static void Hide(VisualElement modal) => modal?.AddToClassList("mm-modal--hidden");

        /// <summary>A space, not an empty string, so the error line keeps its height and the buttons
        /// below do not jump when a refusal appears or clears.</summary>
        private static void SetError(Label label, string message)
        {
            if (label != null) label.text = string.IsNullOrEmpty(message) ? " " : message;
        }

        /// <summary>Enum name as a player reads it: EMBERWARD → Emberward.</summary>
        private static string Pretty(CardClass c)
        {
            string raw = c.ToString();
            return raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw.Substring(1).ToLowerInvariant();
        }

        // ---- Destinations (stubbed — navigation is a later pass) ----
        // Find Game → BoardNetworked.unity (server matchmaking; the only destination with a backend today).
        /// <summary>
        /// Find Game → <c>BoardNetworked</c>.
        ///
        /// <para><b>There is no queue screen to build.</b> That scene's <c>NetworkedGameBootstrap</c>
        /// already connects, sends its hello and waits, and the server's gate pairs whoever is waiting
        /// with the next arrival (<c>MatchGate.PairAsync</c>) — so this one scene load is the entire
        /// matchmaking entry point. Two clients that both press this find each other.</para>
        /// </summary>
        private void OnFindGame() => Go("BoardNetworked");

        /// <summary>Single Player → <c>Board</c>, the local vs-AI slice (<c>GameBootstrap</c>).</summary>
        private void OnSinglePlayer() => Go("Board");

        /// <summary>
        /// Load a scene by name, reporting the one failure mode that is otherwise silent.
        ///
        /// <para><b>A scene missing from the build list does not throw a useful error</b> — in a player
        /// it is simply not there, and in the Editor <c>LoadScene</c> needs it in
        /// <c>EditorBuildSettings</c> or nothing happens. Both present as a dead button. The build list
        /// is kept in sync by <c>Emberfall/Build/Sync Scene List</c>; this check is what tells you it
        /// wasn't.</para>
        /// </summary>
        private static void Go(string sceneName)
        {
            // CanStreamedLevelBeLoaded, not SceneUtility.GetBuildIndexByScenePath — the latter wants a
            // full asset path and answers -1 for a bare scene name, which would make this guard reject
            // every load it was written to protect.
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[MainMenu] Scene '{sceneName}' is not in the build list, so this button " +
                               "cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            Debug.Log($"[MainMenu] → {sceneName}");
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }

        /// <summary>
        /// Deck Builder → DeckbuilderSpike.unity. With an empty roster there is nothing to build for,
        /// so this opens character creation instead — the only entry point to it until S6 is built.
        ///
        /// <para>No character switcher exists yet (that's S6's sidebar, still unbuilt), so a roster of
        /// more than one opens the first character's own deck rather than offering a choice.</para>
        /// </summary>
        private void OnDeckBuilder()
        {
            if (_account == null) return;
            var roster = _account.Roster();
            if (roster.Count == 0) { OpenCharacterModal(); return; }

            DeckbuilderController.PendingCharacterId = roster[0].Id;
            Go("DeckbuilderSpike");
        }

        // Store → no backend yet.
        private void OnStore() => Debug.Log("[MainMenu] Store → coming soon. No backend yet.");

        // Leaderboards → no backend yet.
        private void OnLeaderboards() => Debug.Log("[MainMenu] Leaderboards → coming soon. No backend yet.");
    }
}
