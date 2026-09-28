using System;
using System.Collections.Generic;
using System.Threading;
using Emberfall.Net.Client.Collection;
using Emberfall.Net.Client.Shop;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
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
    /// to.) The STAGE in the middle is where packs open. The RAIL under it is where a pack is held to
    /// count up a stack. <see cref="PackRackDrag"/> owns the gesture; a tap still opens, so a broken
    /// drag never strands a pack.</para>
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
    /// caller (<c>MainMenuController.BeginFirstRun</c>) and consumed on arrival.</para>
    ///
    /// <para><b>The BALANCE routes; the MARKER only picks the mode.</b> That split is what makes
    /// starter packs unstrandable, and it is the reason <see cref="FirstRunMarkers.Gift"/> is written
    /// HERE — on the first pack opened — rather than by whatever routed the player in. A player who
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

        private VisualElement _root;
        private VisualElement _rack;
        private VisualElement _stage;
        private VisualElement _stageHint;
        private VisualElement _rail;
        private VisualElement _railHint;
        private Label _status;
        private VisualElement _banner;
        private Button _back;
        private PackRevealPanel _reveal;
        private PackTearAnimator _tear;
        private IPackArtProvider _packArt;
        private PackRackDrag _drag;

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
        /// an unset <c>EMBERFALL_PACK_SEED_SECRET</c> produces on every request — and a session the
        /// server no longer accepts. <see cref="Fail"/> and a zero-total render both release it.</para>
        /// </summary>
        private bool _giftLocked;

        /// <summary>The entrance is an arrival, so it plays on the first rack this visit draws and
        /// never again — the rack is re-rendered after every open, and re-staggering it would read as
        /// a glitch, not a flourish.</summary>
        private bool _entrancePlayed;

        /// <summary>True while the reveal on screen belongs to somebody else's open. Decides where
        /// Done goes, and nothing else.</summary>
        private bool _revealWasHandedIn;

        /// <summary>One open at a time, and no gesture starts while one is in flight. A second open
        /// during the first would spend more packs than the player asked for, which is the one
        /// mistake on this screen that costs the player something real.</summary>
        private bool _busy;

        /// <summary>
        /// The balance the rack last drew, kept so Done can redraw it with the opened packs gone
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
        /// on screen, so Done may redraw from it without a fetch.</summary>
        private bool _shelfSettled;

        /// <summary>The stack parked on the rail: a pack line and how many. Presentation only — no
        /// pack is spent or reserved by parking it, and it is clamped to the balance on every render.</summary>
        private string _parkedPack;
        private int _parkedCount;

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
            _root = root;

            // Top-level destination — same corner settings gear as the main menu (Sam, 2026-09-05).
            MatchSettingsController.Mount();

            _mode = Mode;
            Mode = PackVaultMode.Vault;

            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "MainMenu" : ReturnScene;
            ReturnScene = ScreenNav.MainMenu;

            // The gift goes home to the menu whatever the last caller left in ReturnScene. It is the
            // tail of first run, not a detour out of some screen the player chose to be on.
            if (_mode == PackVaultMode.Gift) _returnScene = "MainMenu";

            _rack = root.Q<VisualElement>("vault-rack");
            _stage = root.Q<VisualElement>("vault-stage");
            _stageHint = root.Q<VisualElement>("vault-stage-hint");
            _rail = root.Q<VisualElement>("vault-rail");
            _railHint = root.Q<VisualElement>("vault-rail-hint");
            _status = root.Q<Label>("vault-status");
            _banner = root.Q<VisualElement>("vault-banner");
            _packArt = new PackArtProvider();
            _reveal = new PackRevealPanel(root, RevealDismissed);
            _tear = new PackTearAnimator();
            _cancel = new CancellationTokenSource();

            _drag = new PackRackDrag(root, _rack, _stage, _rail, ResolveSource,
                canStart: () => !_busy && _shelfBalance != null,
                held: packId => (int)Math.Min(Held(packId), int.MaxValue),
                parkedCount: () => _parkedCount,
                art: packId => FaceSpriteOrNull(packId, 0),
                open: (packId, count) => OpenBatch(packId, count),
                park: Park,
                unpark: Unpark);

            _back = root.Q<Button>("btn-vault-back");
            if (_back != null) _back.clicked += Exit;

            _entrancePlayed = false;
            _giftLocked = _mode == PackVaultMode.Gift;
            ApplyGiftLock();

            PackOpenReceipt handedIn = Pending;
            Pending = null;

            if (handedIn != null)
            {
                // Somebody else's open. The rack underneath is never drawn: Done leaves, so a fetch
                // would be a round trip to render a screen nobody sees.
                //
                // A handed-in receipt is already open, never the gift, so nothing here is gated:
                // if a caller managed to set both, the receipt wins and the lock comes off with it.
                _revealWasHandedIn = true;
                ReleaseGiftLock();
                SetStatus("");
                _reveal.Show(handedIn);
                return;
            }

            Refresh();
        }

        private void OnDisable()
        {
            _drag?.Dispose();
            _drag = null;

            // A scene change mid-fetch would otherwise land a UI write on a destroyed panel.
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;
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
                SetStatus("Checking what you have…");

                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!Alive(token)) return;

                if (string.IsNullOrEmpty(accessToken))
                {
                    // TokenForConnectAsync answers the empty string for every failure, including a
                    // refresh that was refused. Both read the same from here: no identity, no rack.
                    Fail("Sign in to see your packs.");
                    return;
                }

                CollectionSnapshot snapshot = await PackGateway.ReadShelfAsync(accessToken, token);
                if (!Alive(token)) return;

                switch (snapshot.Status)
                {
                    // Both statuses carry real pack balances — a superuser has no card list to
                    // enumerate and does still hold packs, and the gift is developed on one.
                    case CollectionSyncStatus.Synced:
                    case CollectionSyncStatus.Unconstrained:
                        // Unbusy BEFORE the render, so the rack is drawn live.
                        SetBusy(false);
                        RenderRack(snapshot.Packs);
                        break;

                    case CollectionSyncStatus.Offline:
                        Fail("Could not reach the server. Your packs are safe — try again in a moment.");
                        break;

                    case CollectionSyncStatus.Unauthenticated:
                        Fail("Sign in again to see your packs.");
                        break;

                    case CollectionSyncStatus.Unavailable:
                        Fail("This server has no pack economy.");
                        break;

                    default:
                        Fail("The server sent something this build could not read.");
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
                if (Alive(token)) Fail("Could not read your packs.");
            }
            finally
            {
                if (Alive(token)) SetBusy(false);
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

            // The park never outlives the packs it stands for.
            if (_parkedPack != null)
                _parkedCount = (int)Math.Min(_parkedCount, Math.Min(Held(_parkedPack), PackRackDrag.MaxBatch));
            if (_parkedCount <= 0) { _parkedPack = null; _parkedCount = 0; }
            RenderRail();

            if (total == 0)
            {
                // Nothing to hold the player on, so the gift stops holding them. Reachable in Gift
                // mode: the balance that routed them here was read on another screen, and another
                // device may have spent it since.
                ReleaseGiftLock();
                ShowBanner(false);
                SetStageHint(false);
                SetStatus("No packs waiting.");
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

            var label = new Label(PackLineName(packId)) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("vault-slot__label");
            slot.Add(label);
            return slot;
        }

        /// <summary>A pack miniature with its count bubble — the rack slot's and the parked stack's.
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

        /// <summary>"pack.emberfall" → "Emberfall"; "pack.emberfall.starter" → "Emberfall Starter".</summary>
        private static string PackLineName(string packId)
        {
            string[] parts = packId?.Split('.');
            if (parts == null || parts.Length < 2) return "Pack";
            string name = TokenText.Pretty(parts[1]);
            if (name.Length == 0) return "Pack";
            return parts.Length > 2 ? name + " " + TokenText.Pretty(parts[2]) : name;
        }

        // ── The rail ────────────────────────────────────────────────────────────────────

        private void Park(string packId, int count)
        {
            _parkedPack = packId;
            _parkedCount = count;
            RenderRail();
        }

        private void Unpark()
        {
            _parkedPack = null;
            _parkedCount = 0;
            RenderRail();
        }

        /// <summary>The parked stack, or the rail's hint when nothing is parked.</summary>
        private void RenderRail()
        {
            if (_rail == null) return;
            _rail.Q<VisualElement>("vault-rail-stack")?.RemoveFromHierarchy();

            bool parked = _parkedPack != null && _parkedCount > 0;
            _railHint?.EnableInClassList("vault-rail__hint--hidden", parked);
            if (!parked) return;

            var stack = new VisualElement { name = "vault-rail-stack", userData = _parkedPack };
            stack.AddToClassList("vault-rail__stack");
            stack.SetEnabled(!_busy);
            stack.Add(BuildMini(_parkedPack, _parkedCount));
            var label = new Label("Drag to open " + _parkedCount) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("vault-rail__label");
            stack.Add(label);
            _rail.Add(stack);
        }

        /// <summary>What <see cref="PackRackDrag"/> is pressing on: a rack slot or the parked stack,
        /// found by walking up from the event target to the element carrying a pack id.</summary>
        private PackRackDrag.Source ResolveSource(VisualElement target)
        {
            for (VisualElement e = target; e != null && e != _root; e = e.parent)
            {
                if (!(e.userData is string packId)) continue;
                if (e.ClassListContains("vault-slot")) return new PackRackDrag.Source(e, packId, false);
                if (e.ClassListContains("vault-rail__stack")) return new PackRackDrag.Source(e, packId, true);
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
        /// Open <paramref name="count"/> packs of one line, one request at a time, then tear the
        /// stage pack and reveal everything that came back.
        ///
        /// <para><b>It stops at the first open that does not return a receipt, and never retries
        /// it.</b> <see cref="PackOpenStatus.Offline"/> may be a pack genuinely spent; the recovery is
        /// a re-read, and a retry would spend a second one. Whatever did come back is still revealed
        /// — those cards are granted — and Done re-reads rather than trusting the arithmetic.</para>
        /// </summary>
        private async void OpenBatch(string packId, int count)
        {
            if (_busy) return;
            count = (int)Math.Min(count, Math.Min(Held(packId), PackRackDrag.MaxBatch));
            if (count < 1) return;

            // A stack taken off the rail is spent from the rail; any other open leaves the park as it
            // is, and the render after Done clamps it to what is left.
            if (packId == _parkedPack) Unpark();

            CancellationToken token = _cancel?.Token ?? CancellationToken.None;

            // Set on every path that hands the screen to something else — a reveal, or a re-read. The
            // busy flag belongs to whatever is now driving; clearing it here would re-arm the rack
            // underneath a reveal, and under a Refresh that is still in flight.
            bool retained = false;
            try
            {
                SetBusy(true);
                Sprite art = PlaceStagePack(packId);
                SetStatus(count == 1 ? "Opening…" : $"Opening 1 of {count}…");

                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!Alive(token)) return;

                if (string.IsNullOrEmpty(accessToken))
                {
                    Fail("Sign in again to open this pack.");
                    return;
                }

                var receipts = new List<PackOpenReceipt>(count);
                bool settled = true;
                PackOpenResult stopped = null;

                for (int i = 0; i < count; i++)
                {
                    if (i > 0) SetStatus($"Opening {i + 1} of {count}…");

                    PackOpenResult result = await PackGateway.OpenAsync(packId, accessToken, token);
                    if (!Alive(token)) return;

                    if (!result.HasReveal)
                    {
                        stopped = result;
                        break;
                    }

                    // AlreadyOpened carries the SAME cards and spent nothing twice, so it is shown
                    // exactly as an Opened is — but it moved no counter, so the balance is not ours to
                    // decrement and Done re-reads.
                    receipts.Add(result.Receipt);
                    if (result.Status == PackOpenStatus.Opened && Held(packId) > 0)
                        _shelfBalance[packId] = Held(packId) - 1;
                    else
                        settled = false;

                    // THE GIFT IS SPENT THE MOMENT A PACK OPENS, not when the player arrived and not
                    // when they leave. Anything earlier loses the gift to a force-quit; anything later
                    // loses it to one. Written on a Vault-mode open too: a player who has opened a
                    // pack has had the moment, whichever door they came through, and marking it here
                    // means there is exactly one site that can.
                    MarkGiftSeen();
                    ReleaseGiftLock();
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

                string note = stopped == null ? null : $"stopped after {receipts.Count} of {count}";
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
                if (Alive(token)) Fail("Could not open that pack.");
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
                    SetStatus("That pack is already open.");
                    Refresh();
                    return true;

                case PackOpenStatus.Offline:
                    // NOT "nothing happened". The open commits before the response is written, so
                    // this pack may be genuinely spent. Re-reading is the recovery; a retry derives the
                    // next instance id and spends a second pack. Never offer one.
                    SetStatus("We lost the connection. Checking what you have…");
                    Refresh();
                    return true;

                case PackOpenStatus.Unauthenticated:
                    Fail("Sign in again to open this pack.");
                    return false;

                case PackOpenStatus.Unavailable:
                    Fail("Packs cannot be opened on this server.");
                    Debug.LogWarning($"[PackVault] open unavailable: {result.Detail}");
                    return false;

                case PackOpenStatus.UnknownPack:
                    // A client bug — this rack was drawn from the server's own balance, so a pack
                    // line it will not accept means the two disagree about what a pack is.
                    Fail("This build does not know that pack.");
                    Debug.LogError($"[PackVault] server refused pack id '{packId}': {result.Detail}");
                    return false;

                default:
                    // A 2xx that decoded to nothing. The cards may well have been granted, so the
                    // answer is another read, never another open.
                    SetStatus("Something went wrong opening that. Checking what you have…");
                    Debug.LogWarning($"[PackVault] open malformed: {result?.Detail}");
                    Refresh();
                    return true;
            }
        }

        /// <summary>Done, on the reveal. A run this screen produced goes back to the rack with the
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

        /// <summary>
        /// Hide Back while the gift holds the player, show it otherwise.
        ///
        /// <para><b>Hidden, not disabled.</b> <c>emb-back</c> makes this the target of Android's back
        /// gesture (<c>BackNavigation</c> clicks the topmost visible back control), so a merely
        /// disabled button would still be found and the system gesture would land on a dead control.
        /// Removing it from the tree lets the gesture fall through to the reveal's Done when a reveal
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
            _rail?.Q<VisualElement>("vault-rail-stack")?.SetEnabled(!busy);
        }

        private void SetStatus(string text)
        {
            if (_status == null) return;
            _status.text = text ?? "";
            _status.EnableInClassList("vault-status--empty", _status.text.Length == 0);
        }

        /// <summary>A failure the player can read, with no rack behind it — an empty rack under an
        /// error message would say the player owns nothing, which is the one thing none of these
        /// statuses is evidence of.</summary>
        private void Fail(string message)
        {
            // A dead end is not something to hold the player on. Every one of these leaves a screen
            // with no openable pack on it, and the gift's lock exists only to stop a pack being
            // skipped — so without this, a server that will not open packs is a screen with nothing
            // to press. Reachable today: an unset EMBERFALL_PACK_SEED_SECRET makes the route 503 for
            // everyone, which arrives here as Unavailable on the very first pack of first run.
            ReleaseGiftLock();
            ShowBanner(false);
            _rack?.Clear();
            ClearStagePack();
            SetStageHint(false);
            _shelfBalance = null;
            Unpark();
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
