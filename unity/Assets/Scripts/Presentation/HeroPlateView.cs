using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// World-space hero portrait for one player, clipped to the Royal Council portrait fixture.
    /// Created procedurally by
    /// NetworkedGameBootstrap — one per player.
    /// </summary>
    public sealed class HeroPlateView : MonoBehaviour
    {
        private static readonly Color PlateColor   = Color.white;
        private static readonly Color HpFull       = Color.white;
        private static readonly Color FrostedTint   = new Color(0.40f, 0.60f, 0.95f);
        // Matches CardPresenter.SelectedColor so hero-as-attacker selection reads
        // identically to minion selection.
        private static readonly Color SelectedTint = new Color(1.00f, 0.85f, 0.10f);
        // Valid-target cue: cyan to match CardMaterialBinder's target glow.
        private static readonly Color TargetableTint = new Color(0.25f, 0.90f, 1.00f);
        // Armor reads as steel beside the Health it shields.
        private static readonly Color ArmorInk = new Color(0.78f, 0.82f, 0.88f);

        // Royal Council's portrait fixtures have more room than the old board anchors; grow the complete
        // composition together so art, HP label and ClickableHero collider stay registered.
        private const float FixtureScale = 1.50f;
        private static readonly Quaternion EnemyFixtureRotation =
            Quaternion.Euler(-11f, 0f, -7f);
        internal const float PlateW = 2.16f * FixtureScale;
        internal const float PlateH = 3.24f * FixtureScale;
        private const float PortraitW = 1.566f * FixtureScale;
        private const float PortraitH = 2.133f * FixtureScale;
        private static readonly Vector3 PortraitCentre = new Vector3(0f, -0.02f, -0.01f);
        private static readonly Vector3 TabletopPortraitCentre = new Vector3(0f, 0f, -0.01f);
        private const int PortraitOrder = 9;
        private const int TextOrder = 10;
        private const int OvalSegments = 64;

        private PlayerId _playerId;
        private bool _selected;
        private bool _targetable;
        private bool _targetFocused;
        private bool _frosted;

        private Renderer     _portrait;
        private TextMeshPro  _hpLabel;
        private TextMeshPro  _attackLabel;
        private TextMeshPro  _armorLabel;
        private HealthNumber _hp;
        private CardClass _heroClass;
        private IUiSpriteProvider _uiSprites;
        private PortraitCastEffect _cast;
        private bool _castBuilt;
        private Coroutine _castRoutine;
        private PortraitShadowWipe _shadowWipe;
        private Coroutine _shadowWipeRoutine;
        private MaterialPropertyBlock _mpb;
        private bool _tabletop;

        /// <summary>Attach to a new GameObject and call Init once before first OnStateUpdated.
        /// Identified by PlayerId, never by seat/visual index — a networked seat-1 local renders
        /// at visual bottom, so index-based state reads swap the players (found live, 2026-07-19).</summary>
        public void Init(PlayerId playerId, Transform heroAnchor,
                         IArtProvider artProvider, CardClass heroClass, bool isLocalSeat,
                         IUiSpriteProvider uiSprites = null)
        {
            _playerId  = playerId;
            _heroClass = heroClass;
            _mpb       = new MaterialPropertyBlock();
            _tabletop = heroAnchor.GetComponentInParent<RoyalCouncilBoard>() != null;

            transform.SetParent(heroAnchor, false);
            transform.localPosition = Vector3.zero;
            // The far Royal Council portrait mount is painted as a foreshortened oval tilted
            // down-left. Seat roots stay unrotated because the power/weapon satellites have their
            // own fixture alignment; only the portrait assembly follows that recess.
            transform.localRotation = isLocalSeat || _tabletop ? Quaternion.identity : EnemyFixtureRotation;

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif

            // The clipped portrait sits inside the board's physical frame.
            _uiSprites = uiSprites;
            _portrait = MakePortrait(transform, _tabletop);
            _portrait.gameObject.SetActive(false);
            artProvider?.LoadArt($"hero_{heroClass.ToString().ToLowerInvariant()}", SetPortrait);

            // HP sits inside the portrait's right edge. It was driven there by the two enlarged
            // sockets owning the entire lower skirt; they moved out to flank the plate on
            // 2026-09-04 and the skirt is clear again, so bottom-right is available if the
            // composition ever wants it back. Left here because it reads well, not because it
            // has to be.
            _hpLabel = MakeLabel(transform, "HP",
                new Vector3(0.648f * FixtureScale, 0.162f * FixtureScale, -0.02f),
                6.345f * FixtureScale,
                HpFull);
            // Attack gained this turn mirrors HP on the portrait's left edge; Armor sits under HP.
            // Both are hidden at zero, which is nearly always (Empower, 2026-11-04). Not yet seen
            // on a device: the positions are the HP label's, mirrored and dropped.
            _attackLabel = MakeLabel(transform, "Attack",
                new Vector3(-0.648f * FixtureScale, 0.162f * FixtureScale, -0.02f),
                6.345f * FixtureScale, HealthInk.Buffed);
            _armorLabel = MakeLabel(transform, "Armor",
                new Vector3(0.648f * FixtureScale, -0.38f * FixtureScale, -0.02f),
                4.2f * FixtureScale, ArmorInk);
            if (_tabletop)
            {
                // Centre both totals below the portraits and face the fixed tabletop view,
                // so the digits remain square instead of following the board's perspective.
                FaceTabletop(_hpLabel, new Vector3(0f, -0.82f, -0.01f), 7.8f);
                FaceTabletop(_attackLabel, new Vector3(-0.62f, -0.82f, -0.01f), 6.2f);
                FaceTabletop(_armorLabel, new Vector3(0.62f, -0.82f, -0.01f), 6.2f);
            }
            _attackLabel.gameObject.SetActive(false);
            _armorLabel.gameObject.SetActive(false);
        }

        private static void FaceTabletop(TextMeshPro label, Vector3 localPosition, float fontSize)
        {
            label.transform.localPosition = localPosition;
            Camera camera = Camera.main;
            if (camera != null)
            {
                // Lift along the viewing ray to clear the rim without shifting the
                // number sideways relative to its point on the portrait.
                Vector3 screen = camera.WorldToScreenPoint(label.transform.position);
                screen.z -= .5f;
                label.transform.SetPositionAndRotation(camera.ScreenToWorldPoint(screen), camera.transform.rotation);
            }
            label.enableAutoSizing = false;
            label.overflowMode = TextOverflowModes.Overflow;
            label.fontSize = label.fontSizeMax = fontSize;
            label.fontSizeMin = fontSize;
            label.rectTransform.sizeDelta = new Vector2(1.5f, 1.6f);
        }

        private void SetPortrait(Sprite sprite)
        {
            if (_portrait == null || sprite == null) return;
            _portrait.GetPropertyBlock(_mpb);
            _mpb.SetTexture("_BaseMap", sprite.texture);
            _mpb.SetColor("_BaseColor", Color.white);
            _portrait.SetPropertyBlock(_mpb);
            _portrait.gameObject.SetActive(true);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_hpLabel == null) return;

            PlayerState ps   = state.Players[state.SeatOf(_playerId)];
            HeroInstance hero = ps.Hero;

            // A hero's full health is never buffed, so there is no green here: white or the red scale.
            _hp ??= new HealthNumber(this);
            _hp.Show(_hpLabel, hero.CurrentHealth,
                     HealthInk.Resting(hero.CurrentHealth, hero.MaxHealth, hero.MaxHealth));

            // The total the hero would hit for, shown only while some of it was gained this turn;
            // a weapon alone already shows its Attack in the weapon socket.
            int attack = (ps.Weapon?.CurrentAttack ?? 0) + ps.HeroAttackThisTurn;
            ShowCount(_attackLabel, ps.HeroAttackThisTurn > 0 ? attack : 0, "");
            ShowCount(_armorLabel, hero.Armor, "+");

            _frosted = hero.IsFrosted();
            ApplyPlateTint();
        }

        private static void ShowCount(TextMeshPro label, int value, string prefix)
        {
            if (label == null) return;
            bool show = value > 0;
            if (label.gameObject.activeSelf != show) label.gameObject.SetActive(show);
            if (show) label.text = prefix + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Play this hero's cast up the portrait: wait <paramref name="delaySeconds"/>,
        /// then rise over <paramref name="riseSeconds"/>. Called by VfxDirector when this hero's
        /// power fires. Does nothing for a class with no cast art.</summary>
        public void PlayCast(float delaySeconds, float riseSeconds)
        {
            if (!isActiveAndEnabled) return;
            if (!_castBuilt)
            {
                // Built on first use, and only tried once: a class with no art stays that way.
                _castBuilt = true;
                _cast = PortraitCastEffect.TryBuild(transform, _heroClass, _uiSprites, _tabletop ? TabletopPortraitCentre : PortraitCentre,
                                                    _tabletop ? RoyalCouncilBoard.PortraitDiameter : PortraitW,
                                                    _tabletop ? RoyalCouncilBoard.PortraitDiameter : PortraitH, PortraitOrder);
            }
            if (_cast == null) return;

            if (_castRoutine != null) StopCoroutine(_castRoutine);
            _castRoutine = StartCoroutine(_cast.Play(delaySeconds, riseSeconds));
        }

        /// <summary>Wipe black across the portrait, left to right, for a Nightveil spell this hero
        /// cast: wait <paramref name="delaySeconds"/>, wipe over <paramref name="wipeSeconds"/>,
        /// then fade. Called by VfxDirector.</summary>
        public void PlayShadowWipe(float delaySeconds, float wipeSeconds)
        {
            if (!isActiveAndEnabled) return;
            _shadowWipe ??= new PortraitShadowWipe(transform, _tabletop ? TabletopPortraitCentre : PortraitCentre,
                                                   _tabletop ? RoyalCouncilBoard.PortraitDiameter : PortraitW,
                                                   _tabletop ? RoyalCouncilBoard.PortraitDiameter : PortraitH,
                                                   PortraitOrder);
            if (_shadowWipeRoutine != null) StopCoroutine(_shadowWipeRoutine);
            _shadowWipeRoutine = StartCoroutine(_shadowWipe.Play(delaySeconds, wipeSeconds));
        }

        // A disabled plate's coroutine dies with it, which would leave the cast frozen on screen.
        private void OnDisable()
        {
            _cast?.Hide();
            _shadowWipe?.Hide();
        }

        /// <summary>Hero-as-attacker selection cue — driven by InputRouter, mirrors
        /// CardPresenter.SetSelectionHighlight. Selection outranks the frosted tint.</summary>
        public void SetSelected(bool selected)
        {
            _selected = selected;
            ApplyPlateTint();
        }

        /// <summary>Valid-target cue while targeting is armed — driven by InputRouter
        /// (targeting-state visibility). Selection outranks it; it outranks frosted.</summary>
        public void SetTargetable(bool targetable)
        {
            _targetable = targetable;
            ApplyPlateTint();
        }

        public void SetTargetFocused(bool focused)
        {
            _targetFocused = focused;
            ApplyPlateTint();
        }

        private void ApplyPlateTint()
        {
            Tint(_portrait, _selected   ? SelectedTint
                       : _targetFocused ? SelectedTint
                       : _targetable ? TargetableTint
                       : _frosted     ? FrostedTint
                       : PlateColor);
        }

        // ── Build helpers ───────────────────────────────────────────────────────

        private static Renderer MakePortrait(Transform parent, bool tabletop = false)
        {
            var go = new GameObject("Portrait");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = tabletop ? TabletopPortraitCentre : PortraitCentre;
            go.transform.localScale = tabletop ? new Vector3(RoyalCouncilBoard.PortraitDiameter, RoyalCouncilBoard.PortraitDiameter, 1f) : new Vector3(PortraitW, PortraitH, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = tabletop ? RoundMesh() : OvalMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sortingOrder = PortraitOrder;
            MaterialCatalog.Assign(mr, MaterialCatalog.CardArt);
            return mr;
        }

        private static Mesh _oval;
        private static Mesh _round;
        private static Mesh RoundMesh()
        {
            if (_round != null) return _round;
            _round = Instantiate(OvalMesh());
            _round.name = "Tabletop Hero Portrait";
            Vector2[] uv = _round.uv;
            for (int i = 0; i < uv.Length; i++) uv[i].x = 0.5f + (uv[i].x - 0.5f) * PortraitH / PortraitW;
            _round.uv = uv;
            return _round;
        }

        private static Mesh OvalMesh()
        {
            if (_oval != null) return _oval;

            var verts = new Vector3[OvalSegments + 1];
            var uvs = new Vector2[OvalSegments + 1];
            var tris = new int[OvalSegments * 3];
            float uSpan = PortraitW / PortraitH; // crop square art instead of stretching a face

            verts[0] = Vector3.zero;
            uvs[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < OvalSegments; i++)
            {
                float a = 2f * Mathf.PI * i / OvalSegments;
                float x = Mathf.Cos(a) * 0.5f;
                float y = Mathf.Sin(a) * 0.5f;
                verts[i + 1] = new Vector3(x, y, 0f);
                uvs[i + 1] = new Vector2(0.5f + x * uSpan, y + 0.5f);
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = 1 + (i + 1) % OvalSegments;
                tris[i * 3 + 2] = 1 + i;
            }

            _oval = new Mesh { name = "HeroPortraitOval" };
            _oval.SetVertices(verts);
            _oval.SetUVs(0, uvs);
            _oval.SetTriangles(tris, 0);
            _oval.RecalculateBounds();
            return _oval;
        }

        private static TextMeshPro MakeLabel(Transform parent, string goName, Vector3 localPos, float fontSize, Color color)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize  = fontSize;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = color;
            tmp.rectTransform.sizeDelta = new Vector2(PlateW, fontSize * 0.25f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Ellipsis;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax      = fontSize;
            tmp.fontSizeMin      = fontSize * 0.45f;
            BoardController.ApplyUiFont(tmp);
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = TextOrder;
            return tmp;
        }

        private void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            if (r is SpriteRenderer sr)
            {
                sr.color = c;
                return;
            }
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(_mpb);
        }
    }
}
