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
    /// The pack vault: every unopened pack this player holds, and the verb that spends one.
    /// Slice 1 of <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
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
        /// How many pack tiles the shelf draws before it stops and counts the rest.
        ///
        /// <para>The shelf does not scroll (see the type remarks), so an unbounded balance would
        /// overflow the panel silently. Ten is comfortably more than the five the gift grants and
        /// more than a shopping trip produces; past it, a count is more use than a tile the player
        /// cannot see anyway.</para>
        /// </summary>
        private const int MaxTilesShown = 10;

        private VisualElement _shelf;
        private Label _status;
        private Label _subtitle;
        private PackRevealPanel _reveal;
        private IPackArtProvider _packArt;

        private string _returnScene = "MainMenu";

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

            _returnScene = string.IsNullOrEmpty(ReturnScene) ? "MainMenu" : ReturnScene;
            ReturnScene = "MainMenu";

            _shelf = root.Q<VisualElement>("vault-shelf");
            _status = root.Q<Label>("vault-status");
            _subtitle = root.Q<Label>("vault-subtitle");
            _packArt = new PackArtProvider();
            _reveal = new PackRevealPanel(root, RevealDismissed);
            _cancel = new CancellationTokenSource();

            Button back = root.Q<Button>("btn-vault-back");
            if (back != null) back.clicked += Exit;

            PackOpenReceipt handedIn = Pending;
            Pending = null;

            if (handedIn != null)
            {
                // Somebody else's open. The shelf underneath is never drawn: Done leaves, so a fetch
                // would be a round trip to render a screen nobody sees.
                _revealWasHandedIn = true;
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
                _shelf.Add(more);
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
                    SetStatus("");
                    _reveal.Show(result.Receipt);
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
