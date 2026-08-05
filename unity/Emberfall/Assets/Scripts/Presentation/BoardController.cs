using System.Collections;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Session;
using TMPro;
using UnityEngine;


namespace Emberfall.Presentation
{
    // Owns all CardPresenter instances on the board and in hand.
    // Called by GameBootstrap: Initialize() once, then OnStateUpdated() on every engine tick.
    // Diffs successive GameState snapshots → spawn/move/destroy CardPresenters accordingly.
    public sealed class BoardController : MonoBehaviour
    {
        [SerializeField] private BoardAnchors boardAnchors;

        // Runtime card visual dimensions (world units). Internal, not private: HandFanLayout
        // derives the fan pitch from CardW, and a second copy of this number has already gone
        // stale once (the §1.1 pitch table was written against an assumed 1.3 × 1.8).
        internal const float CardW = 1.17f;
        internal const float CardH = 1.65f;

        // How far a bulwarked minion steps out of its row toward the centerline.
        private const float BulwarkRowOffset = 0.40f;

        // Hand cards draw above board content and below the match-end banner (sortingOrder 100).
        // Order within the hand is the slot index, so the fan layers left-to-right and the
        // right-most card at any point is the topmost — the hit-test rule in hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8.
        private const int HandSortBase = 50;

        private ICardDatabase _db;
        private PlayerId _p0Id;
        private PlayerId _p1Id;
        private PlayerId _localHumanId;
        private InputRouter _inputRouter;
        private MatchRunner _matchRunner;
        private IArtProvider _artProvider;
        private IUiSpriteProvider _uiSprites;

        private readonly Dictionary<InstanceId, CardPresenter>  _presenters  = new();
        private readonly Dictionary<InstanceId, CardLiveState>  _liveStates  = new();

        // Card-backs for projected hidden hands (dev-docs/architecture/determinism-and-projection.md:23). Hidden cards all
        // share InstanceId(0) with an empty DefinitionId, so they can't ride the id-keyed path —
        // they're tracked positionally per owner instead.
        private readonly Dictionary<PlayerId, List<CardPresenter>> _cardBacks = new();

        // Tracks every InstanceId that was live last tick so we can destroy departed cards.
        private readonly HashSet<InstanceId> _lastLive = new();
        private readonly HashSet<InstanceId> _thisLive = new();

        // Draw flights spawned during the current tick, so a multi-card tick deals rather than
        // dumps. The opening hand is the case that makes this worth having: four cards arriving
        // in one snapshot fly as a sequence instead of a single blur.
        private const float DrawStagger = 0.06f;
        private int _drawsThisTick;

        // ── Death dissolve (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §5) ────────────────────────────────────
        //
        // THE ORDERING PROBLEM. MatchRunner.PlaybackLoop fires OnStateUpdated and THEN
        // OnMatchEvent off the same queue entry, in the same frame. A minion that dies is already
        // absent from that snapshot's board list, so the departed-card sweep below used to destroy
        // its presenter before the CardDestroyed fact it belongs to had been raised at all — the card
        // was gone, and nothing could have animated it.
        //
        // So the sweep no longer destroys: it PARKS ids, and a WaitForEndOfFrame flush resolves
        // them once the same frame's events have landed. End of frame is the right boundary
        // because it is unambiguously after OnMatchEvent and unambiguously before the next
        // PlaybackLoop iteration (which dequeues at most one entry per frame).
        private readonly List<InstanceId>    _pendingRemoval = new();
        private readonly HashSet<InstanceId> _diedThisTick   = new();
        private Coroutine _removalFlush;

        private const float DeathDissolveDur = 0.40f;

        // ── Public API ──────────────────────────────────────────────────────────────

        public void Initialize(ICardDatabase db, BoardAnchors anchors, PlayerId p0Id, PlayerId p1Id,
                               InputRouter inputRouter = null, MatchRunner matchRunner = null,
                               IArtProvider artProvider = null, PlayerId? localHumanId = null)
        {
            _db          = db;
            boardAnchors = anchors;
            _p0Id        = p0Id;
            _p1Id        = p1Id;
            // Attack affordances key off the human seat. Hot-seat: p0. Networked: the local
            // player, who may sit at either seat — NetworkedGameBootstrap passes it explicitly.
            _localHumanId = localHumanId ?? p0Id;
            _inputRouter = inputRouter;
            _matchRunner = matchRunner;
            _artProvider = artProvider;
#if UNITY_EDITOR
            // Editor play mode: serve art from AssetDatabase — fast iteration, no content
            // build required (dev-docs/pipelines/art-and-rendering.md:13). Player builds resolve through Addressables.
            _artProvider ??= new EditorArtProvider();
            _uiSprites   ??= new EditorUiSpriteProvider();
#else
            _artProvider ??= new AddressableArtProvider();
            _uiSprites   ??= new AddressableUiSpriteProvider();
#endif
            // Targeting-state visibility: while the router has a targeting state armed,
            // only valid targets glow (binder suppresses readiness glow for the duration).
            if (_inputRouter != null)
                _inputRouter.TargetingChanged += OnTargetingChanged;

            // Deaths are read here, not only in CardMaterialBinder, because the controller is what
            // owns the presenter's lifetime and therefore the only place that can hold a card
            // alive long enough to burn.
            if (_matchRunner != null)
                _matchRunner.OnMatchEvent += OnMatchEventForDeaths;
        }

        private void OnDestroy()
        {
            if (_inputRouter != null)
                _inputRouter.TargetingChanged -= OnTargetingChanged;
            if (_matchRunner != null)
                _matchRunner.OnMatchEvent -= OnMatchEventForDeaths;
        }

        // EffectInterpreter.cs:148 and RulesEngine.cs:94 both emit CardDestroyed with SOURCE set and no
        // target — the dying thing is the subject of its own death. Reading both fields keeps this
        // correct either way, and costs nothing: a weapon's CardDestroyed fact names an id with no presenter,
        // which the sweep simply never looks up.
        private void OnMatchEventForDeaths(IReadOnlyList<GameEvent> batch)
        {
            foreach (GameEvent e in batch)
            {
                if (e.Kind != GameEventKind.CardDestroyed) continue;
                if (e.Source.HasValue) _diedThisTick.Add(e.Source.Value);
                if (e.Target.HasValue) _diedThisTick.Add(e.Target.Value);
            }
        }

        private void OnTargetingChanged(HashSet<InstanceId> targets)
        {
            foreach (var kvp in _presenters)
            {
                var binder = kvp.Value != null ? kvp.Value.GetComponent<CardMaterialBinder>() : null;
                binder?.SetTargetOverride(targets == null ? (bool?)null : targets.Contains(kvp.Key));
            }
        }

        public void OnStateUpdated(GameState state)
        {
            if (_db == null) return;

            // Defensive: if the end-of-frame flush could not run (component disabled mid-tick,
            // coroutine stopped), resolve last tick's parked ids now — while _diedThisTick still
            // describes THEM — rather than carrying them into this tick's answer.
            if (_pendingRemoval.Count > 0) FlushRemovals();
            _diedThisTick.Clear();

            _thisLive.Clear();
            _drawsThisTick = 0;

            foreach (PlayerState ps in state.Players)
            {
                // Attack-affordance context (CardLiveState.BuildReadiness): only the local
                // human's minions, on their own Main turn, show ready-glow / exhausted-dim.
                bool ownerMayAct = ps.Id == _localHumanId
                    && state.ActivePlayer == ps.Id
                    && state.Phase == TurnPhase.Main;

                SyncZone(ps.Id, ps.Board, Zone.Board, ownerMayAct);
                SyncZone(ps.Id, ps.Hand,  Zone.Hand,  ownerMayAct);
                SyncWeapon(ps.Id, ps.Weapon);
            }

            // Park presenters for cards that left hand and board — resolved at end of frame, once
            // this tick's CardDestroyed facts have told us WHICH of them died (see _pendingRemoval).
            foreach (InstanceId id in _lastLive)
                if (!_thisLive.Contains(id))
                    _pendingRemoval.Add(id);

            if (_pendingRemoval.Count > 0)
            {
                if (isActiveAndEnabled && _removalFlush == null)
                    _removalFlush = StartCoroutine(FlushRemovalsAtEndOfFrame());
                else if (!isActiveAndEnabled)
                    FlushRemovals();   // no coroutine possible; take the immediate-destroy path
            }

            // Swap: _lastLive becomes the current live set for the next tick.
            _lastLive.Clear();
            foreach (InstanceId id in _thisLive) _lastLive.Add(id);
        }

        // ── Private helpers ─────────────────────────────────────────────────────────

        private void SyncZone(PlayerId owner, List<CardInstance> cards, Zone zone, bool ownerMayAct)
        {
            // Projected hidden hand: every entry is the redaction sentinel (id 0, empty
            // DefinitionId — a4 design §6). _db.Get("") would THROW and abort the whole tick,
            // so sentinel hands take the positional card-back path instead.
            if (zone == Zone.Hand)
            {
                // Re-solve the fan BEFORE any hand anchor is resolved this tick: the fan's
                // pitch is a function of hand count, so every card moves when one is drawn
                // or played (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8).
                boardAnchors?.SetHandCount(owner, cards.Count);

                bool hidden = cards.Count > 0 && cards[0].Id.Value == 0;
                SyncHiddenHand(owner, hidden ? cards.Count : 0);
                if (hidden) return;
            }

            for (int i = 0; i < cards.Count; i++)
            {
                CardInstance ci = cards[i];
                _thisLive.Add(ci.Id);
                GetOrCreate(ci, owner, zone, i, ownerMayAct);
            }
        }

        // Maintains N card-back presenters for a hidden hand: no def bind, no art, no clickable,
        // no CardMaterialBinder, no CardLiveState — a card-back must never exist with id 0 or
        // redacted events would light it up (a4 design §6 / §11 finding 3).
        private void SyncHiddenHand(PlayerId owner, int count)
        {
            if (!_cardBacks.TryGetValue(owner, out List<CardPresenter> backs))
            {
                if (count == 0) return;
                backs = new List<CardPresenter>();
                _cardBacks[owner] = backs;
            }

            while (backs.Count > count)
            {
                CardPresenter p = backs[backs.Count - 1];
                backs.RemoveAt(backs.Count - 1);
                if (p != null) DestroyObj(p.gameObject);
            }

            while (backs.Count < count)
            {
                int slot = backs.Count;
                Transform anchor = boardAnchors != null
                    ? boardAnchors.AnchorFor(owner, Zone.Hand, slot)
                    : transform;

                CardPresenter presenter = CreatePresenter(anchor != null ? anchor : transform);
                var binder = presenter.GetComponent<CardMaterialBinder>();
                if (binder != null) DestroyObj(binder);
                presenter.SetHidden();
                SetCardSorting(presenter, Zone.Hand, slot);
                LaunchDrawFlight(presenter.transform, owner);
                backs.Add(presenter);
            }
        }

        // Sends a just-spawned hand card flying in from its owner's deck pile. Silent no-op when
        // there is no DeckAnchor (a stripped test scene), because a missing decoration must never
        // cost a card its position.
        private void LaunchDrawFlight(Transform card, PlayerId owner)
        {
            Transform deck = boardAnchors != null ? boardAnchors.AnchorFor(owner, Zone.Deck, 0) : null;
            if (deck == null) return;
            CardDrawFlight.Launch(card, deck.position, _drawsThisTick * DrawStagger);
            _drawsThisTick++;
        }

        private void GetOrCreate(CardInstance ci, PlayerId owner, Zone zone, int slotIndex, bool ownerMayAct)
        {
            bool spawned = false;
            if (!_presenters.TryGetValue(ci.Id, out CardPresenter presenter))
            {
                spawned = true;
                Transform anchor = boardAnchors != null
                    ? boardAnchors.AnchorFor(owner, zone, slotIndex)
                    : transform;

                presenter = CreatePresenter(anchor);
                var live = new CardLiveState(ci.Id);
                _liveStates[ci.Id] = live;

                CardDefinition def = _db.Get(ci.DefinitionId);
                if (def != null)
                {
                    presenter.Bind(def, live);
                    _artProvider?.LoadArt(def.ArtId, presenter.SetArt);
                }

                presenter.GetComponent<CardMaterialBinder>()?.Init(live, _matchRunner);

                // Attach the raycast descriptor when InputRouter is present (hot-seat mode) —
                // InputRouter reads identity off it after its own raycast (EF-048).
                if (_inputRouter != null)
                {
                    var clickable = presenter.gameObject.AddComponent<ClickableCard>();
                    clickable.Init(ci.Id, owner, zone, presenter);
                }

                _presenters[ci.Id] = presenter;
            }
            else if (_inputRouter != null)
            {
                // Keep zone current for hand→board transitions.
                var clickable = presenter.GetComponent<ClickableCard>();
                clickable?.UpdateZone(zone);
            }

            // Update live state regardless (stats may have changed).
            if (_liveStates.TryGetValue(ci.Id, out CardLiveState liveState))
                liveState.UpdateFrom(ci, zone == Zone.Board && ownerMayAct);

            // Re-parent to the correct anchor if the card moved slots.
            if (boardAnchors != null)
            {
                Transform targetAnchor = boardAnchors.AnchorFor(owner, zone, slotIndex);
                if (targetAnchor != null && presenter.transform.parent != targetAnchor)
                {
                    presenter.transform.SetParent(targetAnchor, false);
                    presenter.transform.localPosition = Vector3.zero;
                    presenter.transform.localRotation = Quaternion.identity;
                }
            }

            SetCardSorting(presenter, zone, slotIndex);

            // A card that appears in hand having had no presenter last tick was drawn (or
            // generated straight to hand, which deals the same way). Must run AFTER the
            // re-parent block above — the flight resolves its origin in the anchor's local
            // space and zeroes the card into it.
            if (spawned && zone == Zone.Hand) LaunchDrawFlight(presenter.transform, owner);

            // Front-row bulwark (decision 2026-07-17): bulwarked board minions step toward
            // the centerline so blockers read spatially, not by glow color. Re-applied
            // every sync — bulwark can be gained/lost without a slot change.
            if (zone == Zone.Board)
            {
                float toward = (owner == _p0Id) ? 1f : -1f;
                bool hasBulwark = ci.Keywords.Contains(Keyword.BULWARK);
                presenter.transform.localPosition =
                    hasBulwark ? new Vector3(0f, toward * BulwarkRowOffset, 0f) : Vector3.zero;
            }
        }

        /// <summary>
        /// The equipped weapon is NOT drawn as a card. HeroSocketsView renders it as the forged
        /// ring docked beside the hero power, with attack and durability on the ring's two stat
        /// bosses, so a CardPresenter at WeaponAnchor is a second copy of the same weapon sitting
        /// directly behind the first — which is exactly what it looked like: a whole card showing
        /// behind the ring (Sam, watching it live, 2026-07-25).
        ///
        /// The id still joins _thisLive so that if anything ever does register a presenter for a
        /// weapon, breaking it still falls out of the normal departed-card sweep rather than
        /// leaking a view.
        /// </summary>
        private void SyncWeapon(PlayerId owner, CardInstance weapon)
        {
            if (weapon == null) return;
            _thisLive.Add(weapon.Id);
        }

        // Hand cards overlap, so their draw order has to be explicit. Board cards never overlap
        // and sit at the group default; the fan's per-index z-stagger (HandFanLayout) keeps
        // opaque materials and the physics hit-test agreeing with this ordering.
        //
        // Hover-reveal is hand-only. Disabling the component on the board path is what returns a
        // played card to its resting pose — HandCardHover.OnDisable snaps the visual back, so a
        // card can never land on the board still lifted and enlarged.
        private static void SetCardSorting(CardPresenter presenter, Zone zone, int slotIndex)
        {
            bool inHand = zone == Zone.Hand;
            int  order  = inHand ? HandSortBase + slotIndex : 0;

            // For a hand card the hover component OWNS sortingOrder, because it may be holding a
            // boost this method knows nothing about. Writing the base order here first would stomp
            // that boost on every state tick and silently kill the re-sort mid-hover.
            var hover = presenter.GetComponent<HandCardHover>();
            if (hover != null)
            {
                hover.enabled = inHand;
                if (inHand)
                {
                    hover.SetBaseOrder(order);
                    return;
                }
            }

            var group = presenter.GetComponent<UnityEngine.Rendering.SortingGroup>();
            if (group != null) group.sortingOrder = order;
        }

        private IEnumerator FlushRemovalsAtEndOfFrame()
        {
            yield return new WaitForEndOfFrame();
            _removalFlush = null;
            FlushRemovals();
        }

        // Every parked id is resolved together, so a board wipe burns as one event rather than a
        // cascade of one-at-a-time destroys — each dying card gets its own snapshot, its own
        // material instance and its own coroutine, and they overlap.
        private void FlushRemovals()
        {
            for (int i = 0; i < _pendingRemoval.Count; i++)
            {
                InstanceId id = _pendingRemoval[i];
                DestroyPresenter(id, died: _diedThisTick.Contains(id));
            }
            _pendingRemoval.Clear();
        }

        /// <param name="died">
        /// True when this tick raised CardDestroyed for the id. Cards that merely LEFT the board — played
        /// from hand, returned to hand, discarded, shuffled back — keep the immediate destroy: a
        /// burn would read as "this card was killed", which for those is a lie.
        /// </param>
        private void DestroyPresenter(InstanceId id, bool died = false)
        {
            if (_presenters.TryGetValue(id, out CardPresenter p))
            {
                _presenters.Remove(id);   // out of the diff immediately, even while it burns
                if (p != null)
                {
                    if (died && Application.isPlaying && TryStartDissolve(p))
                    {
                        _liveStates.Remove(id);
                        return;
                    }
                    p.Unbind();
                    DestroyObj(p.gameObject);
                }
            }
            _liveStates.Remove(id);
        }

        // The card root outlives the state that justified it for DeathDissolveDur seconds, so
        // everything that could still act on it is switched off first: no clicks, no hit-test, no
        // hover lift, no shader binder chasing a live state that has already been dropped.
        private bool TryStartDissolve(CardPresenter p)
        {
            Transform visual = p.transform.Find("Visual");
            if (visual == null) return false;

            p.Unbind();
            // DESTROYED, not disabled. A disabled MonoBehaviour still receives C# delegate
            // callbacks, so a merely-disabled binder stays subscribed to MatchRunner.OnMatchEvent
            // and would try to StartCoroutine on itself — which errors. Destroy runs its OnDestroy,
            // which is the only thing that actually unsubscribes.
            var binder = p.GetComponent<CardMaterialBinder>();
            if (binder != null) DestroyObj(binder);
            var hover = p.GetComponent<HandCardHover>();
            if (hover != null) hover.enabled = false;
            var clickable = p.GetComponent<ClickableCard>();
            if (clickable != null) clickable.enabled = false;
            var col = p.GetComponent<BoxCollider>();
            if (col != null) col.enabled = false;

            GameObject root = p.gameObject;
            return root.AddComponent<CardDeathDissolve>()
                       .Play(visual, DeathDissolveDur, () => DestroyObj(root));
        }

        // CreatePresenter runs from the offscreen dissolve preview as well as from play mode, and
        // edit mode refuses Destroy — without this the harness would build every card with the
        // primitives' auto-generated MeshColliders still attached, and log an error per quad.
        private static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// <summary>
        /// Builds ONE detached card visual under <paramref name="parent"/> through the shipping
        /// <see cref="CreatePresenter"/> — the same renderers, the same frame layer, the same
        /// TMP labels a card in a match gets — and hands back its presenter, unbound.
        ///
        /// DETACHED MEANS NOT IN THE MATCH. It is registered in nothing: not _presenters, not
        /// _liveStates, not _thisLive. The diffing loop therefore cannot see it, cannot re-parent
        /// it to an anchor, and cannot destroy it. It carries no ClickableCard and its
        /// CardMaterialBinder is never Init'd, so nothing drives it and it cannot act — the caller
        /// owns its lifetime and calls Bind/SetArt itself.
        ///
        /// It exists so <see cref="CardFaceBaker"/> can photograph the REAL card for the collection
        /// wall instead of the wall re-implementing the card front in UI Toolkit. Two
        /// implementations of one card face would drift on the first frame-art change; one
        /// implementation and a camera cannot.
        ///
        /// <see cref="Initialize"/> must have run first: the frame/rarity sprites resolve through
        /// the IUiSpriteProvider that Initialize picks (Editor vs Addressables).
        /// </summary>
        public CardPresenter BuildDetachedCard(Transform parent) => CreatePresenter(parent);

#if UNITY_EDITOR
        /// <summary>
        /// Builds one real card through the shipping <see cref="CreatePresenter"/>/GetOrCreate path
        /// for Emberfall/Preview Card Dissolve. Editor-only: the preview must burn the actual card
        /// hierarchy — every renderer, real frame art, real TMP labels — because a mock card would
        /// only ever prove the harness agrees with itself.
        /// </summary>
        public CardPresenter EditorBuildCard(CardInstance ci, PlayerId owner, Zone zone)
        {
            _thisLive.Add(ci.Id);
            GetOrCreate(ci, owner, zone, 0, ownerMayAct: false);
            return _presenters.TryGetValue(ci.Id, out CardPresenter p) ? p : null;
        }
#endif

        // Builds a minimal world-space card visual hierarchy (no external prefab needed).
        // Phase C step 5: upgraded to TextMeshPro. Replace primitives with shader-driven prefab when card art lands.
        private CardPresenter CreatePresenter(Transform parent)
        {
            var root = new GameObject("Card");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            // One SortingGroup re-layers the whole card — quads, sprites and TMP labels
            // together — off a single integer, which is what makes the fan's overlap (and
            // later the hover-reveal in hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §8) a one-line change.
            root.AddComponent<UnityEngine.Rendering.SortingGroup>();

            // Background quad (SG_CardComposite body) — remove auto-generated MeshCollider.
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "BG";
            bg.transform.SetParent(root.transform, false);
            bg.transform.localScale    = new Vector3(CardW, CardH, 1f);
            bg.transform.localPosition = Vector3.zero;
            DestroyObj(bg.GetComponent<MeshCollider>());
            var bodyRenderer = bg.GetComponent<Renderer>();
            MaterialCatalog.Assign(bodyRenderer, MaterialCatalog.CardBody);

            // Glow overlay — slightly larger, just in front. SG_Glow material assigned later.
            var glowGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            glowGo.name = "GlowOverlay";
            glowGo.transform.SetParent(root.transform, false);
            glowGo.transform.localScale    = new Vector3(CardW + 0.15f, CardH + 0.15f, 1f);
            glowGo.transform.localPosition = new Vector3(0f, 0f, 0.005f);
            DestroyObj(glowGo.GetComponent<MeshCollider>());
            var glowRenderer = glowGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(glowRenderer, MaterialCatalog.Glow);
            // GO stays active, renderer starts disabled: CardMaterialBinder toggles
            // Renderer.enabled, which is inert on an inactive GameObject.
            glowRenderer.enabled = false;

            // Aegis underlay — slightly larger, just behind. SG_Aegis material assigned later.
            var aegisGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            aegisGo.name = "AegisUnderlay";
            aegisGo.transform.SetParent(root.transform, false);
            aegisGo.transform.localScale    = new Vector3(CardW + 0.21f, CardH + 0.21f, 1f);
            aegisGo.transform.localPosition = new Vector3(0f, 0f, 0.01f);
            DestroyObj(aegisGo.GetComponent<MeshCollider>());
            var aegisRenderer = aegisGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(aegisRenderer, MaterialCatalog.Aegis);
            // Same as the glow overlay: active GO, disabled renderer (bulwark/shield ring
            // never displayed before this — the binder's enable was a no-op).
            aegisRenderer.enabled = false;

            // Art window — upper card face, in front of BG, behind labels. Texture assigned
            // via CardPresenter.SetArt when the IArtProvider resolves; hidden until then.
            var artGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            artGo.name = "Art";
            artGo.transform.SetParent(root.transform, false);
            artGo.transform.localScale    = new Vector3(CardW * 0.92f, CardH * 0.52f, 1f);
            artGo.transform.localPosition = new Vector3(0f, CardH * 0.12f, -0.005f);
            DestroyObj(artGo.GetComponent<MeshCollider>());
            var artRenderer = artGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(artRenderer, MaterialCatalog.CardArt);
            artGo.SetActive(false);

            // Frame border — full card face, in front of the (opaque) art quad so the border ring
            // overlaps the illustration edges; its transparent window lets the art show through.
            // Sprite (frame_{type}_{rarity}) assigned by CardPresenter.ApplyFrame (D2).
            var frameRenderer = MakeSprite(root.transform, "Frame",
                new Vector3(0f, 0f, -0.006f), new Vector2(CardW, CardH));

            // Frost overlay — the frost cue (SG_Frost), card-sized, driven by CardMaterialBinder's
            // _FrostAmount. Its own quad because SG_Frost is an overlay shader: it has _FrostOpacity
            // and _RefractStrength and expects something behind it to frost OVER. The body quad
            // wears SG_CardComposite, which does not declare _FrostAmount at all, so pushing the
            // property there wrote it nowhere.
            //
            // Z = -0.008 puts it in FRONT of the art (-0.005) and frame (-0.006) but BEHIND every
            // TMP label (-0.010) and the gem (-0.012). That is deliberate: a frosted minion still
            // has to be readable, so the ice covers the illustration and never the numbers. At
            // -0.02 it sat in front of the labels and, writing depth on the transparent queue,
            // made them vanish entirely while drawing no ice of its own.
            var frostGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            frostGo.name = "FrostOverlay";
            frostGo.transform.SetParent(root.transform, false);
            frostGo.transform.localScale    = new Vector3(CardW, CardH, 1f);
            frostGo.transform.localPosition = new Vector3(0f, 0f, -0.008f);
            DestroyObj(frostGo.GetComponent<MeshCollider>());
            var frostRenderer = frostGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(frostRenderer, MaterialCatalog.Frost);
            // Same pattern as glow and aegis: active GO, disabled renderer. The binder enables it
            // when AuraSet.Frosted arrives, so an unfrosted card costs no draw call.
            frostRenderer.enabled = false;

            // Rarity gem — bottom-center; sprite (gem_{rarity}) assigned per-rarity by ApplyFrame.
            var gemRenderer = MakeSprite(root.transform, "Gem",
                new Vector3(0f, -CardH * 0.42f, -0.012f), new Vector2(CardW * 0.16f, CardW * 0.16f));

            // Keyword icon strip — up to 4 icons laid left-to-right just below the art window.
            // Sprites (icon_kw_*) assigned from live keywords by RebuildKeywords (§4).
            const int kIconSlots = 4;
            float iconSize = CardW * 0.15f;
            float iconGap  = iconSize * 1.05f;
            float iconY    = -CardH * 0.20f;
            var keywordIcons = new SpriteRenderer[kIconSlots];
            for (int i = 0; i < kIconSlots; i++)
            {
                float x = (i - (kIconSlots - 1) * 0.5f) * iconGap;
                keywordIcons[i] = MakeSprite(root.transform, $"Kw{i}",
                    new Vector3(x, iconY, -0.012f), new Vector2(iconSize, iconSize));
                keywordIcons[i].gameObject.SetActive(false);
            }

            // BoxCollider on root for OnMouseDown — sized to card face.
            var col = root.AddComponent<BoxCollider>();
            col.size = new Vector3(CardW, CardH, 0.1f);

            // Name — top strip
            TextMeshPro nameLabel   = MakeLabel(root.transform, "Name", new Vector3(0f,             CardH *  0.39f, -0.01f), 3.0f, Color.white);
            // Cost — top-left boss. White + rim per §3.1; the blue recolor is a pending
            // nudge-list item, deliberately not applied yet.
            TextMeshPro costLabel   = MakeLabel(root.transform, "Cost", new Vector3(-CardW * 0.38f,  CardH *  0.42f, -0.01f), 3.75f, Color.white, statOutline: true);
            // Stat digits are white with a black rim (§3.1) — the rim is what keeps them
            // legible on any frame's boss art, any rarity.
            // Attack — bottom-left (minions + weapons)
            TextMeshPro attackLabel = MakeLabel(root.transform, "Atk",  new Vector3(-CardW * 0.35f, -CardH *  0.42f, -0.01f), 3.75f, Color.white, statOutline: true);
            // Health — bottom-right (minions only)
            TextMeshPro healthLabel = MakeLabel(root.transform, "HP",   new Vector3( CardW * 0.35f, -CardH *  0.42f, -0.01f), 3.75f, Color.white, statOutline: true);
            // Durability — bottom-right (weapons only; occupies the health slot, mutually exclusive)
            TextMeshPro durabilityLabel = MakeLabel(root.transform, "Dur", new Vector3( CardW * 0.35f, -CardH *  0.42f, -0.01f), 3.75f, Color.white, statOutline: true);
            // Keywords — center strip
            TextMeshPro kwLabel     = MakeLabel(root.transform, "KW",   new Vector3(0f,             -CardH *  0.08f, -0.01f), 1.8f, new Color(0.9f, 0.85f, 0.6f));

            // Rules text — band between the keyword icon strip and the stat row. Unlike
            // MakeLabel's single-line names this wraps; auto-sizes down for long lines.
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(root.transform, false);
            bodyGo.transform.localPosition = new Vector3(0f, -CardH * 0.30f, -0.01f);
            var bodyLabel = bodyGo.AddComponent<TextMeshPro>();
            bodyGo.GetComponent<Renderer>().sortingOrder = 10; // over the frame, same as MakeLabel
            var bodyFont = BodyFont();   // EF-050: text face here, display face everywhere else
            if (bodyFont != null) bodyLabel.font = bodyFont;
            bodyLabel.fontSize  = 1.0f;
            bodyLabel.alignment = TextAlignmentOptions.Center;
            bodyLabel.color     = new Color(0.92f, 0.92f, 0.92f);
            bodyLabel.rectTransform.sizeDelta = new Vector2(CardW * 0.90f, CardH * 0.22f);
            bodyLabel.textWrappingMode = TextWrappingModes.Normal;
            bodyLabel.overflowMode     = TextOverflowModes.Ellipsis;
            bodyLabel.enableAutoSizing = true;
            bodyLabel.fontSizeMax      = 1.0f;
            bodyLabel.fontSizeMin      = 0.5f;

            // Wrap every visual under one child transform. Hover-reveal lifts and scales THIS,
            // never the root — the BoxCollider above stays on the root and therefore never moves,
            // which is what makes the reveal flicker-proof (see HandCardHover). Re-parenting after
            // the fact keeps the construction above untouched; the wrapper sits at identity, so
            // every local pose already computed is still correct.
            var visualGo = new GameObject("Visual");
            visualGo.transform.SetParent(root.transform, false);
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = root.transform.GetChild(i);
                if (child != visualGo.transform) child.SetParent(visualGo.transform, true);
            }

            var hover = root.AddComponent<HandCardHover>();
            hover.Init(visualGo.transform, root.GetComponent<UnityEngine.Rendering.SortingGroup>());
            hover.enabled = false;   // hand-only; ConfigureForZone turns it on

            var presenter = root.AddComponent<CardPresenter>();
            presenter.Setup(nameLabel, costLabel, attackLabel, healthLabel, durabilityLabel, kwLabel, bodyRenderer, artRenderer, bodyLabel);
            presenter.SetupFrameLayer(frameRenderer, gemRenderer, keywordIcons, _uiSprites);

            // CardMaterialBinder drives shader MPB floats; Init() called after ICardLiveState exists.
            var binder = root.AddComponent<CardMaterialBinder>();
            binder.SetupRenderers(bodyRenderer, glowRenderer, aegisRenderer, frostRenderer);

            return presenter;
        }

        // Creates a world-space SpriteRenderer child sized to exact world dimensions via Sliced
        // draw mode (PPU-independent). Sprite is assigned later by CardPresenter; starts empty.
        private static SpriteRenderer MakeSprite(Transform parent, string goName, Vector3 localPos, Vector2 worldSize)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.drawMode = SpriteDrawMode.Sliced; // lets .size set exact world dimensions regardless of PPU
            sr.size     = worldSize;
            return sr;
        }

        // One shared outlined material for every stat label — per-label .fontMaterial would
        // instance a material per digit and break batching across the whole board.
        private static Material s_statLabelMaterial;

        // EF-050 display face for card names, stats, and digits. Loaded once;
        // TMP's default font remains the fallback when the asset is unavailable.
        private static TMP_FontAsset s_cardFont;

        // Internal, not private: DeckPileView's hover count is a card-face digit by any other
        // name and has to be the same typeface — a second copy of this loader would be a second
        // thing to forget when the display face changes.
        internal static TMP_FontAsset CardFont()
        {
            if (s_cardFont == null)
                s_cardFont = LoadAsset<TMP_FontAsset>(
                    "Assets/Art/Fonts/AlfaSlabOne-Regular SDF.asset", "fonts/AlfaSlabOne-Regular SDF");
            return s_cardFont;
        }

        // EF-050: two typefaces, two roles. CardFont() above is the DISPLAY face and owns the
        // card name and the stat digits; this is the TEXT face and owns the rules box only.
        //
        // Why the split is not taste: at 265 px card width (the true size on the smallest
        // supported phone) body text runs at roughly 1.6x the letter-recognition acuity
        // threshold. There is no margin at that ratio, and a display face spends all of it —
        // slab strokes thicken and counters close first at small sizes, which is exactly the
        // failure mode you cannot see on a monitor.
        //
        // The bed-margin overrun that used to affect this label was a separate bug in the bed
        // and font-window size calculation, already fixed. Do not re-diagnose it as a wrapping
        // or font-width problem.
        private static TMP_FontAsset s_bodyFont;

        internal static TMP_FontAsset BodyFont()
        {
            if (s_bodyFont == null)
                s_bodyFont = LoadAsset<TMP_FontAsset>(
                    "Assets/Art/Fonts/GoogleSans-Regular SDF.asset", "fonts/GoogleSans-Regular SDF");
            return s_bodyFont ?? CardFont();   // missing asset must not blank the rules box
        }

        private static T LoadAsset<T>(string editorPath, string address) where T : Object
        {
#if UNITY_EDITOR
            var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(editorPath);
#else
            var handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<T>(address);
            var asset = handle.WaitForCompletion();
#endif
            if (asset == null)
                Debug.LogWarning($"[BoardController] asset '{address}' not found — using default.");
            return asset;
        }

        private static TextMeshPro MakeLabel(Transform parent, string goName, Vector3 localPos, float fontSize, Color color, bool statOutline = false)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var tmp = go.AddComponent<TextMeshPro>();
            // Labels draw after the frame sprite EXPLICITLY. Inside the card's SortingGroup
            // everything shares order 0 and the tie-break is hierarchy order, which the Visual
            // reparenting loop reverses — the painted frames are opaque at the bosses/plaque,
            // so a frame drawn after the text erases it (the old placeholders were transparent
            // there, which is why this never showed).
            tmp.GetComponent<Renderer>().sortingOrder = 10;
            var font = CardFont();
            if (font != null) tmp.font = font;
            tmp.fontSize  = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;

            if (statOutline)
            {
                if (s_statLabelMaterial == null)
                {
                    s_statLabelMaterial = new Material(tmp.font.material);
                    s_statLabelMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    s_statLabelMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.25f);
                    s_statLabelMaterial.EnableKeyword(ShaderUtilities.Keyword_Outline);
                }
                tmp.fontSharedMaterial = s_statLabelMaterial;
            }

            // Constrain to the card face: without a sized rect TMP renders at unbounded width,
            // so long names ("Throneless King") bleed across neighboring cards. Auto-size down
            // to fit rather than wrap — card names are single-line by design.
            tmp.rectTransform.sizeDelta = new Vector2(CardW * 0.94f, fontSize * 0.22f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Ellipsis;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax      = fontSize;
            tmp.fontSizeMin      = fontSize * 0.45f;
            return tmp;
        }
    }
}
