using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The hero-power and weapon sockets: two forged rings docked side by side inboard of the
    /// hero portrait, hero power first, weapon second, both resting on the §1.3 baseline.
    /// Replaces the pair of flat 0.9Ø tinted discs HeroPlateView used to draw.
    ///
    /// DOCKED, NEVER STACKED. Stacking them in the old disc column would force both rings to
    /// shrink to fit the plate's 1.8 height (Sam, 2026-07-25). Side by side, height stops being
    /// the constraint. The weapon ring is present only while a weapon is equipped, which is why
    /// the two are separate closed rings with nothing bridging them — the pair still reads
    /// correctly with one half absent.
    ///
    /// ALL GEOMETRY INSIDE A FRAME IS READ, NOT WRITTEN. Ring size, socket aperture and the two
    /// stat bosses come from HeroFrames_&lt;Material&gt;.json, which split_hero_frames.py measures
    /// off the generated art. The generator does not honour the requested layout exactly and is
    /// expected to land differently every batch, so a constant here would quietly offset every
    /// numeral the next time the art is regenerated.
    /// </summary>
    public sealed class HeroSocketsView : MonoBehaviour
    {
        /// <summary>Frame canvas, in world units — 3/4 of the 1.8 hero plate.
        /// Budget against the RING, not this: the canvas carries 34% of transparent margin
        /// so the bosses can overhang, so the ring only reads ~1.008 units. Sizing the
        /// column by the canvas overstates its footprint by a third.</summary>
        public const float FrameSize = 1.35f;

        /// <summary>Visible ring diameter as a fraction of the frame canvas — the reciprocal of
        /// split_hero_frames.py's CANVAS_PAD. Used for LAYOUT only, so the board can size the
        /// socket row without loading a material's JSON; the per-material file refines it (this
        /// batch measures 0.7464 for both). Change CANVAS_PAD and change this with it.</summary>
        public const float RingFraction = 0.7464f;

        private const string DefaultMaterial = "CarvedStone";

        // Rings are transparent sprites, so they draw after the opaque socket discs and composite
        // over them — which is what lets the rim occlude the illustration's edge. Numerals ride
        // on top of both.
        private const int RingOrder = 10;
        private const int StatOrder = 11;

        // Segments around the socket disc. 64 is smooth at 88 px (the socket at board scale) and
        // still smooth if the frames are ever drawn larger for a bake-off sheet.
        private const int DiscSegments = 64;

        private static readonly Color PowerReady = new Color(1.00f, 1.00f, 1.00f);
        private static readonly Color PowerSpent = new Color(0.42f, 0.42f, 0.48f);
        private static readonly Color AttackText = new Color(1.00f, 0.93f, 0.62f);
        private static readonly Color DuraText   = new Color(0.78f, 0.86f, 1.00f);

        private PlayerId _playerId;
        private ICardDatabase _db;
        private IArtProvider _art;
        private MaterialPropertyBlock _mpb;

        private SpriteRenderer _powerRing;
        private Renderer       _powerArt;
        private TextMeshPro    _powerCost;
        private GameObject     _weaponRoot;
        private SpriteRenderer _weaponRing;
        private Renderer       _weaponArt;
        private TextMeshPro    _weaponAttack, _weaponDurability;

        // Last artId handed to each socket. OnStateUpdated runs on every snapshot, so without
        // this the same illustration is re-requested dozens of times a turn.
        private string _powerArtId, _weaponArtId;

        /// <summary>Attach to a new GameObject and call Init once before the first
        /// OnStateUpdated. Identified by PlayerId, never by seat index (HeroPlateView
        /// precedent — a networked seat-1 local renders at visual bottom).</summary>
        public void Init(PlayerId playerId, Transform powerAnchor, Transform weaponAnchor,
                         ICardDatabase db, IArtProvider art,
                         IUiSpriteProvider uiSprites = null, string material = DefaultMaterial)
        {
            _playerId = playerId;
            _db       = db;
            _art      = art;
            _mpb      = new MaterialPropertyBlock();

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif

            HeroFrameGeometry geo = HeroFrameGeometry.Load(material);

            if (powerAnchor != null)
            {
                HeroFrameGeometry.Frame f = geo.HeroPower;
                _powerRing = MakeRing(powerAnchor, "PowerRing", uiSprites, $"HeroPower_{material}");
                _powerArt  = MakeSocketArt(powerAnchor, "PowerArt", FrameSize * f.SocketDiameter);
                // The cost rides low on the rim rather than a boss: the hero-power ring is
                // measured to have none, by design.
                _powerCost = MakeStat(powerAnchor, "PowerCost", new Vector2(0.5f, 0.86f),
                                      f.BossDiameterOrDefault, Color.white);
            }

            if (weaponAnchor != null)
            {
                HeroFrameGeometry.Frame f = geo.Weapon;
                _weaponRoot = new GameObject("Weapon");
                _weaponRoot.transform.SetParent(weaponAnchor, false);
                _weaponRoot.transform.localPosition = Vector3.zero;

                Transform wt = _weaponRoot.transform;
                _weaponRing = MakeRing(wt, "WeaponRing", uiSprites, $"Weapon_{material}");
                _weaponArt  = MakeSocketArt(wt, "WeaponArt", FrameSize * f.SocketDiameter);

                // Attack left, durability right — the order the splitter reports them, which
                // sorts by x.
                _weaponAttack     = MakeStat(wt, "Attack",     f.BossAt(0), f.BossDiameterOrDefault, AttackText);
                _weaponDurability = MakeStat(wt, "Durability", f.BossAt(1), f.BossDiameterOrDefault, DuraText);

                _weaponRoot.SetActive(false);
            }
        }

        public void OnStateUpdated(GameState state)
        {
            PlayerState ps = state.Players[state.SeatOf(_playerId)];

            CardDefinition powerDef = _db?.Get(ps.Hero.HeroPowerId);
            int  cost   = powerDef?.Cost ?? 2;
            bool usable = !ps.HeroPowerUsedThisTurn
                          && ps.Mana.Available >= cost
                          && state.ActivePlayer == ps.Id
                          && state.Phase == TurnPhase.Main;
            SetPower(cost, usable, powerDef?.ArtId);

            WeaponInstance w = ps.Weapon;
            if (w == null) SetWeapon(false, 0, 0, null);
            else           SetWeapon(true, w.Attack, w.Durability, _db?.Get(w.DefinitionId)?.ArtId);
        }

        /// <summary>Public so an editor preview can drive the shipping layer directly rather than
        /// a re-derivation of it — the mana rail shipped a real bug behind one sampled state, so
        /// a parameterised thing gets exercised across its parameter space.</summary>
        public void SetPower(int cost, bool usable, string artId)
        {
            if (_powerRing == null) return;
            _powerRing.color = usable ? PowerReady : PowerSpent;
            if (_powerCost != null) _powerCost.text = cost.ToString();
            Tint(_powerArt, usable ? Color.white : PowerSpent);
            LoadInto(_powerArt, artId, ref _powerArtId);
        }

        public void SetWeapon(bool present, int attack, int durability, string artId)
        {
            if (_weaponRoot == null) return;
            _weaponRoot.SetActive(present);
            if (!present) return;

            if (_weaponAttack != null)     _weaponAttack.text     = attack.ToString();
            if (_weaponDurability != null) _weaponDurability.text = durability.ToString();
            LoadInto(_weaponArt, artId, ref _weaponArtId);
        }

        // ── Build helpers ───────────────────────────────────────────────────────────────

        private static SpriteRenderer MakeRing(Transform parent, string goName,
                                               IUiSpriteProvider uiSprites, string key)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.02f);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.drawMode = SpriteDrawMode.Sliced;   // .size sets exact world dims, PPU-independent
            // SPRITE FIRST, THEN SIZE — assignment resets .size to the sprite's own PPU extents,
            // so a size set beforehand is silently thrown away.
            sr.sprite       = uiSprites?.Get(key);
            sr.size         = new Vector2(FrameSize, FrameSize);
            sr.sortingOrder = RingOrder;
            if (sr.sprite == null)
                Debug.LogWarning($"[HeroSocketsView] no UI sprite '{key}' — socket renders empty.");
            return sr;
        }

        /// <summary>
        /// The illustration, on a DISC mesh rather than a quad.
        ///
        /// WHY A MESH AND NOT A MASK. The art is square and the aperture is round: a square of
        /// side d has corners at 0.707d from centre, so sized to the socket they reach past the
        /// ring's outer edge and the frame reads as a card laid down with a ring painted on top
        /// (Sam, watching it live, 2026-07-25). The obvious fix — a circular SpriteMask — is a
        /// SILENT NO-OP here: URP-Renderer.asset is a UniversalRendererData, and SpriteMask is
        /// only honoured by the 2D Renderer. Clipping with geometry needs no stencil, no shader
        /// and no CPU-readable texture, so it cannot quietly stop working.
        /// </summary>
        private Renderer MakeSocketArt(Transform parent, string goName, float diameter)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            go.transform.localScale    = new Vector3(diameter, diameter, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = DiscMesh();
            var mr = go.AddComponent<MeshRenderer>();
            MaterialCatalog.Assign(mr, MaterialCatalog.CardArt);
            // Explicit white, the way HeroPlateView does for the portrait: M_CardArt's own
            // _BaseColor is not something to rely on, and LoadInto only writes _BaseMap.
            Tint(mr, Color.white);
            go.SetActive(false);   // shown when art actually resolves
            return mr;
        }

        private static Mesh _disc;

        /// <summary>Unit-diameter disc centred on the origin, UVs mapping the art's inscribed
        /// circle across it, so the illustration fills the aperture edge to edge.</summary>
        private static Mesh DiscMesh()
        {
            if (_disc != null) return _disc;

            var verts = new Vector3[DiscSegments + 1];
            var uvs   = new Vector2[DiscSegments + 1];
            var tris  = new int[DiscSegments * 3];

            verts[0] = Vector3.zero;
            uvs[0]   = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < DiscSegments; i++)
            {
                float a = 2f * Mathf.PI * i / DiscSegments;
                float x = Mathf.Cos(a) * 0.5f, y = Mathf.Sin(a) * 0.5f;
                verts[i + 1] = new Vector3(x, y, 0f);
                uvs[i + 1]   = new Vector2(x + 0.5f, y + 0.5f);

                // Wind clockwise seen down -Z, matching Unity's quad primitive so the disc faces
                // the camera in the same orientation every other board element does.
                tris[i * 3 + 0] = 0;
                tris[i * 3 + 1] = 1 + (i + 1) % DiscSegments;
                tris[i * 3 + 2] = 1 + i;
            }

            _disc = new Mesh { name = "SocketDisc" };
            _disc.SetVertices(verts);
            _disc.SetUVs(0, uvs);
            _disc.SetTriangles(tris, 0);
            _disc.RecalculateBounds();
            return _disc;
        }

        /// <summary>A numeral centred on a measured boss. <paramref name="frac"/> is the boss
        /// centre in canvas fractions with a top-left origin, which is how the splitter reports
        /// it; y flips because world space grows upward.</summary>
        private static TextMeshPro MakeStat(Transform parent, string goName, Vector2 frac,
                                            float diameterFrac, Color color)
        {
            float d = FrameSize * diameterFrac;
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(
                (frac.x - 0.5f) * FrameSize,
                (0.5f - frac.y) * FrameSize,
                -0.03f);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment        = TextAlignmentOptions.Center;
            tmp.color            = color;
            tmp.fontSize         = d * 3.4f;      // fits a two-digit numeral inside the boss face
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.rectTransform.sizeDelta = new Vector2(d, d);
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax      = d * 3.4f;
            tmp.fontSizeMin      = d * 1.6f;

            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = StatOrder;
            return tmp;
        }

        private void Tint(Renderer r, Color c)
        {
            if (r == null) return;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(_mpb);
        }

        private void LoadInto(Renderer target, string artId, ref string lastId)
        {
            if (target == null) return;
            if (string.IsNullOrEmpty(artId)) { target.gameObject.SetActive(false); lastId = null; return; }
            if (artId == lastId) return;
            lastId = artId;
            _art?.LoadArt(artId, sprite =>
            {
                if (target == null || sprite == null) return;
                target.GetPropertyBlock(_mpb);
                _mpb.SetTexture("_BaseMap", sprite.texture);
                target.SetPropertyBlock(_mpb);
                target.gameObject.SetActive(true);
            });
        }
    }
}
