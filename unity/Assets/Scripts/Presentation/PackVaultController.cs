using System;
using System.Collections.Generic;
using System.Threading;
using Kingdom.Net.Client.Collection;
using Kingdom.Net.Client.Shop;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Which of the vault's two faces is being shown. <b>One scene, not two</b> — two would mean two
    /// copies of the tear, the reveal and the balance read, and would put the "congratulations" beat
    /// somewhere a returning player could route back into.
    /// </summary>
    public enum PackVaultMode
    {
        /// <summary>The unopened-pack surface: the rack immediately, Back always, no banner.</summary>
        Vault = 0,

        /// <summary>The onboarding gift: a banner, a scripted entrance, and no way out until the
        /// first pack is open.</summary>
        Gift = 1,
    }

    /// <summary>
    /// The pack vault: every unopened pack this player holds, and the verb that spends them.
    /// Slices 1 and 2 of <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>, reshaped into a
    /// rack, a stage and a rail on 2026-09-27 (Sam).
    ///
    /// <para><b>The layout.</b> A RACK down the left holds one slot per pack LINE — a miniature of
    /// the expansion's pack, its name, and a count bubble — so five expansions fit however many packs
    /// each holds. (It was a tile per pack, which at forty packs drew three rows it could not scroll
    /// to.) The RAIL is the rack's narrow right edge, where a pack is held to count up a stack. The
    /// STAGE in the middle is where packs open. <see cref="PackRackDrag"/> owns the gesture; a tap
    /// still opens, so a broken drag never strands a pack.</para>
    ///
    /// <para><b>The pack that tears is the one the player is holding</b> (Sam, 2026-09-03, carried
    /// over). The ruling was "no stage that re-renders a copy of a pack the player already touched";
    /// here the player carries the pack onto the stage themselves, so the pack on the stage IS the
    /// one they dropped, and it tears where they put it.</para>
    ///
    /// <para><b>Several packs are several opens.</b> The server opens one pack per request and a
    /// stack is a loop of them, one at a time, stopping at the first that does not come back with a
    /// receipt — never retrying one (see <see cref="PackGateway"/>: a retry spends a second pack).
    /// <see cref="PackRackDrag.MaxBatch"/> bounds the wait.</para>
    ///
    /// <para><b>The reveal is still <see cref="PackRevealPanel"/></b>, which queries its
    /// <c>reveal-*</c> elements out of whatever root it is handed. One pack deals its row as before;
    /// a stack goes through <see cref="PackRevealPanel.ShowMany"/>. Do not write a second reveal.</para>
    ///
    /// <para><b>Two modes, one screen</b> (<see cref="PackVaultMode"/>). <see cref="PackVaultMode.Gift"/>
    /// is the onboarding beat: it opens with a banner and a staggered entrance, it hides Back until a
    /// pack has actually been opened, and it always goes home to the main menu. The mode is set by the
    /// caller (<c>MainMenuController.OnCardVault</c>, which the menu's vault guide holds the player
    /// to) and consumed on arrival.</para>
    ///
    /// <para><b>The BALANCE guides; the MARKER only picks the mode.</b> That split is what makes
    /// starter packs unstrandable, and it is the reason <see cref="FirstRunMarkers.Gift"/> is written
    /// HERE — on the first pack opened — rather than by whatever sent the player in. A player who
    /// force-quits the instant this scene loads has not had their gift yet, and must get it on the
    /// next launch.</para>
    ///
    /// <para><b>Nothing here rolls anything</b> (ADR-0004). The balance comes from
    /// <c>GET /collection</c> and the awards come from the open's receipt; this screen has no opinion
    /// about either.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// PackVaultScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PackVaultController : MonoBehaviour
    {
        /// <summary>Where Back returns to, set by whoever opened the vault — the same
        /// caller-writes-its-own-way-home shape <see cref="CollectionController.ReturnScene"/> and
        /// <see cref="ShopScreenController.ReturnScene"/> use. Consumed on arrival and reset, so a
        /// caller that forgets to set it does not inherit the last one's answer.</summary>
        public static string ReturnScene = ScreenNav.MainMenu;

        /// <summary>A receipt opened somewhere else, to be revealed on arrival. The Shop does not
        /// use this for purchases; it grants unopened inventory instead. Consumed on read so a
        /// handed-in receipt cannot be replayed on a later visit.</summary>
        public static PackOpenReceipt Pending;

        /// <summary>
        /// Which face to wear, set by whoever opened the vault. <b>Consumed on arrival and reset to
        /// <see cref="PackVaultMode.Vault"/></b>, exactly as <see cref="ReturnScene"/> is: the gift is
        /// a once-ever beat, and a caller that forgets to set this must not inherit it from the launch
        /// that did.
        ///
        /// <para>A static rather than a scene reference for the same reason <see cref="Pending"/> is
        /// one — the caller is in another scene and is gone by the time this one enables.</para>
        /// </summary>
        public static PackVaultMode Mode = PackVaultMode.Vault;

        /// <summary>How long after a render the banner settles, and the step between rack slots
        /// behind it. The same shape as <see cref="PackRevealPanel"/>'s card stagger, a little
        /// quicker: these are objects arriving, not things to read.</summary>
        private const int EntranceBannerMs = 60;
        private const int EntranceFirstSlotMs = 220;
        private const int EntranceSlotStepMs = 90;
        private const float LoadingDegreesPerSecond = 36f;

        /// <summary>The starter-pack line the onboarding grant hands over. Opening the last of these is
        /// the end of the gift, whichever face the vault is wearing.
        ///
        /// <para>RETIREMENT TRIGGER: THE STARTER PACK GOES ON SALE. This reads "the starter line hit
        /// zero" as "first run's packs are done", which holds only while the grant is the one way to
        /// hold a starter pack (see <c>OnboardingGrant.StarterPack</c>). Once one can be bought, a
        /// returning player opening one would be sent home to a guided menu: gate
        /// <see cref="FinishGift"/> on the first-run route instead.</para></summary>
        // Qualified, not imported: Kingdom.Core.Collection also declares a PackOpenResult, and a
        // using for it makes every PackOpenResult below ambiguous with the gateway's (CS0104).
        private static readonly string StarterPackId =
            Kingdom.Core.Collection.OnboardingGrant.StarterPack().Id;

        private VisualElement _root;
        private VisualElement _rack;
        private VisualElement _stage;
        private VisualElement _stageHint;
        private VisualElement _rail;
        private Label _status;
        private VisualElement _loading;
        private VisualElement _loadingIcon;
        private IVisualElementScheduledItem _loadingTick;
        private VisualElement _banner;
        private Button _back;
        private PackRevealPanel _reveal;
        private CardFaceBaker _revealFaceBaker;
        private PackTearAnimator _tear;
        private IPackArtProvider _packArt;
        private PackRackDrag _drag;
        private FirstRunTidbit _tidbit;

        /// <summary>True once this visit has opened a pack. An empty rack means "the last pack is
        /// gone" only when it does; a player who arrives at an empty vault has finished nothing.</summary>
        private bool _openedAPack;

        /// <summary>True once this visit has opened a starter pack. The end of the gift is "this visit
        /// opened one and the line is now empty" — never just "the line is empty", which is every
        /// returning player's vault.</summary>
        private bool _openedStarterPack;

        /// <summary>True once <see cref="FinishGift"/> has taken over the screen, so a second render
        /// while its tidbit is up cannot start it twice.</summary>
        private bool _finishingGift;

        private string _returnScene = "MainMenu";

        /// <summary>This visit's mode, read off <see cref="Mode"/> once on arrival.</summary>
        private PackVaultMode _mode = PackVaultMode.Vault;

        /// <summary>
        /// True while the gift is holding the player on this screen — Gift mode, before any pack has
        /// been opened. It hides Back, which is the whole of "no skip on the gift's first pack".
        ///
        /// <para><b>It is released by every dead end, not only by a successful open.</b> The gate
        /// exists to stop a player skipping a pack they have; where there is no pack to open there is
        /// nothing to gate, and a screen with no packs on it and no way off it is a trap. The two
        /// reachable dead ends are a server that refuses the open — <c>Unavailable</c>, which is what
        /// an unset <c>KINGDOM_PACK_SEED_SECRET</c> produces on every request — and a session the
        /// server no longer accepts. <see cref="Fail"/> and a zero-total render both release it.</para>
        /// </summary>
        private bool _giftLocked;

        /// <summary>The entrance is an arrival, so it plays on the first rack this visit draws and
        /// never again — the rack is re-rendered after every open, and re-staggering it would read as
        /// a glitch, not a flourish.</summary>
        private bool _entrancePlayed;

        /// <summary>True while the reveal on screen belongs to somebody else's open. Decides where
        /// dismissing goes, and nothing else.</summary>
        private bool _revealWasHandedIn;

        /// <summary>One open at a time, and no gesture starts while one is in flight. A second open
        /// during the first would spend more packs than the player asked for, which is the one
        /// mistake on this screen that costs the player something real.</summary>
        private bool _busy;

        /// <summary>
        /// The balance the rack last drew, kept so dismissing can redraw it with the opened packs gone
        /// instead of re-reading all of <c>GET /collection</c> — a full round trip, token refresh
        /// included, to learn numbers the receipts already settled.
        ///
        /// <para><b>Not a prediction</b> (ADR-0004): the decrement is applied only on
        /// <see cref="PackOpenStatus.Opened"/>, which is the server saying it spent one pack on that
        /// line. <see cref="PackOpenStatus.AlreadyOpened"/> moved no counter, so it re-reads instead,
        /// as does a stack that stopped short.</para>
        /// </summary>
        private Dictionary<string, long> _shelfBalance;

        /// <summary>True when <see cref="_shelfBalance"/> already reflects the open whose reveal is
        /// on screen, so dismissing may redraw from it without a fetch.</summary>
        private bool _shelfSettled;

        /// <summary>The pack on the stage while an open is in flight, and what tears.</summary>
        private VisualElement _stagePack;

        /// <summary>Picks which of a set's art pieces the next stage pack wears, so consecutive opens
        /// of one line are not the same picture every time.</summary>
        private int _stageArtTurn;

        private CancellationTokenSource _cancel;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[PackVault] No rootVisualElement — is the UXML assigned?");
                return;
            }
            UiLocalizer.Attach(root);
            BackgroundArt.BindTree(root);
            _root = root;

            // Top-level destination — same corner settings gear as the main menu (Sam, 2026-09-05).
            MatchSettingsController.Mount();
            FriendsDrawerController.Mount();

            _mode = Mode;
            Mode = PackVaultMode.Vault;

            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "MainMenu" : ReturnScene;
            ReturnScene = ScreenNav.MainMenu;

            // The gift goes home to the menu whatever the last caller left in ReturnScene. It is the
            // tail of first run, not a detour out of some screen the player chose to be on.
            if (_mode == PackVaultMode.Gift) _returnScene = "MainMenu";

            _rack = root.Q<VisualElement>("vault-rack-slots");
            _stage = root.Q<VisualElement>("vault-stage");
            _stageHint = root.Q<VisualElement>("vault-stage-hint");
            _rail = root.Q<VisualElement>("vault-rail");
            _status = root.Q<Label>("vault-status");
            _loading = root.Q<VisualElement>("vault-loading");
            _loadingIcon = root.Q<VisualElement>("vault-loading-icon");
            if (_loadingIcon != null)
            {
                _loadingTick = _loadingIcon.schedule.Execute(() =>
                    _loadingIcon.style.rotate = new Rotate(new Angle(
                        Time.realtimeSinceStartup * LoadingDegreesPerSecond % 360f)))
                    .Every(16);
                _loadingTick.Pause();
            }
            _banner = root.Q<VisualElement>("vault-banner");
            _packArt = new PackArtProvider();
            _revealFaceBaker = CreateRevealFaceBaker();
            _reveal = new PackRevealPanel(root, RevealDismissed, _revealFaceBaker);
            _tear = new PackTearAnimator();
            _tidbit = new FirstRunTidbit(root);
            _openedAPack = false;
            _openedStarterPack = false;
            _finishingGift = false;
            _cancel = new CancellationTokenSource();

            _drag = new PackRackDrag(root, _stage, _rail, ResolveSource,
                canStart: () => !_busy && _shelfBalance != null,
                held: packId => (int)Math.Min(Held(packId), int.MaxValue),
                art: packId => FaceSpriteOrNull(packId, 0),
                open: (packId, count) => OpenBatch(packId, count));

            _back = root.Q<Button>("btn-vault-back");
            if (_back != null) _back.clicked += Exit;

            _entrancePlayed = false;
            _giftLocked = _mode == PackVaultMode.Gift;
            ApplyGiftLock();

            PackOpenReceipt handedIn = Pending;
            Pending = null;

            if (handedIn != null)
            {
                // Somebody else's open. The rack underneath is never drawn: dismissing leaves, so a fetch
                // would be a round trip to render a screen nobody sees.
                //
                // A handed-in receipt is already open, never the gift, so nothing here is gated:
                // if a caller managed to set both, the receipt wins and the lock comes off with it.
                _revealWasHandedIn = true;
                ReleaseGiftLock();
                SetLoading(false);
                SetStatus("");
                _reveal.Show(handedIn);
                return;
            }

            Refresh();
        }

        private void OnDisable()
        {
            _reveal?.Hide();
            _loadingTick?.Pause();
            _tidbit?.Cancel();
            _tidbit = null;
            _drag?.Dispose();
            _drag = null;

            // A scene change mid-fetch would otherwise land a UI write on a destroyed panel.
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;

            if (_revealFaceBaker != null)
            {
                Destroy(_revealFaceBaker.gameObject);
                _revealFaceBaker = null;
            }
        }

        /// <summary>The batch reveal shows the canonical printed card, not a second art-and-name
        /// imitation. The baker owns an off-camera card rig and a small per-visit texture cache;
        /// failure leaves PackRevealPanel's readable fallback in place.</summary>
        private CardFaceBaker CreateRevealFaceBaker()
        {
            var bakerHost = new GameObject("PackRevealCardFaceBaker");
            bakerHost.transform.SetParent(transform, false);
            var baker = bakerHost.AddComponent<CardFaceBaker>();
            try
            {
                // Large enough that the five-card gallery still downsamples on the widest authored
                // viewport instead of stretching the collection wall's smaller default capture.
                baker.SetFaceSize(384, 538);
                baker.Initialize(CardDatabaseLoader.Load());
                return baker;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackVault] printed card faces unavailable; using reveal fallbacks " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
                Destroy(bakerHost);
                return null;
            }
        }

        // ── The rack ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Read the balance and draw the rack.
        ///
        /// <para><b>async void is correct here</b>, the same rule the menu's bootstrap follows: this is
        /// driven by a Unity lifecycle method and by button callbacks, neither of which can be
        /// awaited, and the try/catch is what keeps it honest.</para>
        /// </summary>
        private async void Refresh()
        {
            CancellationToken token = _cancel?.Token ?? CancellationToken.None;
            try
            {
                SetBusy(true);
                SetStatus("");
                SetLoading(true);
                SetRailVisible(false);

                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!Alive(token)) return;

                if (string.IsNullOrEmpty(accessToken))
                {
                    // TokenForConnectAsync answers the empty string for every failure, including a
                    // refresh that was refused. Both read the same from here: no identity, no rack.
                    Fail(Loc.Get("vault_signed_out"));
                    return;
                }

                CollectionSnapshot snapshot = await PackGateway.ReadShelfAsync(accessToken, token);
                if (!Alive(token)) return;

                switch (snapshot.Status)
                {
                    // Synced carries real pack balances for every account, a superuser's included
                    // (the gift is developed on one).
                    case CollectionSyncStatus.Synced:
                        // Unbusy BEFORE the render, so the rack is drawn live.
                        SetBusy(false);
                        RenderRack(snapshot.Packs);
                        break;

                    case CollectionSyncStatus.Offline:
                        Fail(Loc.Get("vault_offline"));
                        break;

                    case CollectionSyncStatus.Unauthenticated:
                        Fail(Loc.Get("vault_session_expired"));
                        break;

                    case CollectionSyncStatus.Unavailable:
                        Fail(Loc.Get("vault_no_economy"));
                        break;

                    default:
                        Fail(Loc.Get("vault_unreadable"));
                        Debug.LogWarning($"[PackVault] shelf unreadable: {snapshot.Detail}");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // The scene went away mid-fetch. Nothing to report and nothing to draw on.
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackVault] could not read the shelf " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
                if (Alive(token)) Fail(Loc.Get("vault_read_failed"));
            }
            finally
            {
                if (Alive(token))
                {
                    SetLoading(false);
                    SetBusy(false);
                }
            }
        }

        private long Held(string packId)
            => packId != null && _shelfBalance != null && _shelfBalance.TryGetValue(packId, out long n) ? n : 0;

        private void RenderRack(IReadOnlyDictionary<string, long> packs)
        {
            if (_rack == null) return;
            _rack.Clear();
            ClearStagePack();

            _shelfBalance = new Dictionary<string, long>(StringComparer.Ordinal);
            if (packs != null)
                foreach (KeyValuePair<string, long> line in packs) _shelfBalance[line.Key] = line.Value;

            // The last starter pack is open: the gift is over, and first run carries on at the main
            // menu (Sam, 2026-11-04). Checked against the balance being drawn, so it fires on the
            // render after the player dismisses that pack's reveal, never under it.
            if (_openedStarterPack && Held(StarterPackId) <= 0)
            {
                FinishGift();
                return;
            }

            // Ordinal, so the rack does not reorder itself between two reads of the same balance.
            var lines = new List<KeyValuePair<string, long>>();
            long total = 0;
            foreach (KeyValuePair<string, long> line in _shelfBalance)
            {
                if (line.Value <= 0) continue;
                lines.Add(line);
                total += line.Value;
            }
            lines.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            SetRailVisible(total > 0);

            if (total == 0)
            {
                // Nothing to hold the player on, so the gift stops holding them. Reachable in Gift
                // mode: the balance that routed them here was read on another screen, and another
                // device may have spent it since.
                ReleaseGiftLock();
                ShowBanner(false);
                SetStageHint(false);
                SetStatus(Loc.Get("vault_no_packs"));
                // The tail of the gift (spec §4, slice 4). Its one card, "shards", is parked in
                // TidbitCatalog until it moves after the first match, so this shows nothing today.
                if (_openedAPack) _tidbit?.Show(TidbitTrigger.PackVaultGiftComplete);
                return;
            }

            SetStatus("");
            SetStageHint(true);

            foreach (KeyValuePair<string, long> line in lines)
                _rack.Add(BuildSlot(line.Key, line.Value));

            // The banner belongs to the rack, not to the screen: it says "here is a gift", and it
            // must not be the thing on screen while the fetch is still running or after it failed.
            ShowBanner(_mode == PackVaultMode.Gift);
            if (_mode == PackVaultMode.Gift && !_entrancePlayed)
            {
                _entrancePlayed = true;
                PlayEntrance();
            }

            // First look (#403), here rather than in OnEnable because the lesson is about the packs
            // and must not sit over the spinner or over a failed read. The rack redraws after every
            // open; FirstRunTidbit shows nothing once the card has been dismissed.
            _tidbit?.Show(TidbitTrigger.PackVaultArrival);
        }

        /// <summary>
        /// Stagger the banner and the rack in. <b>USS does the animation</b> — every element is built
        /// carrying an <c>--enter</c> class that holds it faded and displaced, and this only schedules
        /// the class coming off, at which point the transition on the base rule runs. Same mechanism as
        /// <see cref="PackRevealPanel"/>'s card stagger: a transition is one declaration and a
        /// hand-driven lerp is a frame loop that has to be cancelled on scene exit.
        ///
        /// <para><c>schedule</c> is owned by the element, so a scene change drops the callbacks with
        /// the panel and there is nothing here to unhook.</para>
        /// </summary>
        private void PlayEntrance()
        {
            if (_banner != null)
                _banner.schedule
                       .Execute(() => _banner.RemoveFromClassList("vault-banner--enter"))
                       .StartingIn(EntranceBannerMs);

            if (_rack == null) return;

            int i = 0;
            foreach (VisualElement child in _rack.Children())
            {
                VisualElement slot = child;
                slot.schedule
                    .Execute(() => slot.RemoveFromClassList("vault-slot--enter"))
                    .StartingIn(EntranceFirstSlotMs + (i * EntranceSlotStepMs));
                i++;
            }
        }

        /// <summary>
        /// One pack LINE: its miniature, its name, and how many are held. <b>A slot per line, not per
        /// pack</b> — the count bubble is what says "ten", so a player's forty packs are five slots
        /// rather than forty tiles the screen cannot scroll to.
        ///
        /// <para>A plain element, not a Button: <see cref="PackRackDrag"/> owns press, drag and tap on
        /// it, and a Button's own clickable would fire a second open on the same tap.</para>
        /// </summary>
        private VisualElement BuildSlot(string packId, long held)
        {
            var slot = new VisualElement { userData = packId };
            slot.AddToClassList("vault-slot");
            slot.SetEnabled(!_busy);
            // Staged displaced, for PlayEntrance to release. Applied at BUILD time because a class
            // added in the same frame the element is created transitions from the staged value.
            if (WillPlayEntrance) slot.AddToClassList("vault-slot--enter");

            slot.Add(BuildMini(packId, held));

            var label = new Label(TokenText.PackName(packId)) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("vault-slot__label");
            slot.Add(label);
            return slot;
        }

        /// <summary>A rack slot's pack miniature with its count bubble.
        /// Everything inside ignores picking so the press lands on the slot that owns it.</summary>
        private VisualElement BuildMini(string packId, long count)
        {
            var mini = new VisualElement { pickingMode = PickingMode.Ignore };
            mini.AddToClassList("vault-mini");

            // A set whose art has not been drawn yet is the normal state (PackArtProvider says so),
            // so an artless pack reads as a pack without a picture, never as a picture that failed.
            Sprite art = FaceSpriteOrNull(packId, 0);
            if (art != null)
            {
                PackWrapperBaker.ApplyTo(mini, art);
                mini.AddToClassList("vault-mini--art");
            }

            var badge = new Label(count.ToString()) { pickingMode = PickingMode.Ignore };
            badge.AddToClassList("vault-count");
            mini.Add(badge);
            return mini;
        }

        /// <summary>What <see cref="PackRackDrag"/> is pressing on: a rack slot, found by walking
        /// up from the event target to the element carrying a pack id.</summary>
        private PackRackDrag.Source ResolveSource(VisualElement target)
        {
            for (VisualElement e = target; e != null && e != _root; e = e.parent)
            {
                if (!(e.userData is string packId)) continue;
                if (e.ClassListContains("vault-slot")) return new PackRackDrag.Source(e, packId);
            }
            return default;
        }

        // ── Opening ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The face a pack of this line wears at art turn <paramref name="turn"/>, or null when the
        /// set has no art.
        ///
        /// <para><see cref="IPackArtProvider.LoadPackArt"/> is synchronous by contract — both the
        /// Addressables and the AssetDatabase path resolve inline, and the callback shape is kept only
        /// for a future streamed loader — which is what makes assigning out of it and returning
        /// legal here.</para>
        /// </summary>
        private Sprite FaceSpriteOrNull(string packId, int turn)
        {
            Sprite face = null;
            _packArt?.LoadPackArt(packId, pieces =>
            {
                if (pieces != null && pieces.Count > 0) face = pieces[turn % pieces.Count].Sprite;
            });
            return face;
        }

        /// <summary>Put a sealed pack of this line in the middle of the stage — the pack the player
        /// just dropped — and return the sprite it wears, for the tear to bake from.</summary>
        private Sprite PlaceStagePack(string packId)
        {
            ClearStagePack();
            SetStageHint(false);
            if (_stage == null) return null;

            _stagePack = new VisualElement { pickingMode = PickingMode.Ignore };
            // .vault-pack__face is what the tear's rules hang off (packvault.uss, THE TEAR).
            _stagePack.AddToClassList("vault-pack__face");
            _stagePack.AddToClassList("vault-stage__pack");

            Sprite art = FaceSpriteOrNull(packId, _stageArtTurn++);
            if (art != null)
            {
                PackWrapperBaker.ApplyTo(_stagePack, art);
                _stagePack.AddToClassList("vault-pack__face--art");
            }
            _stage.Add(_stagePack);
            return art;
        }

        private void ClearStagePack()
        {
            _stagePack?.RemoveFromHierarchy();
            _stagePack = null;
        }

        /// <summary>
        /// Open <paramref name="count"/> packs of one line in one bounded batch request, then tear
        /// the stage pack and reveal everything that came back.
        ///
        /// <para>A stack is one bounded server request. The server authenticates and acquires the
        /// player mutation lock once, then returns every per-pack receipt together. A lost response
        /// is never retried because it may represent several packs already committed; recovery is a
        /// shelf re-read.</para>
        /// </summary>
        private async void OpenBatch(string packId, int count)
        {
            if (_busy) return;
            count = (int)Math.Min(count, Math.Min(Held(packId), PackRackDrag.MaxBatch));
            if (count < 1) return;

            CancellationToken token = _cancel?.Token ?? CancellationToken.None;

            // Set on every path that hands the screen to something else — a reveal, or a re-read. The
            // busy flag belongs to whatever is now driving; clearing it here would re-arm the rack
            // underneath a reveal, and under a Refresh that is still in flight.
            bool retained = false;
            try
            {
                SetBusy(true);
                Sprite art = PlaceStagePack(packId);
                SetStatus("");

                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!Alive(token)) return;

                if (string.IsNullOrEmpty(accessToken))
                {
                    Fail(Loc.Get("vault_open_signed_out"));
                    return;
                }

                var receipts = new List<PackOpenReceipt>(count);
                bool settled = true;
                int openedCount = 0;
                PackOpenResult stopped = null;

                if (count == 1)
                {
                    PackOpenResult result = await PackGateway.OpenAsync(packId, accessToken, token);
                    if (!Alive(token)) return;
                    if (!result.HasReveal)
                        stopped = result;
                    else
                    {
                        receipts.Add(result.Receipt);
                        settled = result.Status == PackOpenStatus.Opened;
                        if (result.Status == PackOpenStatus.Opened) openedCount++;
                    }
                }
                else
                {
                    PackOpenBatchResult result = await PackGateway
                        .OpenManyAsync(packId, count, accessToken, token);
                    if (!Alive(token)) return;
                    if (result.ShouldFallbackToSingleOpens)
                    {
                        // A client may update before its server deployment. A route-level 404 proves
                        // the batch did not spend anything, so the old endpoint is safe here. Keep the
                        // loader as the only waiting feedback, then hand the collected receipts to the
                        // same tear and multi-card reveal used by the batch path.
                        for (int i = 0; i < count; i++)
                        {
                            PackOpenResult fallback = await PackGateway
                                .OpenAsync(packId, accessToken, token);
                            if (!Alive(token)) return;
                            if (!fallback.HasReveal)
                            {
                                stopped = fallback;
                                settled = false;
                                break;
                            }

                            receipts.Add(fallback.Receipt);
                            if (fallback.Status == PackOpenStatus.Opened) openedCount++;
                            else settled = false;
                        }
                    }
                    else if (!result.HasReveals)
                        stopped = PackOpenResult.Failed(result.Status, result.Detail);
                    else
                    {
                        receipts.AddRange(result.Receipts);
                        settled = result.Receipts.Count == count;
                        openedCount = result.Receipts.Count;
                    }
                }

                if (receipts.Count > 0)
                {
                    _shelfBalance[packId] = Math.Max(0, Held(packId) - openedCount);

                    // THE GIFT IS SPENT THE MOMENT A PACK OPENS, not when the player arrived and not
                    // when they leave. Anything earlier loses the gift to a force-quit; anything later
                    // loses it to one. Written on a Vault-mode open too: a player who has opened a
                    // pack has had the moment, whichever door they came through, and marking it here
                    // means there is exactly one site that can.
                    MarkGiftSeen();
                    ReleaseGiftLock();
                    _openedAPack = true;
                    if (string.Equals(packId, StarterPackId, StringComparison.Ordinal))
                        _openedStarterPack = true;
                }

                if (receipts.Count == 0)
                {
                    ClearStagePack();
                    retained = HandleNoReceipt(packId, stopped);
                    return;
                }

                _revealWasHandedIn = false;
                _shelfSettled = settled && stopped == null;
                retained = true;
                SetStatus("");

                string note = receipts.Count == count ? null : Loc.Format("vault_opened_partial", receipts.Count, count);
                if (stopped != null)
                    Debug.LogWarning($"[PackVault] batch stopped at {receipts.Count}/{count}: " +
                                     $"{stopped.Status} {stopped.Detail}");

                // NO SKIP CONTROL, and spec §8 is satisfied by its absence rather than by a flag: the
                // tear is half a second on the pack the player just dropped, with nothing between them
                // and the cards — there is no wait to skip, so there is no control to hide.
                _tear.Play(receipts[0], _stagePack, art, () =>
                {
                    if (receipts.Count == 1 && note == null) _reveal.Show(receipts[0]);
                    else _reveal.ShowMany(receipts, note);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackVault] open threw ({ex.GetType().Name}: {ex.Message}).");
                if (Alive(token)) Fail(Loc.Get("vault_open_failed"));
            }
            finally
            {
                if (Alive(token) && !retained) SetBusy(false);
            }
        }

        /// <summary>The first open of a run came back without a receipt. Returns true when a re-read
        /// has taken over the screen (and with it the busy flag).</summary>
        private bool HandleNoReceipt(string packId, PackOpenResult result)
        {
            switch (result?.Status)
            {
                case PackOpenStatus.NoneOwned:
                    // Honest and reachable: another device opened the last one, or this rack was
                    // drawn before it was. Re-read rather than argue with it.
                    SetStatus(Loc.Get("vault_already_open"));
                    Refresh();
                    return true;

                case PackOpenStatus.Offline:
                    // NOT "nothing happened". The open commits before the response is written, so
                    // this pack may be genuinely spent. Re-reading is the recovery; a retry derives the
                    // next instance id and spends a second pack. Never offer one.
                    Refresh();
                    return true;

                case PackOpenStatus.Unauthenticated:
                    Fail(Loc.Get("vault_open_signed_out"));
                    return false;

                case PackOpenStatus.Unavailable:
                    Fail(Loc.Get("vault_open_unavailable"));
                    Debug.LogWarning($"[PackVault] open unavailable: {result.Detail}");
                    return false;

                case PackOpenStatus.UnknownPack:
                    // A client bug — this rack was drawn from the server's own balance, so a pack
                    // line it will not accept means the two disagree about what a pack is.
                    Fail(Loc.Get("vault_unknown_pack"));
                    Debug.LogError($"[PackVault] server refused pack id '{packId}': {result.Detail}");
                    return false;

                default:
                    // A 2xx that decoded to nothing. The cards may well have been granted, so the
                    // answer is another read, never another open.
                    Debug.LogWarning($"[PackVault] open malformed: {result?.Detail}");
                    Refresh();
                    return true;
            }
        }

        /// <summary>A tap off the cards, on the reveal. A run this screen produced goes back to the rack with the
        /// opened packs gone — drawn from the settled balance when the receipts settled it, re-read
        /// otherwise; a receipt handed in by the store goes back to the store.</summary>
        private void RevealDismissed()
        {
            if (_revealWasHandedIn)
            {
                Exit();
                return;
            }

            if (_shelfSettled)
            {
                _shelfSettled = false;
                // Unbusy BEFORE the render: BuildSlot reads _busy to decide whether a slot is live.
                SetBusy(false);
                RenderRack(_shelfBalance);
                return;
            }
            Refresh();
        }

        /// <summary>
        /// The starter packs are all open. Arm the main menu's collection guide, play the gift's
        /// closing tidbits, then go home to the menu without waiting for Back (Sam, 2026-11-04: "after
        /// a new user opens their grant packs auto navigate back to the menu").
        ///
        /// <para><b>The guide is armed BEFORE the tidbits</b>, for the same force-quit reason the gift
        /// marker is written on the open: a player who quits under the closing scroll has finished
        /// their packs, and the next launch's menu should still point them at Collection.</para>
        ///
        /// <para>Any other packs this player holds stay in the vault, reachable from the menu once the
        /// guide lifts. The gift is the starter line; the rest is not part of first run.</para>
        /// </summary>
        private void FinishGift()
        {
            if (_finishingGift) return;
            _finishingGift = true;

            try
            {
                FirstRunMarkers.Mark(FirstRunMarkers.CollectionGuide);
            }
            catch (Exception ex)
            {
                // Same contract as MarkGiftSeen: a marker that does not land costs the player the
                // guided menu, never the trip home.
                Debug.LogWarning($"[PackVault] could not arm the collection guide " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
            }

            ReleaseGiftLock();
            ShowBanner(false);
            _rack?.Clear();
            ClearStagePack();
            SetStageHint(false);
            SetRailVisible(false);
            SetStatus("");
            SetBusy(true);

            void GoHome()
            {
                if (this != null && isActiveAndEnabled) ScreenNav.Go(ScreenNav.MainMenu);
            }

            // Show calls onDone itself when every card is already seen or the markup is missing, so
            // the trip home never depends on a tidbit being shown.
            if (_tidbit != null) _tidbit.Show(TidbitTrigger.PackVaultGiftComplete, GoHome);
            else GoHome();
        }

        // ── Chrome ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Record that the gift has been seen, so the next launch holding starter packs lands in
        /// <see cref="PackVaultMode.Vault"/> rather than replaying the beat.
        ///
        /// <para><b>It cannot fail loudly and must not fail the open.</b> The marker is a presentation
        /// fact on local disk (<see cref="FirstRunMarkers"/>) — a write that does not land costs one
        /// replayed animation, while an exception escaping here would cost the player the reveal for a
        /// pack the ledger has already spent.</para>
        /// </summary>
        private static void MarkGiftSeen()
        {
            try
            {
                FirstRunMarkers.Mark(FirstRunMarkers.Gift);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PackVault] could not record the gift as seen " +
                                 $"({ex.GetType().Name}: {ex.Message}); it may play once more.");
            }
        }

        /// <summary>True while this render's elements should be staged for <see cref="PlayEntrance"/>.
        /// Read during the build, so it must be the same question the render asks afterwards.</summary>
        private bool WillPlayEntrance => _mode == PackVaultMode.Gift && !_entrancePlayed;

        /// <summary>
        /// Show or hide the gift banner. Vault mode never shows it, so the default in the UXML is
        /// hidden and only the gift turns it on — a returning player must not be congratulated for
        /// arriving.
        /// </summary>
        private void ShowBanner(bool shown)
        {
            if (_banner == null) return;

            // Staged before it is unhidden, in that order: the displaced state has to be the one the
            // element is first laid out in, or the banner appears at rest and then jumps back to
            // animate in. Same reason BuildSlot stages a slot at construction.
            if (shown && WillPlayEntrance) _banner.AddToClassList("vault-banner--enter");
            if (!shown) _banner.RemoveFromClassList("vault-banner--enter");

            _banner.EnableInClassList("vault-banner--hidden", !shown);
        }

        private void SetStageHint(bool shown)
            => _stageHint?.EnableInClassList("vault-stage__hint--hidden", !shown);

        private void SetRailVisible(bool shown)
            => _rail?.EnableInClassList("vault-rail--hidden", !shown);

        /// <summary>
        /// Hide Back while the gift holds the player, show it otherwise.
        ///
        /// <para><b>Hidden, not disabled.</b> <c>emb-back</c> makes this the target of Android's back
        /// gesture (<c>BackNavigation</c> clicks the topmost visible back control), so a merely
        /// disabled button would still be found and the system gesture would land on a dead control.
        /// Removing it from the tree lets the gesture fall through to the reveal's dismiss layer when a reveal
        /// is up, and to nothing when it is not — which is the intent.</para>
        /// </summary>
        private void ApplyGiftLock()
        {
            if (_back == null) return;
            _back.style.display = _giftLocked ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>The gift stops holding the player. Idempotent — called from the first open and
        /// from every dead end, and more than one of those can happen in a visit.</summary>
        private void ReleaseGiftLock()
        {
            if (!_giftLocked) return;
            _giftLocked = false;
            ApplyGiftLock();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            if (busy) _drag?.End();
            if (_rack != null)
                foreach (VisualElement child in _rack.Children())
                    child.SetEnabled(!busy);
        }

        private void SetStatus(string text)
        {
            if (_status == null) return;
            _status.text = text ?? "";
            _status.EnableInClassList("vault-status--empty", _status.text.Length == 0);
        }

        private void SetLoading(bool loading)
        {
            _loading?.EnableInClassList("vault-loading--hidden", !loading);
            if (loading) _loadingTick?.Resume();
            else _loadingTick?.Pause();
        }

        /// <summary>A failure the player can read, with no rack behind it — an empty rack under an
        /// error message would say the player owns nothing, which is the one thing none of these
        /// statuses is evidence of.</summary>
        private void Fail(string message)
        {
            // A dead end is not something to hold the player on. Every one of these leaves a screen
            // with no openable pack on it, and the gift's lock exists only to stop a pack being
            // skipped — so without this, a server that will not open packs is a screen with nothing
            // to press. Reachable today: an unset KINGDOM_PACK_SEED_SECRET makes the route 503 for
            // everyone, which arrives here as Unavailable on the very first pack of first run.
            ReleaseGiftLock();
            ShowBanner(false);
            _rack?.Clear();
            ClearStagePack();
            SetStageHint(false);
            SetRailVisible(false);
            _shelfBalance = null;
            SetStatus(message);
        }

        /// <summary>True while this screen is still on and its fetch still wanted. Every await here
        /// can outlive the scene, and a UI write after that is an exception on a destroyed
        /// panel.</summary>
        private bool Alive(CancellationToken token)
            => this != null && isActiveAndEnabled && !token.IsCancellationRequested;

        private void Exit() => ScreenNav.Go(_returnScene);
    }
}
