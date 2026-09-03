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
    /// Character creation, as a scene (Sam, 2026-08-23). Three sequential views over one class
    /// picker: pick the primary class (its Hero Power), pick the secondary (cards only), then name
    /// the character and create it with NO deck — it lands in the roster as a lone "+" row, the
    /// same empty state a character whose decks were all deleted already shows.
    ///
    /// <para>It was a <c>.delete-modal</c> overlay inside <c>CharacterRosterScreen.uxml</c> until
    /// then. Same call the Store got on 2026-08-20 and for the same reason: full-screen, opaque,
    /// and a destination — the overlay was hosting rather than design.</para>
    ///
    /// <para><b>Navigation is the caller-writes-its-own-way-home shape</b> that
    /// <c>ShopScreenController.ReturnScene</c> and <c>CollectionController.ReturnScene</c> use: the
    /// caller sets the static, this screen reads it once and resets it, so a scene can never be
    /// stranded by a stale value.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CharacterCreateController : MonoBehaviour
    {
        /// <summary>Where Cancel and a completed creation go. Set by the caller before loading.</summary>
        public static string ReturnScene = "CharacterRoster";

        /// <summary>
        /// Every real class is pairable; NEUTRAL is not a class a character can be. A pairing whose
        /// pool is too thin to generate a 30 is caught downstream by
        /// <see cref="PlayerAccount.CanCreate"/>, not here — this screen creates without a deck.
        /// </summary>
        private static readonly CardClass[] PairableClasses = BuildPairableClasses();

        private static CardClass[] BuildPairableClasses()
        {
            var all = (CardClass[])Enum.GetValues(typeof(CardClass));
            var real = new List<CardClass>(all.Length);
            foreach (CardClass c in all)
                if (c != CardClass.NEUTRAL) real.Add(c);
            return real.ToArray();
        }

        /// <summary>Which of the three views is showing.</summary>
        private enum CreateView { Picker, Detail, Confirm }

        private VisualElement _root;
        private PlayerAccount _account;
        private ICardDatabase _db;
        private IArtProvider _art;
        private string _returnScene = "CharacterRoster";

        private readonly List<CardClass> _picks = new List<CardClass>(2);
        private CardClass _detailClass;

        private VisualElement _pickerView, _detailView, _confirmView;
        private VisualElement _heroColumnLeft, _heroColumnRight;
        private VisualElement _detailPortrait, _detailBullets, _powerBlock;
        private Label _passTitle, _passHelp, _detailName, _detailPass;
        private Label _powerName, _powerDesc, _confirmSummary, _nameError;
        private Button _detailSelect;
        private TextField _nameField;

        private void OnEnable()
        {
            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "CharacterRoster" : ReturnScene;
            ReturnScene = "CharacterRoster";

            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[CharacterCreate] No rootVisualElement — is the UXML assigned?");
                return;
            }

            CacheElements();
            Wire();

#if UNITY_EDITOR
            _art = new EditorArtProvider();
#else
            _art = new AddressableArtProvider();
#endif

            try
            {
                _account = PlayerAccount.Local;
                _db = CardDatabaseLoader.Load();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterCreate] could not open this device's collection: {ex.Message}");
                SetNameError("Collection unavailable — see the console.");
                return;
            }

            _picks.Clear();
            _nameField?.SetValueWithoutNotify("");
            SetNameError("");
            ShowView(CreateView.Picker);
        }

        private void CacheElements()
        {
            _pickerView = _root.Q<VisualElement>("create-picker-view");
            _detailView = _root.Q<VisualElement>("create-detail-view");
            _confirmView = _root.Q<VisualElement>("create-confirm-view");

            _passTitle = _root.Q<Label>("create-pass-title");
            _passHelp = _root.Q<Label>("create-pass-help");
            _heroColumnLeft = _root.Q<VisualElement>("create-hero-column-left");
            _heroColumnRight = _root.Q<VisualElement>("create-hero-column-right");

            _detailPortrait = _root.Q<VisualElement>("create-detail-portrait");
            _detailName = _root.Q<Label>("create-detail-name");
            _detailPass = _root.Q<Label>("create-detail-pass");
            _detailBullets = _root.Q<VisualElement>("create-detail-bullets");
            _powerBlock = _root.Q<VisualElement>("create-power-block");
            _powerName = _root.Q<Label>("create-power-name");
            _powerDesc = _root.Q<Label>("create-power-desc");
            _detailSelect = _root.Q<Button>("create-detail-select");

            _confirmSummary = _root.Q<Label>("create-confirm-summary");
            _nameField = _root.Q<TextField>("create-name-field");
            _nameError = _root.Q<Label>("create-name-error");
        }

        private void Wire()
        {
            _root.Q<Button>("create-cancel")?.RegisterCallback<ClickEvent>(_ => Go(_returnScene));
            _root.Q<Button>("create-detail-back")?.RegisterCallback<ClickEvent>(_ => ShowView(CreateView.Picker));
            _detailSelect?.RegisterCallback<ClickEvent>(_ => TogglePick(_detailClass));
            _root.Q<Button>("create-change")?.RegisterCallback<ClickEvent>(_ => ChangeSecondary());
            _root.Q<Button>("create-continue")?.RegisterCallback<ClickEvent>(_ => CommitCreate());
            _nameField?.RegisterValueChangedCallback(_ => SetNameError(""));
        }

        /// <summary>True while pass 1 is live — nothing picked yet, so the next pick is the primary.</summary>
        private bool ChoosingPrimary => _picks.Count == 0;

        private void ShowView(CreateView view)
        {
            Show(_pickerView, view == CreateView.Picker);
            Show(_detailView, view == CreateView.Detail);
            Show(_confirmView, view == CreateView.Confirm);

            if (view == CreateView.Picker) BuildHeroChoices();
            else if (view == CreateView.Confirm) RefreshConfirm();
        }

        /// <summary>
        /// The button picker, rebuilt each time it shows so the primary choice stays visible.
        /// Its title is pass-specific: pass 1 chooses the Hero Power; pass 2 adds cards only.
        /// </summary>
        private void BuildHeroChoices()
        {
            if (_heroColumnLeft == null || _heroColumnRight == null) return;
            _heroColumnLeft.Clear();
            _heroColumnRight.Clear();

            if (ChoosingPrimary)
            {
                if (_passTitle != null) _passTitle.text = "Choose your hero";
                if (_passHelp != null)
                    _passHelp.text = "A character is TWO classes. This first pick decides your Hero Power.";
            }
            else
            {
                if (_passTitle != null) _passTitle.text = "Choose a second class";
                if (_passHelp != null)
                    _passHelp.text = $"Your Hero Power stays {Pretty(_picks[0])}'s — this pick only adds its cards.";
            }

            int split = (PairableClasses.Length + 1) / 2;
            for (int i = 0; i < PairableClasses.Length; i++)
            {
                VisualElement column = i < split ? _heroColumnLeft : _heroColumnRight;
                column.Add(BuildHeroChoice(PairableClasses[i]));
            }
        }

        /// <summary>
        /// One PLATE per class (#312): the class portrait, the class name, and the class's hero
        /// power on a line beneath it, so the pick is no longer blind. Before this the plate was
        /// the class name and nothing else — eight labels in boxes, distinguished only by a
        /// coloured spine.
        ///
        /// <para><b>Art is back; the SOLVER is not, and that distinction is the whole point.</b>
        /// What 7cc7c9f5 tore out was a wrapping grid that measured its own box in a
        /// <c>GeometryChangedEvent</c> and wrote a solved portrait size onto every tile — an
        /// unresolved box is NaN, every guard against it compared with <c>&lt;= 0f</c>, and the
        /// relayout ran away until the kernel killed the app for thrashing. Nothing here measures
        /// anything: the plate is a flex row, the portrait is a USS width, and the columns are
        /// fixed at four. Adding a geometry callback or an inline size write to this method
        /// reopens that bug, and it will not look like this file when it does.</para>
        ///
        /// <para>The plate stays ONE Button — one hit target, one tap — with every decorative
        /// child set to <see cref="PickingMode.Ignore"/> so nothing inside it can swallow the
        /// tap. The Button's own text is empty; the labels below are its children.</para>
        /// </summary>
        private Button BuildHeroChoice(CardClass heroClass)
        {
            int order = _picks.IndexOf(heroClass);   // -1 = unpicked, 0 = primary, 1 = secondary
            bool selected = order >= 0;
            string prettyName = Pretty(heroClass);

            var plate = new Button(() => OpenHeroDetail(heroClass))
            {
                text = string.Empty,
                tooltip = $"View {prettyName}"
            };
            plate.AddToClassList("roster-button");
            plate.AddToClassList("hero-choice");
            // The enum member lowercased, matching character-roster.uss's .hero-choice--* rows and
            // the .character-row--* spine they mirror. No name map on either side: a class whose
            // token has no row simply gets the neutral spine.
            plate.AddToClassList("hero-choice--" + heroClass.ToString().ToLowerInvariant());
            plate.EnableInClassList("hero-choice--selected", selected);

            // The art twice, deliberately: once as a full-bleed wash behind the whole plate so the
            // row reads as the class rather than as a list item, and once as the square portrait
            // that carries the actual likeness. The wash is a CROP THROUGH THE MIDDLE of a
            // full-body portrait — a torso, not a face — which is exactly why it is dimmed to a
            // texture and the square beside it is what the player actually reads. USS has no
            // background-position here, so this is composition rather than a crop offset.
            var wash = new VisualElement { pickingMode = PickingMode.Ignore };
            wash.AddToClassList("hero-choice-wash");
            plate.Add(wash);

            var scrim = new VisualElement { pickingMode = PickingMode.Ignore };
            scrim.AddToClassList("hero-choice-scrim");
            plate.Add(scrim);

            var portrait = new VisualElement { pickingMode = PickingMode.Ignore };
            portrait.AddToClassList("hero-choice-portrait");
            plate.Add(portrait);

            // One art id, two elements. The provider caches its handles, so the second call for the
            // same class costs a dictionary hit — this is not eight loads becoming sixteen.
            _art.LoadArt("hero_" + ClassSlug(heroClass), sprite =>
            {
                if (sprite == null) return;
                var image = new StyleBackground(sprite);
                portrait.style.backgroundImage = image;
                wash.style.backgroundImage = image;
            });

            var body = new VisualElement { pickingMode = PickingMode.Ignore };
            body.AddToClassList("hero-choice-body");

            var name = new Label(prettyName) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("hero-choice-name");
            body.Add(name);

            var power = new Label(HeroPowerLine(heroClass)) { pickingMode = PickingMode.Ignore };
            power.AddToClassList("hero-choice-power");
            // On pass 2 the power line is still WORTH SHOWING and no longer TRUE of this pick: the
            // secondary class contributes cards only. Dimming says "this is information about the
            // class, not about what you are choosing" without making the plates change shape
            // between the two passes.
            power.EnableInClassList("hero-choice-power--muted", !ChoosingPrimary);
            body.Add(power);
            plate.Add(body);

            if (selected)
            {
                var badge = new Label((order + 1).ToString()) { pickingMode = PickingMode.Ignore };
                badge.AddToClassList("hero-choice-badge");
                plate.Add(badge);
            }

            return plate;
        }

        /// <summary>
        /// "Searing Bolt · Deal 1 damage." — the class's hero power, read off the power CARD by way
        /// of <see cref="HeroPowers.ForClass"/>, the same seam the detail view and match start use.
        /// A class with no power card yields an empty line rather than a placeholder.
        ///
        /// <para><b>Unstyled on purpose, unlike the detail view's copy of the same string.</b>
        /// <c>CardTextStyling.Style</c> wraps keywords in <c>&lt;size=120%&gt;</c>, which grows the
        /// line box — on a plate with ~50dp of room under the class name that is the difference
        /// between two lines fitting and the second one being clipped. The emphasis is worth its
        /// height in the detail panel and is not worth it here.</para>
        /// </summary>
        private string HeroPowerLine(CardClass heroClass)
        {
            int powerId = HeroPowers.ForClass(heroClass);
            if (_db == null || powerId <= 0 || !_db.TryGet(powerId, out CardDefinition power)) return "";
            return string.IsNullOrEmpty(power.Text) ? power.Name : $"{power.Name} · {power.Text}";
        }
        /// <summary>
        /// Add or remove a hero from the picks. Selecting appends (primary first, then secondary);
        /// re-selecting a picked hero removes it. The moment both slots are full the flow
        /// auto-advances to the confirm view.
        /// </summary>
        private void TogglePick(CardClass heroClass)
        {
            int idx = _picks.IndexOf(heroClass);
            if (idx >= 0)
                _picks.RemoveAt(idx);
            else if (_picks.Count < 2)
                _picks.Add(heroClass);
            else
                return;   // both slots full and this one is not among them — ignore

            ShowView(_picks.Count == 2 ? CreateView.Confirm : CreateView.Picker);
        }

        /// <summary>
        /// The per-hero detail view: portrait, name, a pass-specific line, the class's hero power,
        /// and the traits block.
        ///
        /// <para><b>The hero power block is READ OFF THE CARD, never written here (NOR-291).</b> Its
        /// name is the power card's own <c>Name</c> and its description that card's <c>Text</c> —
        /// literally the same string the power shows on the board in a match. The danger was ever a
        /// SECOND copy of the rules with nothing comparing them.</para>
        /// </summary>
        private void OpenHeroDetail(CardClass heroClass)
        {
            _detailClass = heroClass;

            if (_detailPortrait != null)
            {
                _detailPortrait.style.backgroundImage = StyleKeyword.None;
                _art.LoadArt("hero_" + ClassSlug(heroClass), sprite =>
                {
                    if (sprite != null) _detailPortrait.style.backgroundImage = new StyleBackground(sprite);
                });
            }
            if (_detailName != null) _detailName.text = Pretty(heroClass);
            if (_detailPass != null)
                _detailPass.text = ChoosingPrimary
                    ? "Selecting this hero locks in its Hero Power."
                    : $"Selecting this class adds its cards to your pool. Your Hero Power stays {Pretty(_picks[0])}'s.";

            FillTraitBullets(heroClass);
            FillHeroPower(heroClass);

            bool selected = _picks.Contains(heroClass);
            if (_detailSelect != null) _detailSelect.text = selected ? "Deselect" : "Select";

            ShowView(CreateView.Detail);
        }

        /// <summary>Fill the trait bullets for one class from presentation copy (NOR-281).</summary>
        private void FillTraitBullets(CardClass heroClass)
        {
            if (_detailBullets == null) return;
            _detailBullets.Clear();
            foreach (string trait in HeroDetailCopy.TraitsFor(heroClass))
            {
                var bullet = new Label("• " + trait);
                bullet.AddToClassList("hero-detail-bullet");
                _detailBullets.Add(bullet);
            }
        }

        /// <summary>
        /// Fill the hero power block for one class (NOR-291). The class → power mapping comes from
        /// <see cref="HeroPowers.ForClass"/>, the same method <c>MatchEngine</c> uses to stamp
        /// <c>Hero.HeroPowerId</c> at match start, so the screen shows the power the player will
        /// actually get, by construction. A class with no power card on disk hides the block rather
        /// than showing an empty panel.
        /// </summary>
        private void FillHeroPower(CardClass heroClass)
        {
            int powerId = HeroPowers.ForClass(heroClass);
            CardDefinition power = null;
            bool found = _db != null && powerId > 0 && _db.TryGet(powerId, out power);

            if (_powerBlock != null)
                _powerBlock.style.display = found ? DisplayStyle.Flex : DisplayStyle.None;
            if (_powerName != null) _powerName.text = found ? power.Name : "";
            if (_powerDesc != null) _powerDesc.text = found ? CardTextStyling.Style(power.Text) : "";
        }

        /// <summary>The confirm view, reached once both classes are picked.</summary>
        private void RefreshConfirm()
        {
            if (_picks.Count < 2) { ShowView(CreateView.Picker); return; }

            CardClass primary = _picks[0];
            CardClass secondary = _picks[1];
            if (_confirmSummary != null)
                _confirmSummary.text =
                    $"{Pretty(primary)} + {Pretty(secondary)} — {Pretty(primary)}'s Hero Power, cards " +
                    "from both. Choose a name to continue.";
            SetNameError("");
        }

        /// <summary>"Change something" drops the SECONDARY pick only and returns to the grid on
        /// pass 2 (NOR-278). The primary badge stays selected and manually toggleable.</summary>
        private void ChangeSecondary()
        {
            if (_picks.Count == 2) _picks.RemoveAt(1);
            ShowView(CreateView.Picker);
        }

        /// <summary>
        /// Create the character from the two picks and the confirmed name, then go STRAIGHT INTO
        /// THE DECK BUILDER on a new blank deck.
        ///
        /// <para><b>The name carries VULN-002's validation with it.</b> <see cref="PlayerName.Check"/>
        /// is the same predicate the server's <c>MatchGate</c> enforces on the wire, so this reuses
        /// that gate rather than re-implementing it — a bare text field with no check is exactly what
        /// the vulnerability index forbids. The character is created NAMED (its name is immutable),
        /// which is why the field lives here at the one moment the character is constructed rather
        /// than as a deferred rename surface.</para>
        ///
        /// <para><b>A deck, and no stop at the roster</b> (#204). This used to land the character as
        /// a lone "+" row and return here, so creating one meant scrolling back through the roster to
        /// find it and entering the deck builder by hand — "it's assumed that you would like to enter
        /// the deck" (Sam). It now mints a blank deck, sets it as the default, and hands off through
        /// <see cref="OpenDeckForEdit"/>, which parks CharacterRoster as the return scene so Back
        /// still comes here. <c>CreateCharacterWithoutDeck</c> keeps its name because the account API
        /// still constructs the two separately; only this flow joins them.</para>
        /// </summary>
        private void CommitCreate()
        {
            if (_account == null || _picks.Count < 2) return;

            NameCheck name = PlayerName.Check(_nameField?.value ?? "");
            if (!name.IsValid)
            {
                SetNameError(name.Reason);
                return;
            }

            CardClass primary = _picks[0];
            CardClass secondary = _picks[1];

            try
            {
                Character created = _account.CreateCharacterWithoutDeck(name.Normalized, primary, secondary);
                Debug.Log($"[CharacterCreate] created character id={created.Id} " +
                          $"pairing={created.PrimaryClass}/{created.SecondaryClass} (no deck)");
                DeckList newDeck = _account.CreateDeck(created, _db, UnusedDeckName(created), StarterDeck.Blank);
                created.DefaultDeckId = newDeck.Id;
                _account.Save(created);
                Debug.Log($"[CharacterCreate] created blank deck '{newDeck.Name}' for {created.Name}; opening deck builder.");
                OpenDeckForEdit(created.Id, newDeck.Id);
            }
            catch (ArgumentException ex)
            {
                Debug.LogError($"[CharacterCreate] character creation failed: {ex.Message}");
                SetNameError(ex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterCreate] character creation could not be saved: {ex.Message}");
                SetNameError("Character could not be saved — see the console.");
            }
        }

        private void SetNameError(string message)
        {
            if (_nameError == null) return;
            _nameError.text = message ?? "";
            Show(_nameError, !string.IsNullOrEmpty(message));
        }

        private static void Show(VisualElement element, bool visible)
        {
            if (element == null) return;
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string Pretty(CardClass c)
        {
            string raw = c.ToString().ToLowerInvariant();
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        private static string ClassSlug(CardClass c) => c.ToString().ToLowerInvariant();

        // ---- Ported with the flow (#161/#250): CommitCreate hands the new character
        // straight to the deck builder, which is the behaviour the modal had. Both of these
        // were CharacterRosterController's and stay identical to it on purpose.

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

        private static void Go(string sceneName)
        {
            if (!UnityEngine.Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[CharacterCreate] Scene '{sceneName}' is not in the build list. " +
                               "Run Emberfall/Build/Sync Scene List, and remember that menu item does " +
                               "not reach EditorBuildSettings.asset on disk — hand-edit it to match.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }
}
