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
        /// <summary>The unopened-pack surface: the shelf immediately, Back always, no banner.</summary>
        Vault = 0,

        /// <summary>The onboarding gift: a banner, a scripted entrance, and no way out until the
        /// first pack is open.</summary>
        Gift = 1,
    }

    /// <summary>
    /// The pack vault: every unopened pack this player holds, and the verb that spends one.
    /// Slices 1 and 2 of <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>It replaces the PackReveal scene rather than sitting beside it.</b> That screen said
    /// of itself that it was "a frame, not a finished screen … meant to be replaced rather than
    /// extended": it could present a receipt and could not produce one, so a granted pack had a
    /// reveal and no way to reach it. This owns the whole verb — <i>a pack becomes cards</i> — which
    /// is why the old scene, its controller and its UXML were deleted in the same commit. Two pack
    /// screens in one build is the state worth never having.</para>
    ///
    /// <para><b>The reveal itself is still <see cref="PackRevealPanel"/>, untouched.</b> It was split
    /// out of the store in 2026-08-20 precisely so a pack opened somewhere other than the store could
    /// have one, and it queries its <c>reveal-*</c> elements out of whatever root it is handed. Do not
    /// write a second reveal.</para>
    ///
    /// <para><b>Two modes, one screen</b> (<see cref="PackVaultMode"/>). <see cref="PackVaultMode.Gift"/>
    /// is the onboarding beat: it opens with a banner and a staggered entrance, it hides Back until a
    /// pack has actually been opened, and it always goes home to the main menu. Everything else about
    /// it — the shelf, the open, the reveal — is the vault's, unchanged. The mode is set by the caller
    /// (<c>MainMenuController.BeginFirstRun</c>) and consumed on arrival.</para>
    ///
    /// <para><b>The BALANCE routes; the MARKER only picks the mode.</b> That split is what makes
    /// starter packs unstrandable, and it is the reason <see cref="FirstRunMarkers.Gift"/> is written
    /// HERE — on the first pack opened — rather than by whatever routed the player in. A player who
    /// force-quits the instant this scene loads has not had their gift yet, and must get it on the
    /// next launch.</para>
    ///
    /// <para><b>Two ways in, and they go home differently.</b> A receipt written to
    /// <see cref="Pending"/> by someone else — a store purchase — is shown on arrival and returns to
    /// <see cref="ReturnScene"/> when it is dismissed, because that player is mid-shopping and the
    /// wallet they just spent from is back there. A pack opened <i>here</i> returns to the shelf,
    /// with one fewer on it. Same screen, same reveal; the difference is only where Done leads.</para>
    ///
    /// <para><b>No ScrollView, deliberately</b> — the shelf is a wrapping row that fits what it can
    /// and counts the rest. UI Toolkit's ScrollView content lays out correctly and <i>paints</i> above
    /// its viewport on Android (#285, #307); it is a renderer fault no stylesheet can reach, and it
    /// is not worth inheriting for a shelf that holds five things. When the shelf genuinely needs to
    /// scroll, that bug is the thing to fix first.
    /// <b>RETIREMENT TRIGGER:</b> when #285 and #307 close, this paragraph and the
    /// <see cref="MaxTilesShown"/> overflow label may both go.</para>
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
        public static string ReturnScene = "MainMenu";

        /// <summary>A receipt opened somewhere else, to be revealed on arrival. Consumed on read: a
        /// receipt is one opening, and leaving it set would replay the same cards the next time
        /// anything lands here.</summary>
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

        /// <summary>
        /// How many pack tiles the shelf draws before it stops and counts the rest.
        ///
        /// <para>The shelf does not scroll (see the type remarks), so an unbounded balance would
        /// overflow the panel silently. Ten is comfortably more than the five the gift grants and
        /// more than a shopping trip produces; past it, a count is more use than a tile the player
        /// cannot see anyway.</para>
        /// </summary>
        private const int MaxTilesShown = 10;

        /// <summary>How long after a render the banner settles, and the step between tiles behind it.
        /// The same shape as <see cref="PackRevealPanel"/>'s <c>140 + i*170</c> card stagger, a little
        /// quicker: these are five identical objects arriving, not five things to read.</summary>
        private const int EntranceBannerMs = 60;
        private const int EntranceFirstTileMs = 220;
        private const int EntranceTileStepMs = 90;

        private VisualElement _shelf;
        private Label _status;
        private Label _subtitle;
        private VisualElement _banner;
        private Button _back;
        private PackRevealPanel _reveal;
        private PackTearAnimator _tear;
        private IPackArtProvider _packArt;

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

        /// <summary>The entrance is an arrival, so it plays on the first shelf this visit draws and
        /// never again — the shelf is re-rendered after every open, and re-staggering the tiles the
        /// player just watched a pack leave would read as a glitch, not a flourish.</summary>
        private bool _entrancePlayed;

        /// <summary>True while the reveal on screen belongs to somebody else's open. Decides where
        /// Done goes, and nothing else.</summary>
        private bool _revealWasHandedIn;

        /// <summary>One open at a time, and no tile is live while one is in flight. A second tap
        /// during an open would spend a second pack, which is the one mistake on this screen that
        /// costs the player something real.</summary>
        private bool _busy;

        private CancellationTokenSource _cancel;

        private void OnEnable()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[PackVault] No rootVisualElement — is the UXML assigned?");
                return;
            }

            _mode = Mode;
            Mode = PackVaultMode.Vault;

            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "MainMenu" : ReturnScene;
            ReturnScene = "MainMenu";

            // The gift goes home to the menu whatever the last caller left in ReturnScene. It is the
            // tail of first run, not a detour out of some screen the player chose to be on.
            if (_mode == PackVaultMode.Gift) _returnScene = "MainMenu";

            _shelf = root.Q<VisualElement>("vault-shelf");
            _status = root.Q<Label>("vault-status");
            _subtitle = root.Q<Label>("vault-subtitle");
            _banner = root.Q<VisualElement>("vault-banner");
            _packArt = new PackArtProvider();
            _reveal = new PackRevealPanel(root, RevealDismissed);
            _tear = new PackTearAnimator(root);
            _cancel = new CancellationTokenSource();

            _back = root.Q<Button>("btn-vault-back");
            if (_back != null) _back.clicked += Exit;

            _entrancePlayed = false;
            _giftLocked = _mode == PackVaultMode.Gift;
            ApplyGiftLock();

            PackOpenReceipt handedIn = Pending;
            Pending = null;

            if (handedIn != null)
            {
                // Somebody else's open. The shelf underneath is never drawn: Done leaves, so a fetch
                // would be a round trip to render a screen nobody sees.
                //
                // A handed-in receipt is a purchase, never the gift, so nothing here is gated: if a
                // caller managed to set both, the receipt wins and the lock comes off with it.
                _revealWasHandedIn = true;
                ReleaseGiftLock();
                SetSubtitle("");
                SetStatus("");
                _reveal.Show(handedIn);
                return;
            }

            Refresh();
        }

        private void OnDisable()
        {
            // A scene change mid-fetch would otherwise land a UI write on a destroyed panel.
            _cancel?.Cancel();
            _cancel?.Dispose();
            _cancel = null;
        }

        // ── The shelf ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Read the shelf and draw it.
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
                    // refresh that was refused. Both read the same from here: no identity, no shelf.
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
                        RenderShelf(snapshot.Packs);
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

        private void RenderShelf(IReadOnlyDictionary<string, long> packs)
        {
            if (_shelf == null) return;
            _shelf.Clear();

            // Ordinal, so the shelf does not reorder itself between two reads of the same balance.
            var lines = new List<KeyValuePair<string, long>>();
            long total = 0;
            if (packs != null)
            {
                foreach (KeyValuePair<string, long> line in packs)
                {
                    if (line.Value <= 0) continue;
                    lines.Add(line);
                    total += line.Value;
                }
            }
            lines.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            if (total == 0)
            {
                // Nothing to hold the player on, so the gift stops holding them. Reachable in Gift
                // mode: the balance that routed them here was read on another screen, and another
                // device may have spent it since.
                ReleaseGiftLock();
                ShowBanner(false);
                SetSubtitle("");
                SetStatus("No packs waiting.");
                return;
            }

            SetSubtitle(total == 1 ? "1 pack waiting" : $"{total} packs waiting");
            SetStatus("");

            int drawn = 0;
            foreach (KeyValuePair<string, long> line in lines)
            {
                for (long i = 0; i < line.Value && drawn < MaxTilesShown; i++, drawn++)
                    _shelf.Add(BuildTile(line.Key));
                if (drawn >= MaxTilesShown) break;
            }

            long hidden = total - drawn;
            if (hidden > 0)
            {
                var more = new Label($"+{hidden} more");
                more.AddToClassList("vault-pack__overflow");
                if (WillPlayEntrance) more.AddToClassList("vault-pack--enter");
                _shelf.Add(more);
            }

            // The banner belongs to the shelf, not to the screen: it says "here is a gift", and it
            // must not be the thing on screen while the fetch is still running or after it failed.
            ShowBanner(_mode == PackVaultMode.Gift);
            if (_mode == PackVaultMode.Gift && !_entrancePlayed)
            {
                _entrancePlayed = true;
                PlayEntrance();
            }
        }

        /// <summary>
        /// Stagger the banner and the tiles in. <b>USS does the animation</b> — every element is built
        /// carrying an <c>--enter</c> class that holds it faded and displaced, and this only schedules
        /// the class coming off, at which point the transition on the base rule runs. Same mechanism as
        /// <see cref="PackRevealPanel"/>'s card stagger, for the same reason: a transition is one
        /// declaration and a hand-driven lerp is a frame loop that has to be cancelled on scene exit.
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

            if (_shelf == null) return;

            int i = 0;
            foreach (VisualElement child in _shelf.Children())
            {
                VisualElement tile = child;
                int delay = EntranceFirstTileMs + (i * EntranceTileStepMs);
                tile.schedule
                    .Execute(() => tile.RemoveFromClassList("vault-pack--enter"))
                    .StartingIn(delay);
                i++;
            }
        }

        /// <summary>
        /// One unopened pack. <b>A tile per pack, not per line</b> — the count is the point of the
        /// screen, and "five packs" reads as five objects, not as a number beside one.
        ///
        /// <para>A <see cref="Button"/> rather than a click callback on a plain element: it brings
        /// focus, the disabled state the busy guard needs, and keyboard activation, all of which a
        /// hand-rolled <c>ClickEvent</c> handler would have to grow.</para>
        /// </summary>
        private VisualElement BuildTile(string packId)
        {
            var tile = new Button(() => Open(packId));
            tile.AddToClassList("vault-pack");
            tile.SetEnabled(!_busy);

            // Staged displaced, for PlayEntrance to release. Applied at BUILD time rather than after
            // the fact because a class added in the same frame the element is created transitions from
            // the staged value; one added afterwards would flash the tile at rest first.
            if (WillPlayEntrance) tile.AddToClassList("vault-pack--enter");

            var face = new VisualElement();
            face.AddToClassList("vault-pack__face");
            tile.Add(face);

            // The face gets the set's first piece of art if the set has any. A set whose art has not
            // been drawn yet is the normal state and says nothing — see PackArtProvider — so an
            // artless pack reads as a pack without a picture, never as a picture that failed.
            // The starter set is exactly this today: Assets/Art/Shop/Packs/STARTER/ does not exist.
            _packArt?.LoadPackArt(packId, pieces =>
            {
                if (pieces == null || pieces.Count == 0) return;
                face.style.backgroundImage = new StyleBackground(pieces[0].Sprite);
                face.AddToClassList("vault-pack__face--art");
            });

            var name = new Label(PackDisplayName(packId));
            name.AddToClassList("vault-pack__name");
            tile.Add(name);

            var hint = new Label("Tap to open");
            hint.AddToClassList("vault-pack__hint");
            tile.Add(hint);

            return tile;
        }

        /// <summary>A pack line as a player should read it. <c>pack.emberfall.starter</c> is
        /// "Starter Pack" — the last segment is the only part that distinguishes one line from
        /// another, which is the same rule <see cref="PackArtSet.SetIdFor"/> follows.</summary>
        private static string PackDisplayName(string packId)
        {
            string token = TokenText.Pretty(packId);
            return token.Length == 0 ? "Pack" : token + " Pack";
        }

        // ── Opening ─────────────────────────────────────────────────────────────────────

        private async void Open(string packId)
        {
            if (_busy) return;

            CancellationToken token = _cancel?.Token ?? CancellationToken.None;

            // Set on every path that hands the screen to something else — a reveal, or a re-read. The
            // busy flag belongs to whatever is now driving; clearing it here would re-arm the tiles
            // underneath a reveal, and under a Refresh that is still in flight.
            bool retained = false;
            try
            {
                SetBusy(true);
                SetStatus("Opening…");

                string accessToken = await AuthGateway.TokenForConnectAsync();
                if (!Alive(token)) return;

                if (string.IsNullOrEmpty(accessToken))
                {
                    Fail("Sign in again to open this pack.");
                    return;
                }

                PackOpenResult result = await PackGateway.OpenAsync(packId, accessToken, token);
                if (!Alive(token)) return;

                if (result.HasReveal)
                {
                    // AlreadyOpened carries the SAME cards and spent nothing twice, so it is shown
                    // exactly as an Opened is. The two are separate only so a log line can tell them
                    // apart.
                    _revealWasHandedIn = false;
                    retained = true;

                    // THE GIFT IS SPENT THE MOMENT A PACK OPENS, not when the player arrived and not
                    // when they leave. Anything earlier loses the gift to a force-quit; anything later
                    // loses it to one. Written on a Vault-mode open too: a player who has opened a
                    // pack has had the moment, whichever door they came through, and marking it here
                    // means there is exactly one site that can.
                    // Read BEFORE the release, because the release is what stops it being the first.
                    // Spec §8: no skip on the gift's first pack, everything after it is skippable.
                    bool firstGiftPack = _mode == PackVaultMode.Gift && _giftLocked;

                    MarkGiftSeen();
                    ReleaseGiftLock();

                    SetStatus("");
                    PackOpenReceipt receipt = result.Receipt;
                    _tear.Play(receipt, () => _reveal.Show(receipt), allowSkip: !firstGiftPack);
                    return;
                }

                switch (result.Status)
                {
                    case PackOpenStatus.NoneOwned:
                        // Honest and reachable: another device opened the last one, or this shelf was
                        // drawn before it was. Re-read rather than argue with it.
                        retained = true;
                        SetStatus("That pack is already open.");
                        Refresh();
                        return;

                    case PackOpenStatus.Offline:
                        // NOT "nothing happened". The open commits before the response is written, so
                        // this pack may be genuinely spent. Re-reading the shelf is the recovery; a
                        // retry derives the next instance id and spends a second pack. Never offer one.
                        retained = true;
                        SetStatus("We lost the connection. Checking what you have…");
                        Refresh();
                        return;

                    case PackOpenStatus.Unauthenticated:
                        Fail("Sign in again to open this pack.");
                        return;

                    case PackOpenStatus.Unavailable:
                        Fail("Packs cannot be opened on this server.");
                        Debug.LogWarning($"[PackVault] open unavailable: {result.Detail}");
                        return;

                    case PackOpenStatus.UnknownPack:
                        // A client bug — this shelf was drawn from the server's own balance, so a pack
                        // line it will not accept means the two disagree about what a pack is.
                        Fail("This build does not know that pack.");
                        Debug.LogError($"[PackVault] server refused pack id '{packId}': {result.Detail}");
                        return;

                    default:
                        // A 2xx that decoded to nothing. The cards may well have been granted, so the
                        // answer is another read, never another open.
                        retained = true;
                        SetStatus("Something went wrong opening that. Checking what you have…");
                        Debug.LogWarning($"[PackVault] open malformed: {result.Detail}");
                        Refresh();
                        return;
                }
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

        /// <summary>Done, on the reveal. A receipt this screen produced goes back to the shelf with
        /// one fewer on it; one handed in by the store goes back to the store.</summary>
        private void RevealDismissed()
        {
            if (_revealWasHandedIn)
            {
                Exit();
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
            // animate in. Same reason BuildTile stages a tile at construction.
            if (shown && WillPlayEntrance) _banner.AddToClassList("vault-banner--enter");
            if (!shown) _banner.RemoveFromClassList("vault-banner--enter");

            _banner.EnableInClassList("vault-banner--hidden", !shown);
        }

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
            if (_shelf == null) return;
            foreach (VisualElement child in _shelf.Children())
                child.SetEnabled(!busy);
        }

        private void SetStatus(string text)
        {
            if (_status == null) return;
            _status.text = text ?? "";
            _status.EnableInClassList("vault-status--empty", _status.text.Length == 0);
        }

        private void SetSubtitle(string text)
        {
            if (_subtitle == null) return;
            _subtitle.text = text ?? "";
        }

        /// <summary>A failure the player can read, with no tiles behind it — an empty shelf under an
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
            _shelf?.Clear();
            SetSubtitle("");
            SetStatus(message);
        }

        /// <summary>True while this screen is still on and its fetch still wanted. Every await here
        /// can outlive the scene, and a UI write after that is an exception on a destroyed
        /// panel.</summary>
        private bool Alive(CancellationToken token)
            => this != null && isActiveAndEnabled && !token.IsCancellationRequested;

        private void Exit() => Go(_returnScene);

        /// <summary>Load a scene by name, reporting the one failure mode that is otherwise silent
        /// (a scene missing from the build list is a dead button with nothing logged in the
        /// Editor). Mirrors <c>ShopScreenController.Go</c>.</summary>
        private static void Go(string sceneName)
        {
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[PackVault] Scene '{sceneName}' is not in the build list, so this " +
                               "button cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }
    }
}
