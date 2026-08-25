using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Turns a <see cref="CardDefinition"/> into a card-face texture for the collection wall
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.9): the REAL world-space card, built by
    /// <see cref="BoardController.BuildDetachedCard"/> and photographed by
    /// <see cref="CardSnapshot"/>, handed back as a Texture UI Toolkit can use as a
    /// backgroundImage.
    ///
    /// WHY NOT AUTHOR THE WALL TILE. A wall card is the in-match card at 1.50× and must stay
    /// pixel-identical to it forever. A UI Toolkit re-implementation of the card front would be a
    /// SECOND card front, and it would drift on the first frame-art change, the first socket
    /// nudge, the first keyword-lead tweak — silently, because nothing compares the two. There is
    /// one card front; this takes its picture.
    ///
    /// AS PRINTED, NOT AS PLAYED. Every face is baked against one never-updated
    /// <see cref="CardLiveState"/>, which is exactly the state a card in HAND already presents
    /// through: MaxHealth 0 sends CardPresenter.RecolorStats down its printed-stats branch, an
    /// empty keyword set leaves RebuildKeywords with the definition's own keywords, AuraSet.None
    /// and AttackReadiness.None leave no shield ring, no frost, no exhausted dim and no selection
    /// glow. Nothing here interprets "printed" a second time, so nothing here can disagree with
    /// the card in your hand.
    ///
    /// ONE RIG, ONE CARD AT A TIME. Building and destroying a 13-renderer hierarchy per card would
    /// cost a 400-card wall 5,200 GameObjects; instead one detached card is parked off-camera and
    /// rebound per bake. That makes the rig a shared mutable resource, so requests are QUEUED:
    /// IArtProvider.LoadArt may complete synchronously or frames later (see IArtProvider), and a
    /// second card rebinding the rig while the first is still waiting on art would photograph a
    /// chimera. Only the art callback advances the queue.
    ///
    /// DISPOSE. Cached faces are RenderTextures — unmanaged, and not collected. They are released
    /// on <see cref="ClearCache"/>, on a face-size change, and on destroy.
    /// </summary>
    public sealed class CardFaceBaker : MonoBehaviour
    {
        // The wall does not upscale a smaller bake: its fixed display geometry is also its capture
        // resolution, so enlarging the collection reveals more of the real card rather than merely
        // stretching the old texture. CardWall owns the dimensions; the casts remain compile-time
        // constants and keep a newly-added baker on the same contract before any request is queued.
        [SerializeField] private int faceWidth  = (int)CardWall.TileWidth;
        [SerializeField] private int faceHeight = (int)CardWall.TileHeight;

        // Off-camera parking. The rig must stay ACTIVE — an inactive hierarchy renders to nothing,
        // which would bake 400 transparent faces — so it is moved out of the board camera's ortho
        // window instead of disabled. The capture camera doesn't care where it sits: it is planted
        // relative to the visual's own pose and culls to the CardCapture layer alone.
        private static readonly Vector3 RigPark = new Vector3(0f, 1000f, 0f);

        private IArtProvider    _art;
        private BoardController _board;
        private GameObject      _rigRoot;
        private CardPresenter   _rig;
        private Transform       _rigVisual;

        // ONE live state for every card, constructed and never updated — see "AS PRINTED" above.
        private readonly CardLiveState _printed = new CardLiveState(new InstanceId(-1));

        private readonly Dictionary<int, RenderTexture>      _cache   = new();
        private readonly Dictionary<int, List<Action<Texture>>> _waiting = new();
        private readonly Queue<CardDefinition> _queue = new();

        private bool _awaitingArt;   // the rig is bound and its art callback has not fired yet
        private bool _pumping;       // re-entrancy guard: a synchronous completion must not recurse
        private bool _captureFailed; // one-shot: a missing CardCapture layer fails every card

        // ── Public API ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Stands up the hidden bake rig. <paramref name="db"/> is only what BoardController needs
        /// to consider itself initialized — faces are baked from the CardDefinition handed to
        /// <see cref="Bake"/>, never looked up. <paramref name="art"/> defaults to the same
        /// provider BoardController picks, so the wall's illustrations resolve exactly as the
        /// board's do.
        /// </summary>
        public void Initialize(ICardDatabase db, IArtProvider art = null)
        {
            if (_board != null) return;   // idempotent: a second rig would strand the first

#if UNITY_EDITOR
            _art = art ?? new EditorArtProvider();
#else
            _art = art ?? new AddressableArtProvider();
#endif

            var host = new GameObject("__CardFaceBakeHost");
            host.transform.SetParent(transform, false);
            _board = host.AddComponent<BoardController>();
            // No anchors, no input router, no match runner: this BoardController never ticks a
            // match, it only owns CreatePresenter. Seat ids are placeholders that nothing reads.
            _board.Initialize(db, null,
                              new PlayerId("face-bake-p0"), new PlayerId("face-bake-p1"),
                              artProvider: _art);
        }

        /// <summary>
        /// Delivers the face for <paramref name="def"/> to <paramref name="onReady"/> — immediately
        /// if it is already baked, otherwise once its art has resolved and the card has been
        /// photographed. A null texture means the bake could not be staged (missing capture layer);
        /// a face with no illustration is NOT a failure — cards whose art has not been generated
        /// yet bake fine, just without the picture.
        ///
        /// The same card is never baked twice: a second request for a card already in flight joins
        /// the first one's callback list.
        /// </summary>
        public void Bake(CardDefinition def, Action<Texture> onReady)
        {
            // The cache is keyed by card id, so a row with no id has nowhere to live. 0 is the
            // no-id case now — no card is ever numbered 0, which is what makes it usable as one.
            if (def == null || def.Id <= 0) { onReady?.Invoke(null); return; }

            if (_cache.TryGetValue(def.Id, out RenderTexture cached))
            {
                onReady?.Invoke(cached);
                return;
            }

            if (_waiting.TryGetValue(def.Id, out List<Action<Texture>> joined))
            {
                if (onReady != null) joined.Add(onReady);
                return;
            }

            var list = new List<Action<Texture>>();
            if (onReady != null) list.Add(onReady);
            _waiting[def.Id] = list;
            _queue.Enqueue(def);
            PumpQueue();
        }

        /// <summary>
        /// Re-derives the wall tile resolution. Every cached face is at the OLD size, so the cache
        /// is dropped — a resize is the one moment a stale face is guaranteed wrong.
        /// </summary>
        public void SetFaceSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (width == faceWidth && height == faceHeight) return;
            faceWidth  = width;
            faceHeight = height;
            ClearCache();
        }

        /// <summary>Releases every baked face. Idempotent; safe to call mid-queue.</summary>
        public void ClearCache()
        {
            foreach (RenderTexture rt in _cache.Values)
            {
                if (rt == null) continue;
                rt.Release();
                DestroyObj(rt);
            }
            _cache.Clear();
        }

        private void OnDestroy()
        {
            ClearCache();
            _waiting.Clear();
            _queue.Clear();
            if (_rig != null) _rig.Unbind();
            if (_rigRoot != null) { DestroyObj(_rigRoot); _rigRoot = null; }
        }

        // ── Queue ───────────────────────────────────────────────────────────────────

        // Iterative, not recursive. When LoadArt completes synchronously — which the editor
        // provider always does — the art callback calls back into here, and a naive recursive pump
        // would put one stack frame per card between the first Bake and the last. The guard turns
        // that into a loop: an inner completion clears _awaitingArt and returns, and the outer
        // while picks up the next card.
        //
        // ONE CARD'S BAKE MUST NOT COST THE WALL (#230). The pump usually runs from inside an
        // Addressables completion callback, so a throw here does not land in a caller that can
        // deal with it — it unwinds into ResourceManager.Update, the queue stops being pumped by
        // anything, and every remaining tile stays blank forever with no further clue. That is a
        // catastrophic response to one card failing to bind. So each card is bounded: it logs,
        // delivers no face, and the wall carries on. The bug this was written for was
        // WaitForCompletion re-entering ResourceManager.Update (see AddressableUiSpriteProvider,
        // which now prewarms so it cannot happen on that path) — but the guard is deliberately
        // not specific to it, because the next lazily-resolved Addressable on the card-build path
        // arms the same trap and nothing would report it.
        private void PumpQueue()
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                while (_queue.Count > 0 && !_awaitingArt)
                {
                    CardDefinition next = _queue.Dequeue();
                    try
                    {
                        BeginBake(next);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[CardFaceBaker] bake of '{next?.Id}' threw and was skipped; " +
                                       $"its tile will be blank. {ex}");
                        // BeginBake may have died after arming the wait, and no callback is coming.
                        _awaitingArt = false;
                        if (next != null) Deliver(next.Id, null);
                    }
                }
            }
            finally { _pumping = false; }
        }

        private void BeginBake(CardDefinition def)
        {
            if (_captureFailed) { Deliver(def.Id, null); return; }

            CardPresenter rig = Rig();
            // Sticky, so a wall of 400 cards logs the reason once rather than 400 times.
            if (rig == null) { _captureFailed = true; Deliver(def.Id, null); return; }

            // Unbind first: Bind subscribes to ICardLiveState.Changed, and every card shares one
            // live state here, so rebinding without releasing would stack a subscription per card.
            rig.Unbind();
            rig.Bind(def, _printed);

            _awaitingArt = true;
            _art.LoadArt(def.ArtId, sprite => OnArtReady(def, sprite));
        }

        // May run inside BeginBake (synchronous provider) or frames later (Addressables).
        private void OnArtReady(CardDefinition def, Sprite sprite)
        {
            // Nothing but this callback advances the queue, so a provider that fires twice — or
            // once after we were destroyed — would otherwise photograph the rig out of turn.
            if (!_waiting.ContainsKey(def.Id) || _rig == null) return;

            // Bounded for the same reason the pump is (#230): nothing but this callback advances
            // the queue, so a throw between here and _awaitingArt = false would wedge the wall
            // permanently. The finally is the queue's only guarantee of forward progress.
            try
            {
                // A null sprite is a legitimate outcome — art not generated yet — and still gets a
                // face. SetArt hides the illustration quad and the frame reads as an empty window.
                _rig.SetArt(sprite);

                RenderTexture rt = CardSnapshot.Capture(_rigVisual, faceWidth, faceHeight);
                if (rt != null)
                {
                    _cache[def.Id] = rt;
                }
                else
                {
                    // Distinguish permanent (missing layer) from transient (RT alloc / memory pressure).
                    // Latch only on the permanent case — an alloc failure should not poison every card
                    // that follows; the next attempt might succeed once memory is available again.
                    // Capture already logged the specific reason.
                    if (LayerMask.NameToLayer(CardSnapshot.CaptureLayerName) < 0)
                        _captureFailed = true;
                    else
                        Debug.LogWarning($"[CardFaceBaker] capture returned null for '{def.Handle}' " +
                                         "(RT alloc failure — see above); tile will be blank.");
                }

                Deliver(def.Id, rt);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CardFaceBaker] '{def.Handle}' failed after its art resolved; " +
                               $"its tile will be blank. {ex}");
                Deliver(def.Id, null);   // no-op if Deliver above already ran
            }
            finally
            {
                _awaitingArt = false;
            }

            PumpQueue();
        }

        private void Deliver(int cardId, Texture face)
        {
            if (!_waiting.TryGetValue(cardId, out List<Action<Texture>> list)) return;
            _waiting.Remove(cardId);
            for (int i = 0; i < list.Count; i++) list[i]?.Invoke(face);
        }

        // ── Rig ─────────────────────────────────────────────────────────────────────

        private CardPresenter Rig()
        {
            if (_rig != null) return _rig;
            if (_board == null)
            {
                Debug.LogWarning("[CardFaceBaker] Initialize() was never called — no faces can be baked.");
                return null;
            }

            _rigRoot = new GameObject("__CardFaceBakeRig");
            _rigRoot.transform.SetParent(transform, false);
            _rigRoot.transform.position = RigPark;

            _rig = _board.BuildDetachedCard(_rigRoot.transform);
            // "Visual" is CreatePresenter's wrapper around every renderer on the card, and it is
            // what CardDeathDissolve photographs too — a face and a death burn are the same shot.
            _rigVisual = _rig != null ? _rig.transform.Find("Visual") : null;
            if (_rigVisual == null)
            {
                Debug.LogWarning("[CardFaceBaker] bake rig has no 'Visual' child — CreatePresenter's " +
                                 "wrapper changed and CardDeathDissolve will have missed it too.");
                _rig = null;
            }
            return _rig;
        }

        // Edit mode forbids Destroy; the offscreen face preview runs there.
        private static void DestroyObj(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
