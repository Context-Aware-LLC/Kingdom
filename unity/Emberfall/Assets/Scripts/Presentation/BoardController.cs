using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    // Owns all CardPresenter instances on the board and in hand.
    // Called by GameBootstrap: Initialize() once, then OnStateUpdated() on every engine tick.
    // Diffs successive GameState snapshots → spawn/move/destroy CardPresenters accordingly.
    public sealed class BoardController : MonoBehaviour
    {
        [SerializeField] private BoardAnchors boardAnchors;

        // Runtime card visual dimensions (world units).
        private const float CardW = 0.78f;
        private const float CardH = 1.10f;

        private ICardDatabase _db;
        private PlayerId _p0Id;
        private PlayerId _p1Id;

        private readonly Dictionary<InstanceId, CardPresenter>  _presenters  = new();
        private readonly Dictionary<InstanceId, CardLiveState>  _liveStates  = new();

        // Tracks every InstanceId that was live last tick so we can destroy departed cards.
        private readonly HashSet<InstanceId> _lastLive = new();
        private readonly HashSet<InstanceId> _thisLive = new();

        // ── Public API ──────────────────────────────────────────────────────────────

        public void Initialize(ICardDatabase db, BoardAnchors anchors, PlayerId p0Id, PlayerId p1Id)
        {
            _db         = db;
            boardAnchors = anchors;
            _p0Id       = p0Id;
            _p1Id       = p1Id;
        }

        public void OnStateUpdated(GameState state)
        {
            if (_db == null) return;

            _thisLive.Clear();

            foreach (PlayerState ps in state.Players)
            {
                SyncZone(ps.Id, ps.Board, Zone.Board);
                SyncZone(ps.Id, ps.Hand,  Zone.Hand);
            }

            // Destroy presenters for cards that left hand and board.
            foreach (InstanceId id in _lastLive)
                if (!_thisLive.Contains(id))
                    DestroyPresenter(id);

            // Swap: _lastLive becomes the current live set for the next tick.
            _lastLive.Clear();
            foreach (InstanceId id in _thisLive) _lastLive.Add(id);
        }

        // ── Private helpers ─────────────────────────────────────────────────────────

        private void SyncZone(PlayerId owner, List<CardInstance> cards, Zone zone)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                CardInstance ci = cards[i];
                _thisLive.Add(ci.Id);
                GetOrCreate(ci, owner, zone, i);
            }
        }

        private void GetOrCreate(CardInstance ci, PlayerId owner, Zone zone, int slotIndex)
        {
            if (!_presenters.TryGetValue(ci.Id, out CardPresenter presenter))
            {
                Transform anchor = boardAnchors != null
                    ? boardAnchors.AnchorFor(owner, zone, slotIndex)
                    : transform;

                presenter = CreatePresenter(anchor);
                var live = new CardLiveState(ci.Id);
                _liveStates[ci.Id] = live;

                CardDefinition def = _db.Get(ci.DefinitionId);
                if (def != null)
                    presenter.Bind(def, live);

                _presenters[ci.Id] = presenter;
            }

            // Update live state regardless (stats may have changed).
            if (_liveStates.TryGetValue(ci.Id, out CardLiveState liveState))
                liveState.UpdateFrom(ci);

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
        }

        private void DestroyPresenter(InstanceId id)
        {
            if (_presenters.TryGetValue(id, out CardPresenter p))
            {
                p.Unbind();
                if (p != null) Destroy(p.gameObject);
                _presenters.Remove(id);
            }
            _liveStates.Remove(id);
        }

        // Builds a minimal world-space card visual hierarchy (no external prefab needed).
        // Phase C step 5 will replace these primitives with the real shader-driven prefab.
        private CardPresenter CreatePresenter(Transform parent)
        {
            var root = new GameObject("Card");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = Vector3.zero;
            root.transform.localRotation = Quaternion.identity;

            // Background quad
            var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bg.name = "BG";
            bg.transform.SetParent(root.transform, false);
            bg.transform.localScale = new Vector3(CardW, CardH, 1f);
            bg.transform.localPosition = Vector3.zero;
            Destroy(bg.GetComponent<MeshCollider>());

            // Name — top strip
            TextMesh nameLabel = MakeLabel(root.transform, "Name", new Vector3(0f, CardH * 0.39f, -0.01f), 28, Color.white);
            // Cost — top-left gem
            TextMesh costLabel = MakeLabel(root.transform, "Cost", new Vector3(-CardW * 0.38f, CardH * 0.42f, -0.01f), 36, new Color(0.4f, 0.7f, 1f));
            // Attack — bottom-left
            TextMesh attackLabel = MakeLabel(root.transform, "Atk", new Vector3(-CardW * 0.35f, -CardH * 0.42f, -0.01f), 36, Color.white);
            // Health — bottom-right
            TextMesh healthLabel = MakeLabel(root.transform, "HP",  new Vector3( CardW * 0.35f, -CardH * 0.42f, -0.01f), 36, Color.white);
            // Keywords — center strip
            TextMesh kwLabel = MakeLabel(root.transform, "KW", new Vector3(0f, -CardH * 0.08f, -0.01f), 18, new Color(0.9f, 0.85f, 0.6f));

            var presenter = root.AddComponent<CardPresenter>();
            presenter.Setup(nameLabel, costLabel, attackLabel, healthLabel, kwLabel, bg.GetComponent<Renderer>());
            return presenter;
        }

        private static TextMesh MakeLabel(Transform parent, string goName, Vector3 localPos, int fontSize, Color color)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var tm = go.AddComponent<TextMesh>();
            tm.fontSize      = fontSize;
            tm.characterSize = 0.045f;
            tm.anchor        = TextAnchor.MiddleCenter;
            tm.alignment     = TextAlignment.Center;
            tm.color         = color;
            return tm;
        }
    }
}
