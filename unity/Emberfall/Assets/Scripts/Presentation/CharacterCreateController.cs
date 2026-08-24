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
    /// Character creation, as a SCENE (Sam, 2026-08-23). Three sequential views over one portrait
    /// grid: pick the primary class (its Hero Power), pick the secondary (cards only), then name
    /// the character and create it with NO deck — it lands in the roster as a lone "+" row, the
    /// same empty state a character whose decks were all deleted already shows.
    ///
    /// <para>It was a <c>.delete-modal</c> overlay inside <c>CharacterRosterScreen.uxml</c> until
    /// then. Same call the Store got on 2026-08-20 and for the same reason: full-screen, opaque,
    /// and a destination — the overlay was hosting rather than design.</para>
    ///
    /// <para><b>Why the move was forced, and what it actually buys.</b> At <c>referenceDpi: 160</c>
    /// a landscape panel is 411dp. As a modal this flow sat inside three nested paddings —
    /// <c>.emb-bleed</c>'s 52dp hardware clearance per edge, <c>.delete-dialog</c>'s 24dp design
    /// gutter per edge, and the screen's own — so roughly 152dp of 411 was spent before the title
    /// drew. Measured on a Pixel 5a 2026-08-23: the pass title rendered under the status bar,
    /// Cancel under the gesture bar, and <see cref="LayoutHeroGrid"/>'s column solve found no
    /// shape that fit at all. Here the root IS the panel, with one clearance and nothing nested
    /// inside it.</para>
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
        private enum CreateView { Grid, Detail, Confirm }

        private VisualElement _root;
        private PlayerAccount _account;
        private ICardDatabase _db;
        private IArtProvider _art;
        private string _returnScene = "CharacterRoster";

        private readonly List<CardClass> _picks = new List<CardClass>(2);
        private CardClass _detailClass;

        private VisualElement _gridView, _detailView, _confirmView;
        private VisualElement _heroGrid, _detailPortrait, _detailBullets, _powerBlock;
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
            ShowView(CreateView.Grid);
        }

        private void CacheElements()
        {
            _gridView = _root.Q<VisualElement>("create-grid-view");
            _detailView = _root.Q<VisualElement>("create-detail-view");
            _confirmView = _root.Q<VisualElement>("create-confirm-view");

            _passTitle = _root.Q<Label>("create-pass-title");
            _passHelp = _root.Q<Label>("create-pass-help");
            _heroGrid = _root.Q<VisualElement>("create-hero-grid");

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

            // The grid has no size until the scene's panel lays out, and it changes again on
            // rotation. Re-solving on its own geometry is what makes the first frame land at the
            // right size rather than at the stylesheet's.
            _heroGrid?.RegisterCallback<GeometryChangedEvent>(_ => QueueHeroGridLayout());
        }

        private void Wire()
        {
            _root.Q<Button>("create-cancel")?.RegisterCallback<ClickEvent>(_ => Go(_returnScene));
            _root.Q<Button>("create-detail-back")?.RegisterCallback<ClickEvent>(_ => ShowView(CreateView.Grid));
            _detailSelect?.RegisterCallback<ClickEvent>(_ => TogglePick(_detailClass));
            _root.Q<Button>("create-change")?.RegisterCallback<ClickEvent>(_ => ChangeSecondary());
            _root.Q<Button>("create-continue")?.RegisterCallback<ClickEvent>(_ => CommitCreate());
            _nameField?.RegisterValueChangedCallback(_ => SetNameError(""));
        }

        /// <summary>True while pass 1 is live — nothing picked yet, so the next pick is the primary.</summary>
        private bool ChoosingPrimary => _picks.Count == 0;

        private void ShowView(CreateView view)
        {
            Show(_gridView, view == CreateView.Grid);
            Show(_detailView, view == CreateView.Detail);
            Show(_confirmView, view == CreateView.Confirm);

            if (view == CreateView.Grid) BuildHeroGrid();
            else if (view == CreateView.Confirm) RefreshConfirm();
        }

        /// <summary>
        /// The portrait grid, rebuilt each time it shows so every badge reflects the current picks.
        /// The title and help above it are pass-specific: pass 1 says this pick locks in the hero
        /// power; pass 2 says it only adds cards.
        /// </summary>
        private void BuildHeroGrid()
        {
            if (_heroGrid == null) return;
            _heroGrid.Clear();

            if (ChoosingPrimary)
            {
                if (_passTitle != null) _passTitle.text = "Choose your hero";
                if (_passHelp != null)
                    _passHelp.text = "Hero Power from your first choice — Cards from both.";
            }
            else
            {
                if (_passTitle != null) _passTitle.text = "Choose a second class";
                if (_passHelp != null)
                    _passHelp.text = "This pick adds a second class's cards to your pool.";
            }

            foreach (CardClass c in PairableClasses)
                _heroGrid.Add(BuildHeroTile(c));

            QueueHeroGridLayout();
        }

        /// <summary>Must match .hero-tile's margin (--space-2), both sides of a tile.</summary>
        private const float HeroGutter = 16f;

        /// <summary>.hero-tile padding (--space-2) plus its 2px border, both sides — the difference
        /// between the tile's outer width and the square portrait inside it.</summary>
        private const float HeroChrome = 20f;

        /// <summary>The name band under the portrait: --type-body plus its --space-2 margin, with
        /// room for a descender.</summary>
        private const float HeroNameBand = 36f;

        /// <summary>Floor for the solved portrait side, in dp. Only reached when no column count
        /// fits the box at all. 44 rather than Android's 48 touch floor because the TILE is the tap
        /// target and it is the portrait plus <see cref="HeroChrome"/>: 44 + 20 = 64dp of target.</summary>
        private const float MinHeroSide = 44f;

        private const float RowSafetyMargin = 2f;

        private bool _heroLayoutQueued;

        /// <summary>
        /// Deferred because the trigger is the grid's own GeometryChangedEvent, which the layout
        /// engine fires mid-pass, and writing sizes from inside it edits the tree while that pass
        /// is still walking it.
        /// </summary>
        private void QueueHeroGridLayout()
        {
            if (_heroGrid == null || _heroLayoutQueued) return;
            _heroLayoutQueued = true;
            _heroGrid.schedule.Execute(() =>
            {
                _heroLayoutQueued = false;
                LayoutHeroGrid();
            });
        }

        /// <summary>
        /// Grow the portraits into whatever space the full-screen overlay has (NOR-288). The tiles
        /// used to be a fixed 152px inside a 760px dialog, which on a phone in landscape drew eight
        /// small pictures in the middle of a mostly empty screen. Every column count is tried and
        /// the one yielding the biggest portrait wins; within each, both axes are solved and the
        /// smaller wins, so the grid always fits without scrolling. The chosen shape is then
        /// written to the grid's width, because .hero-grid wraps on width and would otherwise be
        /// free to seat a different number per row than was solved for.
        /// </summary>
        private void LayoutHeroGrid()
        {
            if (_heroGrid == null) return;

            // Width comes from the PARENT, never from the grid. This method writes the grid's own
            // width, so measuring the grid would feed its output back into its input and walk the
            // tiles a little smaller on every layout pass.
            VisualElement box = _heroGrid.parent ?? _heroGrid;
            float availableWidth = box.resolvedStyle.width - RowSafetyMargin;
            float availableHeight = _heroGrid.resolvedStyle.height - RowSafetyMargin;
            if (availableWidth <= 0f || availableHeight <= 0f) return;

            int tiles = 0;
            VisualElement sample = null;
            foreach (VisualElement child in _heroGrid.Children())
                if (child.ClassListContains("hero-tile")) { tiles++; sample ??= child; }
            if (tiles == 0) return;

            // MEASURE THE CHROME, DO NOT ASSUME IT. UI Toolkit snaps margins, padding and borders
            // to whole DEVICE pixels, so an authored dp value is not what the tile actually gets:
            // at the phone's 2.625x scale a 4dp margin is 10.5 device px, rounds to 11, and comes
            // back as 4.190dp. Eight tiles then advance 101.333 each and need 810.667 of a
            // 809.905 grid — SHORT BY 0.76 — so the row wraps to seven plus one, and .hero-grid's
            // overflow:hidden clips the first row's tops. That is exactly the 7+1 failure
            // described above the constants, arriving by a second route.
            //
            // Reading it off a real tile also retires the "must match the USS" coupling those two
            // constants carry: they are a FALLBACK for the first pass, before any tile has a
            // resolved style, and no longer a second source of truth that can drift.
            float gutter = HeroGutter;
            float chrome = HeroChrome;
            if (sample != null && sample.resolvedStyle.marginLeft > 0f)
            {
                gutter = sample.resolvedStyle.marginLeft + sample.resolvedStyle.marginRight;
                chrome = sample.resolvedStyle.paddingLeft + sample.resolvedStyle.paddingRight
                       + sample.resolvedStyle.borderLeftWidth + sample.resolvedStyle.borderRightWidth;
            }

            int columns = 1;
            float side = 0f;
            for (int c = 1; c <= tiles; c++)
            {
                int r = Mathf.CeilToInt(tiles / (float)c);
                float fromWidth = availableWidth / c - gutter - chrome;
                float fromHeight = availableHeight / r - gutter - chrome - HeroNameBand;
                float candidate = Mathf.Min(fromWidth, fromHeight);
                if (candidate <= side) continue;
                side = candidate;
                columns = c;
            }
            if (side <= 0f) return;

            // Pin the wrap to the shape that was solved for. Without this the grid is free to seat
            // a different number of tiles per row than the solve assumed — and that is precisely
            // the failure documented above the constants.
            _heroGrid.style.width = columns * (side + chrome + gutter) + RowSafetyMargin;
            _heroGrid.style.alignSelf = Align.Center;

            foreach (VisualElement child in _heroGrid.Children())
            {
                if (!child.ClassListContains("hero-tile")) continue;
                child.style.width = side + chrome;

                VisualElement art = child.Q<VisualElement>(className: "hero-tile-art");
                if (art == null) continue;
                art.style.width = side;
                art.style.height = side;   // square: the whole point of the pass
            }
        }

        /// <summary>
        /// One portrait tile with two independent tap targets: the tile body opens the hero's detail
        /// overlay, and the corner badge toggles selection. The badge stops the click from reaching
        /// the tile so the two never fire together.
        /// </summary>
        private VisualElement BuildHeroTile(CardClass heroClass)
        {
            int order = _picks.IndexOf(heroClass);   // -1 = unpicked, 0 = primary, 1 = secondary
            bool selected = order >= 0;

            var tile = new VisualElement();
            tile.AddToClassList("hero-tile");
            tile.EnableInClassList("hero-tile--selected", selected);

            var art = new VisualElement();
            art.AddToClassList("hero-tile-art");
            _art.LoadArt("hero_" + ClassSlug(heroClass), sprite =>
            {
                if (sprite != null) art.style.backgroundImage = new StyleBackground(sprite);
            });
            tile.Add(art);

            // The badge, top-left, in the mana-frame treatment (NOR-278). Empty when unselected
            // but still the toggle target; filled with the pick order when on. A plain VisualElement
            // with StopPropagation rather than a Button, so its click cannot also open the detail.
            //
            // TWO ELEMENTS: an invisible 48dp box that catches the tap, and a 28dp disc inside it
            // that is what you see. The portrait underneath is only 64dp square on a landscape
            // phone, so a 48dp DISC would cover three quarters of the hero's face — it did, and it
            // read on device as the portrait failing to load. The touch floor and the visual want
            // different sizes, so they get different elements. See .hero-tile-badge in the sheet.
            var badge = new VisualElement();
            badge.AddToClassList("hero-tile-badge");
            badge.EnableInClassList("hero-tile-badge--on", selected);
            var badgeDisc = new VisualElement();
            badgeDisc.AddToClassList("hero-tile-badge__disc");
            var badgeLabel = new Label(selected ? (order + 1).ToString() : "");
            badgeDisc.Add(badgeLabel);
            badge.Add(badgeDisc);
            badge.tooltip = selected ? "Deselect" : "Select";
            badge.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                TogglePick(heroClass);
            });
            tile.Add(badge);

            var name = new Label(Pretty(heroClass));
            name.AddToClassList("hero-tile-name");
            tile.Add(name);

            tile.RegisterCallback<ClickEvent>(_ => OpenHeroDetail(heroClass));
            return tile;
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

            ShowView(_picks.Count == 2 ? CreateView.Confirm : CreateView.Grid);
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
            if (_picks.Count < 2) { ShowView(CreateView.Grid); return; }

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
            ShowView(CreateView.Grid);
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
