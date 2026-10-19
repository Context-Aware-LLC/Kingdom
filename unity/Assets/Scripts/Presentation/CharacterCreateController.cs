using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Collection;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Deck creation, as a scene. Three sequential views over one class picker: pick the primary
    /// class (its Hero Power), pick the secondary (cards only), then choose whether the deck arrives
    /// generated or blank — and the deck is created carrying that pairing for good.
    ///
    /// <para><b>This screen created CHARACTERS until 2026-09-04 (ADR-0009).</b> The ADR listed it as
    /// deleted outright, and that was wrong in one respect the ADR's own ruling 1 makes plain:
    /// <i>"a deck's pairing is immutable, so New Deck opens on a class picker."</i> This screen IS
    /// that picker. Deleting it would have meant rebuilding the class plates (#312), the hero-power
    /// block read off the power card (NOR-291) and the trait bullets (NOR-281) inside a modal pane.
    /// So the flow was repurposed rather than replaced: the two passes and everything they draw are
    /// unchanged, and only the thing at the end of them is different.</para>
    ///
    /// <para><b>The name field is gone with the character.</b> A character was named at creation
    /// because its name was immutable; a deck's is not, and the roster renames one from its own
    /// plate. Asking for it here would be a form standing between the player and a playable deck,
    /// which is the shape this flow exists to avoid. The new deck takes <c>New Deck</c>, then
    /// <c>New Deck 2</c>.</para>
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
        public static string ReturnScene = ScreenNav.CharacterRoster;

        /// <summary>
        /// Every real class is pairable; NEUTRAL is not a class a deck can be built on. A pairing
        /// whose pool is too thin to generate a 30 is caught on the confirm view by
        /// <see cref="PlayerAccount.CanCreate"/>, which disables Generate and leaves Blank — thin is
        /// a reason to build by hand, never a reason to refuse the pairing.
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
        private Label _detailName, _detailPass;
        private Label _powerName, _powerDesc, _confirmSummary, _createError;
        private Button _detailSelect, _createGenerated, _createBlank, _cancel;
        private Label _starterNote;
        private FirstRunTidbit _tidbit;
        private bool _creating;
        private DeckList _pendingDeck;
        private StarterDeck _pendingKind;
        private int _visit;

        private void OnDisable() => _visit++;

        private void OnEnable()
        {
            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "CharacterRoster" : ReturnScene;
            ReturnScene = ScreenNav.CharacterRoster;

            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[DeckCreate] No rootVisualElement — is the UXML assigned?");
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
                Debug.LogError($"[DeckCreate] could not open this device's collection: {ex.Message}");
                SetCreateError("Collection unavailable — see the console.");
                return;
            }

            _picks.Clear();
            SetCreateError("");
            ShowView(CreateView.Picker);

            // THE ONE TIDBIT THAT SHIPS (spec §4, slice 4). It fires BEFORE the first pick, not
            // after, because it explains the choice this screen is about to ask for — the two tail
            // tidbits sit after the gift for the mirror-image reason, and are still held.
            //
            // Deliberately below the two early returns above: a player looking at "Collection
            // unavailable" has a problem to read, not a lesson. FirstRunTidbit itself decides
            // whether there is anything to show, so a returning player sees nothing and no marker
            // is consulted here.
            _tidbit = new FirstRunTidbit(_root);
            _tidbit.Show(TidbitTrigger.CharacterCreateArrival);
        }

        private void CacheElements()
        {
            _pickerView = _root.Q<VisualElement>("create-picker-view");
            _cancel = _root.Q<Button>("create-cancel");
            _detailView = _root.Q<VisualElement>("create-detail-view");
            _confirmView = _root.Q<VisualElement>("create-confirm-view");

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
            _starterNote = _root.Q<Label>("create-starter-note");
            _createGenerated = _root.Q<Button>("create-generated");
            _createBlank = _root.Q<Button>("create-blank");
            _createError = _root.Q<Label>("create-error");
        }

        private void Wire()
        {
            _root.Q<Button>("create-cancel")?.RegisterCallback<ClickEvent>(_ => ScreenNav.Go(_returnScene));
            _root.Q<Button>("create-detail-back")?.RegisterCallback<ClickEvent>(_ => ShowView(CreateView.Picker));
            _detailSelect?.RegisterCallback<ClickEvent>(_ => TogglePick(_detailClass));
            _root.Q<Button>("create-change")?.RegisterCallback<ClickEvent>(_ => ChangeSecondary());
            _createGenerated?.RegisterCallback<ClickEvent>(_ => CommitCreate(StarterDeck.Generated));
            _createBlank?.RegisterCallback<ClickEvent>(_ => CommitCreate(StarterDeck.Blank));
        }

        /// <summary>True while pass 1 is live — nothing picked yet, so the next pick is the primary.</summary>
        private bool ChoosingPrimary => _picks.Count == 0;

        private void ShowView(CreateView view)
        {
            Show(_pickerView, view == CreateView.Picker);
            // The corner Back lives on the root, not in the picker view, so it hides with it here.
            Show(_cancel, view == CreateView.Picker);
            Show(_detailView, view == CreateView.Detail);
            Show(_confirmView, view == CreateView.Confirm);

            if (view == CreateView.Picker) BuildHeroChoices();
            else if (view == CreateView.Confirm) RefreshConfirm();
        }

        /// <summary>
        /// The button picker, rebuilt each time it shows so the primary choice stays visible.
        /// </summary>
        private void BuildHeroChoices()
        {
            if (_heroColumnLeft == null || _heroColumnRight == null) return;
            _heroColumnLeft.Clear();
            _heroColumnRight.Clear();

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
            string prettyName = CardClassTokens.Label(heroClass);

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

            // ONE square portrait, and no full-plate wash behind it. The wash was built, rendered
            // and cut the same day — see the note in character-roster.uss for what it looked like
            // at 2400x1080 and why "the class art as the ground" is the wrong shape for a 5:1
            // plate. The square crop is the whole of a square source image, so every class gets a
            // face rather than a torso.
            var portrait = new VisualElement { pickingMode = PickingMode.Ignore };
            portrait.AddToClassList("hero-choice-portrait");
            plate.Add(portrait);

            _art.LoadArt("hero_" + CardClassTokens.Slug(heroClass), sprite =>
            {
                if (sprite != null) portrait.style.backgroundImage = new StyleBackground(sprite);
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

            // The hero power's own socket ring, holding the class's hero-power art (#312). Both
            // halves already existed and neither was reachable from this screen: the ring is the
            // IronEmber cut Sam split out of the board's hero frame, and the eight glyphs are the
            // collection class rail's. The ring is the board's vocabulary, which is the point —
            // this is where you choose the power you then look at inside that frame all match.
            //
            // THE GLYPH'S CLASS COMES FROM theme.uss, NOT FROM A MAP HERE. The file names do not
            // match the enum (TEMPEST is storm_hp, CLOCKWRIGHT is clock_hp), and that map already
            // had one home; a second copy in C# is how the two drift apart. A class with no row
            // there draws an empty ring rather than the wrong power.
            var ring = new VisualElement { pickingMode = PickingMode.Ignore };
            ring.AddToClassList("hero-choice-ring");
            var glyph = new VisualElement { pickingMode = PickingMode.Ignore };
            glyph.AddToClassList("hero-choice-glyph");
            glyph.AddToClassList("class-tab_art-" + heroClass.ToString().ToLowerInvariant());
            ring.Add(glyph);
            plate.Add(ring);

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
        /// Add or remove a class from the picks. Selecting appends (primary first, then secondary);
        /// re-selecting a picked class removes it. The moment both slots are full the flow
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
        /// The per-class detail view: portrait, name, a pass-specific line, the class's hero power,
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
                _art.LoadArt("hero_" + CardClassTokens.Slug(heroClass), sprite =>
                {
                    if (sprite != null) _detailPortrait.style.backgroundImage = new StyleBackground(sprite);
                });
            }
            if (_detailName != null) _detailName.text = CardClassTokens.Label(heroClass);
            if (_detailPass != null)
                _detailPass.text = ChoosingPrimary
                    ? "Selecting this class locks in its Hero Power."
                    : $"Selecting this class adds its cards to your pool. Your Hero Power stays {CardClassTokens.Label(_picks[0])}'s.";

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

        /// <summary>
        /// The confirm view, reached once both classes are picked. It states the pairing and offers
        /// the two starter shapes (§2.11).
        ///
        /// <para><b>The first deck is not asked</b> — <see cref="DeckCreation.OffersStarterChoice"/>
        /// is false for an account with none, because a player with nothing has nothing to build
        /// from and an empty deck is a worse introduction than a playable one. Blank is hidden
        /// rather than disabled in that case: a control that can never be pressed on this visit is
        /// a question the player has to answer and then discover was not being asked.</para>
        ///
        /// <para><b>Generated is disabled, never hidden, when the pool is thin.</b> That is a fact
        /// about the pairing the player just chose, and they can act on it by going back — so it
        /// has to stay visible with the reason beside it.</para>
        /// </summary>
        private void RefreshConfirm()
        {
            if (_picks.Count < 2) { ShowView(CreateView.Picker); return; }

            CardClass primary = _picks[0];
            CardClass secondary = _picks[1];
            SetCreateError("");

            if (_confirmSummary != null)
                _confirmSummary.text =
                    $"{CardClassTokens.Label(primary)} + {CardClassTokens.Label(secondary)} — {CardClassTokens.Label(primary)}'s Hero Power, cards " +
                    "from both. This pairing is permanent.";

            bool offersChoice = _account != null && _account.OffersStarterChoice();
            bool canGenerate = _account != null
                               && PlayerAccount.CanCreate(primary, secondary, _db, _account.OwnedCards());

            Show(_createBlank, offersChoice);
            _createGenerated?.SetEnabled(canGenerate);
            if (_createGenerated != null)
                _createGenerated.text = offersChoice ? "Generate a queue-ready deck" : "Build my first deck";

            if (_starterNote != null)
                _starterNote.text = !canGenerate
                    ? $"{CardClassTokens.Label(primary)} + {CardClassTokens.Label(secondary)} doesn't have enough cards for a full deck " +
                      "yet, so only a blank draft is available."
                    : offersChoice
                        ? "A generated deck is legal and playable straight away. A blank one is yours to build."
                        : "";

            // A first deck on a pairing too thin to generate would leave this view with no live
            // control at all. Blank is the honest way out of that, so it comes back.
            if (!canGenerate) Show(_createBlank, true);
        }

        /// <summary>"Change something" drops the SECONDARY pick only and returns to the grid on
        /// pass 2 (NOR-278). The primary badge stays selected and manually toggleable.</summary>
        private void ChangeSecondary()
        {
            if (_picks.Count == 2) _picks.RemoveAt(1);
            ShowView(CreateView.Picker);
        }

        /// <summary>
        /// Create the deck from the two picks and go STRAIGHT INTO THE DECK BUILDER.
        ///
        /// <para>The server accepts the new deck before it enters the cache or opens the builder.
        /// Its deck limit is authoritative; failures stay on this view with a retryable candidate.</para>
        ///
        /// <para><b>It goes to the deck builder even for a generated deck.</b> A generated deck needs
        /// no editing, but the player asked for a deck and landing back on a roster to find it is the
        /// detour #204 removed from the old flow.</para>
        /// </summary>
        private async void CommitCreate(StarterDeck kind)
        {
            if (_creating || _account == null || _picks.Count < 2) return;

            CardClass primary = _picks[0];
            CardClass secondary = _picks[1];
            int visit = _visit;
            PlayerAccount account = _account;
            _creating = true;
            _root.SetEnabled(false);
            SetCreateError("Creating deck…");

            try
            {
                if (_pendingDeck == null || _pendingKind != kind ||
                    _pendingDeck.PrimaryClass != primary || _pendingDeck.SecondaryClass != secondary)
                {
                    _pendingDeck = account.PrepareDeck(_db, UnusedDeckName(primary, secondary), primary, secondary, kind);
                    _pendingKind = kind;
                }
                // Keep this candidate's id on retry: a lost response may still mean the PUT landed.
                DeckList created = _pendingDeck;
                RosterWriteStatus status = await account.ConfirmNewDeckAsync(RosterGateway.AccessToken, created);
                if (this == null || visit != _visit || !ReferenceEquals(account, PlayerAccount.Local)) return;
                if (status != RosterWriteStatus.Ok)
                {
                    // Unlike RosterGateway.PushAsync, ConfirmNewDeckAsync is a bare passthrough with no
                    // logging of its own — every non-Ok status reached this screen and nothing else,
                    // so a real failure and "no console line at all" were the same symptom (#361).
                    Debug.LogWarning($"[DeckCreate] server did not confirm the new deck ({status}).");
                    SetCreateError(status switch
                    {
                        RosterWriteStatus.Unauthenticated => "Sign in again to create a deck.",
                        RosterWriteStatus.Conflict => $"The server could not add this deck. You may have reached the {DeckCreation.MaxPerAccount}-deck limit. Return to your decks and try again.",
                        RosterWriteStatus.Rejected => "The server could not accept this deck. Choose your classes again.",
                        _ => "Could not confirm your deck with the server. Check your connection and try again."
                    });
                    return;
                }
                _pendingDeck = null;
                Debug.Log($"[DeckCreate] created {kind} deck '{created.Name}' " +
                          $"pairing={created.PrimaryClass}/{created.SecondaryClass} " +
                          $"({created.TotalCount} cards); opening deck builder.");
                OpenDeckForEdit(created.Id);
            }
            catch (DeckGenerationException ex)
            {
                // Reachable despite CanCreate: ownership can change under a screen the player left
                // open, and the generator is the only thing that knows for certain.
                Debug.LogError($"[DeckCreate] could not generate a deck: {ex.Message}");
                SetCreateError("That pairing can't fill a deck right now. Start blank instead.");
                Show(_createBlank, true);
            }
            catch (ArgumentException ex)
            {
                Debug.LogError($"[DeckCreate] deck creation refused: {ex.Message}");
                SetCreateError(ex.Message);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeckCreate] deck creation could not be saved: {ex.Message}");
                if (this != null && visit == _visit)
                    SetCreateError("Could not finish creating your deck. Try again, or sign in again to refresh your decks.");
            }
            finally
            {
                _creating = false;
                if (this != null && visit == _visit) _root.SetEnabled(true);
            }
        }

        private void SetCreateError(string message)
        {
            if (_createError == null) return;
            _createError.text = message ?? "";
            Show(_createError, !string.IsNullOrEmpty(message));
        }

        private static void Show(VisualElement element, bool visible)
        {
            if (element == null) return;
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        }


        /// <summary>The pairing's name, then "Compost II" — a duplicate name is legal but
        /// indistinguishable in a grid of otherwise identical card backs. Account-wide.</summary>
        private string UnusedDeckName(CardClass primary, CardClass secondary)
        {
            var taken = new List<string>();
            foreach (DeckList deck in _account.Decks())
                taken.Add(deck.Name);
            return DeckNames.Unused(primary, secondary, taken);
        }

        // Both doors lead to one controller (#152) in one scene (#154): handing it a deck is what
        // puts it in deckbuild mode, and View Collection hands it none. Both call ScreenNav.Go(ScreenNav.Collection);
        // the only difference between the two doors is the pending deck.
        private void OpenDeckForEdit(string deckId)
        {
            CollectionController.ReturnScene = ScreenNav.CharacterRoster;
            CollectionController.PendingDeckId = deckId;
            ScreenNav.Go(ScreenNav.Collection);
        }
    }
}
