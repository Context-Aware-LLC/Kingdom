using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using UnityEngine;
using UnityEngine.Rendering;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Physical furniture and semantic mounts for the Royal Council table. Gameplay continues
    /// in the existing XY plane: only the imported model is adapted from glTF's Y-up space.
    /// </summary>
    public sealed class RoyalCouncilBoard : MonoBehaviour
    {
        public const string ResourcePath = "Boards/RoyalCouncil";
        public const float ModelScale = 6f;
        public const float HandCenterY = 4.62f;
        public const float HandDepth = -0.55f;
        private const float LocalHandTiltDegrees = 4f;
        public static readonly float HandTopY = -HandCenterY + HandFanLayout.HandCardH * 0.5f * Mathf.Cos(LocalHandTiltDegrees * Mathf.Deg2Rad);
        // Fill the v06 recesses under their inner lips, not a disc floating above the rim.
        public const float PortraitDiameter = 2.63f;
        public const float SocketDiameter = 0.98f;
        public const float WeaponScale = 1.6f;
        public const float PropTilt = 28f;
        public const float BoardCardScale = 1.25f;
        public const float BoardSlotSpacing = BoardRowGeometry.SlotSpacing + BoardController.CardW * (BoardCardScale - 1f);
        private const float OpponentHandLift = .24f;
        private static readonly Quaternion LocalHandTilt = Quaternion.Euler(-LocalHandTiltDegrees, 0f, 0f);
        private static readonly Vector3 OpponentHandCenter = new Vector3(0f, 6.4f, .5f);
        private static readonly Quaternion OpponentHandTilt = Quaternion.Euler(-70f, 0f, 0f);

        [SerializeField] private Material glassMaterial;
        [SerializeField] private Sprite backdrop;
        private readonly Dictionary<string, Transform> _parts = new Dictionary<string, Transform>();
        private readonly PlayerId[] _players = new PlayerId[2];
        private readonly Renderer[][] _shells = { new Renderer[10], new Renderer[10] };
        private readonly Renderer[][] _cores = { new Renderer[10], new Renderer[10] };
        private readonly RoyalCouncilDeck[] _decks = new RoyalCouncilDeck[2];
        private MaterialPropertyBlock _properties;
        private RoyalCouncilSeal _seal;
        private RoyalCouncilHourglass _clock;
        private RoyalCouncilTableToys _toys;
        private bool _bound;

        public RoyalCouncilHourglass Clock => _clock;
        public Transform Part(string name) => _parts.TryGetValue(name, out Transform value) ? value : null;

        public static RoyalCouncilBoard TryCreate(Transform parent, BoardAnchors anchors)
        {
            GameObject prefab = Resources.Load<GameObject>(ResourcePath);
            if (prefab == null) return null;
            var instance = Instantiate(prefab, parent, false);
            instance.name = "Royal Council 3D";
            RoyalCouncilBoard board = instance.GetComponent<RoyalCouncilBoard>();
            board.Build(anchors);
            return board;
        }

        private void Build(BoardAnchors anchors)
        {
            foreach (Transform part in GetComponentsInChildren<Transform>(true)) _parts[part.name] = part;
            _properties = new MaterialPropertyBlock();
            anchors.Tabletop = this;
            BuildSeat(anchors.p0, 0);
            BuildSeat(anchors.p1, 1);
            for (int seat = 0; seat < 2; seat++)
            {
                for (int i = 0; i < 10; i++)
                {
                    _shells[seat][i] = Part($"RC.P{seat}.Mana.Crystal.{i:00}").GetComponent<Renderer>();
                    _cores[seat][i] = Part($"RC.P{seat}.Mana.FullCore.{i:00}").GetComponent<Renderer>();
                    _shells[seat][i].sharedMaterial = glassMaterial;
                    _shells[seat][i].shadowCastingMode = ShadowCastingMode.Off;
                    _cores[seat][i].shadowCastingMode = ShadowCastingMode.Off;
                    _cores[seat][i].GetPropertyBlock(_properties);
                    _properties.SetFloat("_CloudPhase",seat*17f+i*1.618f);
                    _cores[seat][i].SetPropertyBlock(_properties);
                    _shells[seat][i].GetPropertyBlock(_properties);
                    _properties.SetFloat("_Rim",0.3f);
                    _shells[seat][i].SetPropertyBlock(_properties);
                }
                var deck = new GameObject($"P{seat} Physical Deck").AddComponent<RoyalCouncilDeck>();
                deck.transform.SetParent(transform, false);
                deck.Initialize(Part($"RC.P{seat}.Pile.Card"), Part($"RC.P{seat}.Pile.Top"), ModelScale);
                deck.SetDrawAnchor(seat == 0 ? anchors.p0.DeckAnchor : anchors.p1.DeckAnchor);
                _decks[seat] = deck;
            }
            _seal = new GameObject("End Turn Seal").AddComponent<RoyalCouncilSeal>();
            _seal.transform.SetParent(transform, false);
            _seal.Initialize(Part("RC.Prop.WaxSeal"), Part("RC.Prop.Stamper"));
            _clock = new GameObject("Turn Hourglass").AddComponent<RoyalCouncilHourglass>();
            _clock.transform.SetParent(transform, false);
            _clock.Initialize(this, glassMaterial);
            _toys = new GameObject("Local table toys").AddComponent<RoyalCouncilTableToys>();
            _toys.transform.SetParent(transform, false);
            _toys.Initialize(this);
            // The empty cups must stay dark when they face the key light; a mirror-like
            // imported finish made the opponent's spent mana look permanently full.
            var cups = Part("RC.Static.Mana.Socket.Obsidian").GetComponent<Renderer>();
            cups.GetPropertyBlock(_properties);
            _properties.SetFloat("roughnessFactor", 0.72f);
            _properties.SetFloat("metallicFactor", 0.05f);
            cups.SetPropertyBlock(_properties);
            AddLighting();
            Camera camera = Camera.main;
            if (camera != null)
            {
                if (camera.TryGetComponent(out SafeAreaCamera old)) old.enabled = false;
                if (!camera.TryGetComponent<RoyalCouncilCamera>(out var rig)) rig = camera.gameObject.AddComponent<RoyalCouncilCamera>();
                rig.SetBackdrop(backdrop);
            }
        }

        private void BuildSeat(SeatAnchors seat, int index)
        {
            string prefix = $"RC.Mount.P{index}.";
            seat.FanMirrored = index == 1;
            seat.HandCenterY = HandCenterY;
            seat.HandDepth = HandDepth;
            for (int i = 0; i < 7; i++)
            {
                seat.BoardSlots[i] = Anchor($"P{index}_Board{i}", Part(prefix + $"Board.{i:00}").position);
                seat.BoardSlots[i].localScale = Vector3.one * BoardCardScale;
            }
            for (int i = 0; i < 10; i++) seat.HandSlots[i] = Anchor($"P{index}_Hand{i}", Vector3.zero);
            ApplyHandLayout(seat, 10);
            // Exported mounts sit above the rings. The portrait's local -0.01 Z and socket
            // art's -0.015 Z place each image just above its liner (-0.036 Z before lifts).
            // Matching the recess depth removes perspective drift without per-seat XY nudges.
            seat.HeroAnchor = Anchor($"P{index}_Hero", Part(prefix + "Hero").position + Vector3.forward * 0.324f);
            seat.HeroPowerAnchor = Anchor($"P{index}_Power", Part(prefix + "HeroPower").position + Vector3.forward * 0.209f);
            seat.WeaponAnchor = Anchor($"P{index}_Weapon", Part(prefix + "Weapon").position + Vector3.forward * (0.209f * WeaponScale));
            seat.WeaponAnchor.localScale = Vector3.one * WeaponScale;
            seat.DeckAnchor = Anchor($"P{index}_Deck", Part(prefix + "Deck").position);
            seat.ManaTrayAnchor = Anchor($"P{index}_Mana", Part(prefix + "Mana").position);
        }

        internal static void ApplyHandLayout(SeatAnchors seat, int count)
        {
            if (!seat.FanMirrored)
            {
                HandFanLayout.Apply(seat.HandSlots, count, false, seat.HandCenterY, seat.HandDepth);
                if (seat.HandSlots == null || count <= 0) return;
                Vector3 pivot = new Vector3(0f, -seat.HandCenterY, seat.HandDepth);
                foreach (Transform slot in seat.HandSlots)
                    if (slot != null)
                        slot.SetPositionAndRotation(pivot + LocalHandTilt * (slot.position - pivot),
                            LocalHandTilt * slot.rotation);
                return;
            }
            if (seat.HandSlots == null || count <= 0) return;

            // Solve an upright fan in its own plane, then hold it beyond the far rail.
            // The rail naturally hides the lower portion. Keeping real hand anchors
            // here gives draws and plays the same destination as the resting card backs.
            HandFanLayout.Apply(seat.HandSlots, count, false, 0f, 0f);
            foreach (Transform slot in seat.HandSlots)
                if (slot != null)
                    slot.SetPositionAndRotation(OpponentHandCenter + OpponentHandTilt * (slot.position + Vector3.up * OpponentHandLift),
                        OpponentHandTilt * slot.rotation);
        }

        private Transform Anchor(string label, Vector3 position)
        {
            var go = new GameObject(label);
            go.transform.SetParent(transform, false);
            go.transform.position = position;
            return go.transform;
        }

        public void Bind(PlayerId local, PlayerId opponent, InputRouter input)
        {
            _players[0] = local; _players[1] = opponent;
            _seal.Bind(local, input);
            _toys.Bind(input);
            if (input != null)
            {
                input.HandBoundaryY = HandTopY;
                input.BoardSlotSpacing = BoardSlotSpacing;
            }
            _bound = true;
        }

        public void OnStateUpdated(GameState state)
        {
            if (!_bound || state == null) return;
            for (int s = 0; s < 2; s++)
            {
                PlayerState player = state.Players[state.SeatOf(_players[s])];
                int temporary = Mathf.Min(player.Mana.Temporary, player.Mana.Available);
                int permanent = player.Mana.Available - temporary;
                for (int i = 0; i < 10; i++)
                {
                    bool full = i < player.Mana.Available;
                    bool temp = full && i >= permanent;
                    bool locked = i >= player.Mana.Available + player.Mana.Spent && i < player.Mana.Available + player.Mana.Spent + player.Mana.Locked;
                    bool earned = i < player.Mana.Available + player.Mana.Spent + player.Mana.Locked;
                    Color shell = full ? (temp ? new Color(0.45f, 0.78f, 1f, 0.16f) : new Color(0.08f, 0.34f, 0.62f, 0.12f))
                        : locked ? new Color(0.62f, 0.26f, 0.11f, 0.28f) : new Color(0.66f, 0.82f, 0.92f, earned ? 0.10f : 0.035f);
                    Tint(_shells[s][i], shell, temp ? new Color(0.12f, 0.22f, 0.25f) : Color.black);
                    Tint(_cores[s][i],temp ? new Color(0.28f,0.62f,0.82f,0.67f) : new Color(0.035f,0.28f,0.6f,0.64f),
                        temp ? new Color(0.04f,0.14f,0.19f) : new Color(0.015f,0.075f,0.13f));
                    _cores[s][i].enabled = full;
                }
                _decks[s].SetCount(player.Deck.Count);
            }
            _seal.OnStateUpdated(state);
            _clock.OnStateUpdated(state);
        }

        public void OnTurnTimer(TurnTimer timer) => _clock.OnTurnTimer(timer);

        public void OnMatchEvent(IReadOnlyList<GameEvent> events) => _seal.OnMatchEvent(events);

        public void OnAuthorityStateUpdated(GameState state) => _seal.OnAuthorityStateUpdated(state);

        public void FitHitTargets(BoardAnchors anchors, PlayerId p0, PlayerId p1)
        {
            foreach (PlayerId player in new[] { p0, p1 })
            {
                var hero = anchors.HeroAnchor(player).GetComponent<BoxCollider>();
                if (hero != null) { hero.size = new Vector3(PortraitDiameter, PortraitDiameter, 0.1f); hero.center = Vector3.zero; }
                var power = anchors.HeroPowerAnchor(player).GetComponent<BoxCollider>();
                if (power != null) { power.size = new Vector3(1.05f, 1.05f, 0.1f); power.center = Vector3.back * 0.1f; }
            }
        }

        private void Tint(Renderer target, Color color, Color emission)
        {
            target.GetPropertyBlock(_properties);
            _properties.SetColor("_BaseColor", color);
            _properties.SetColor("_EmissionColor", emission);
            target.SetPropertyBlock(_properties);
        }

        private void AddLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.56f, 0.60f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.35f, 0.29f, 0.26f);
            RenderSettings.ambientGroundColor = new Color(0.18f, 0.13f, 0.10f);
            var key = new GameObject("Table Key").AddComponent<Light>();
            key.transform.SetParent(transform, false);
            key.transform.rotation = Quaternion.LookRotation(new Vector3(0.45f, -0.55f, 1f));
            key.type = LightType.Directional;
            key.color = new Color(1f, 0.87f, 0.71f);
            key.intensity = 1.05f;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.62f;
            key.shadowBias = 0.015f;
            key.shadowNormalBias = 0.06f;
        }

    }
}
