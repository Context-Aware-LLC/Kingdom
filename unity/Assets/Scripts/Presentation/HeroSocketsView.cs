using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The enlarged hero-power and weapon sockets, vertically staggered on the inboard side of
    /// each hero portrait. Their cluster root follows the Royal Council artwork fixture.
    /// They remain separate objects because the weapon disappears when unequipped and the hero
    /// power owns its own input collider. Circular art fills each transparent opening and stays
    /// inside the gold aperture.
    /// </summary>
    public sealed class HeroSocketsView : MonoBehaviour
    {
        /// <summary>Maximum side of either socket frame in world units. The old frame's
        /// visible ring was 1.008 units; these read about 30% larger.
        ///
        /// <para>THIS IS THE QUAD, NOT THE RING. Both sprites carry transparent margin, and the
        /// ring's widest point spans only 0.7149 of the quad — so the drawn ring is 1.619 units
        /// here, not 2.265. Every "how big does it look" and "does it touch the plate" question is
        /// answered against that fraction, never against this constant; see the separate offsets
        /// in <c>ProceduralBoardSetup</c>, which pay for the transparent margins.</para>
        ///
        /// <para>Raised 1.32 -> 1.51 on 2026-09-04 (Sam: bigger, and touching the hero frame).
        /// The current local offsets keep both quads distinct while their drawn metal rims meet
        /// the hero frame.</para>
        ///
        /// <para>Raised again 1.51 -> 2.265 on 2026-10-02 (Sam: larger by 50%); the drawn ring is
        /// 1.619 units. <see cref="HitDiameter"/> and the offsets in <c>ProceduralBoardSetup</c>
        /// moved with it.</para></summary>
        public const float FrameSize = 2.265f;

        /// <summary>The hero power's tap target. Tracks the DRAWN ring rather than the quad —
        /// 1.19x the ring's widest point, the ratio the 1.32/1.12 pair carried.</summary>
        public const float HitDiameter = 1.92f;

        // BOTH RINGS ARE CO-REGISTERED, AND EVERY NUMBER BELOW DEPENDS ON THAT.
        // `tooling/scripts/align_socket_rings.py` writes the pair onto one square canvas with each
        // ring's APERTURE CENTRE pinned at the canvas centre and both apertures scaled to one
        // diameter. That is the only reason a single SocketDiameter draws the same circle in both
        // frames and a single zero offset centres it — before alignment the two apertures differed
        // by 6% in size and 30 source pixels in position, so each frame carried its own hand-measured
        // fudge and swapping one for the other moved the art.
        //
        // Run of 2026-09-03 on Sam's re-cuts (docs/sam/shots/{hp,weapon}-frame.png), canvas 1424:
        //     hero-power.png    aperture 730 x 747   ap/canvas 0.5126
        //     weapon-frame.png  aperture 716 x 759   ap/canvas 0.5028
        //
        // RETIREMENT TRIGGER: re-cutting either ring means re-running that script over BOTH of them
        // and updating ApertureFraction (take the SMALLER ap/canvas) and the bolt fractions below
        // in the same commit. Installing one ring alone silently breaks the co-registration these
        // constants assume, and nothing renders an error — the art just sits off-centre.
        private const float ApertureFraction = 0.5028f;
        // The illustration is inset a hair inside the smaller aperture so a little metal always
        // overlaps it, rather than art and rim meeting on a seam.
        private const float ArtInset = 0.96f;
        private const float SocketDiameter = FrameSize * ApertureFraction * ArtInset;

        // Zero BY CONSTRUCTION, not by measurement — see the alignment note above. Kept as named
        // constants because ClickableHeroPower positions its collider from the power one, and a
        // future non-aligned ring would need them back.
        internal static readonly Vector2 PowerApertureOffset = Vector2.zero;
        private static readonly Vector2 WeaponApertureOffset = Vector2.zero;

        // Filenames match their roles again. They did not: the first exports had the blades on
        // `hero-power.png` and the molten ring on `weapon-frame.png`, so this view mapped them
        // crosswise. Sam's 2026-09-03 re-cut fixed the art, and the two files were overwritten in
        // place (guids, and therefore the Addressables entries, preserved).
        private const string PowerFrameKey = "hero-power";
        private const string WeaponFrameKey = "weapon-frame";

        // WHERE THE WEAPON NUMERALS GO: attack at the ring's bottom-left, durability at its
        // bottom-right, large and outlined, over bare rim (Sam, 2026-09-30: "move it to the bottom
        // left and right of the frame ... and increase the size of them... alot"). They sat on the
        // two stacked rivets at the right edge until then, and at a size the rivet could hold they
        // were illegible on a phone. Canvas fractions of the 1424 canvas, top-left origin, which
        // is the convention MakeStat consumes; the ring's rim runs ~0.19..0.82 down.
        private static readonly Vector2 WeaponAtkAt  = new Vector2(0.24f, 0.80f);
        private static readonly Vector2 WeaponDuraAt = new Vector2(0.80f, 0.80f);

        /// <summary>Weapon numeral em height as a fraction of <see cref="FrameSize"/> — roughly
        /// the hero's health numeral on screen, twice the card stat digit. Sized to be read at a
        /// glance, not to fit any feature of the ring.</summary>
        private const float WeaponDigitEmFrac = 0.36f;

        // THE HERO-POWER COST DOES NOT GO ON A RIVET, and that is not an oversight.
        // The original reason has expired and is recorded because it reads as still-live: the four
        // rivets used to sit in the narrow gap between two abutting frames, so a cost on the
        // power's rivet landed a digit's width from the weapon's attack and read as a third weapon
        // stat. The portrait sits between the two rings since 2026-09-04 — they are 2 x 1.59 apart
        // — and that collision is impossible now.
        //
        // What survives is the reason the lower rim WORKS: it is outside the aperture radius
        // (ApertureFraction * 0.5 = 0.251 from centre, against 0.28 here), so the cost never covers
        // the power's own art, which a rivet 0.115 across cannot promise. Moving it onto a rivet is
        // an open option now rather than a blocked one — a look, not a fix.
        private static readonly Vector2 PowerCostAt = new Vector2(0.50f, 0.78f);

        // The sockets are bottom-docked in world space but must composite over the hero plate.
        // HeroPlateView occupies orders 8..10; sharing those values made overlap depend on
        // renderer submission order and let the portrait swallow the upper half of each frame.
        private const int FrameOrder = 11;
        private const int ArtOrder = 12;
        private const int StatOrder = 13;

        // Segments around the socket disc. 64 is smooth at 88 px (the socket at board scale) and
        // still smooth if the frames are ever drawn larger for a bake-off sheet.
        private const int DiscSegments = 64;

        /// <summary>Hero-power cost em height as a fraction of <see cref="FrameSize"/>.
        ///
        /// <para>0.11 -> 0.17 on 2026-09-30 (#400). 0.11 was judged against a 1.32-unit socket at
        /// ~155 px on a 1080-tall board — the ORTHOGRAPHIC camera retired by f121a591. Under the
        /// live perspective rig the socket is ~95 px and 0.11 drew ~8 px tall. 0.17 is just over
        /// the card stat digit's em (StatDigitEm x CardH = 0.23 units), the smallest numeral the
        /// board already proves readable. Size against the live camera, never against a unit
        /// count.</para></summary>
        private const float DigitEmFrac = 0.17f;

        // Black outline, the recipe BoardController gives card stat digits, because these sit
        // on bright brass rather than a dark boss. One shared material per face so the four
        // labels on the board still batch.
        private static Material s_statMaterial;

        private static readonly Color PowerReady = new Color(1.00f, 1.00f, 1.00f);
        private static readonly Color PowerSpent = new Color(0.42f, 0.42f, 0.48f);
        private static readonly Color AttackText = new Color(1.00f, 0.93f, 0.62f);
        private static readonly Color DuraText   = new Color(0.78f, 0.86f, 1.00f);
        // Same values as CardPresenter's, so a buffed weapon reads identically on the ring and
        // on the card it was played from.
        private static readonly Color BuffColor   = new Color(0.20f, 0.80f, 0.25f);
        private static readonly Color DamageColor = new Color(0.90f, 0.20f, 0.20f);

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

        // THE EQUIPPED WEAPON IS READABLE (Sam, 2026-09-30: "you should be able to mouse over an
        // equipped weapon and still see its large size card preview"). The socket reuses the board
        // cards' reveal pipeline — one CardRevealTrigger on its own collider, one live state fed
        // from ps.Weapon — rather than a second preview. Rebuilt per weapon INSTANCE, so a replaced
        // or destroyed weapon takes its trigger (and through Unregister, any open reveal) with it.
        private CardRevealController _reveal;
        private CardRevealTrigger    _weaponTrigger;
        private bool _tabletop;
        private CardLiveState        _weaponLive;
        private InstanceId           _weaponId;

        /// <summary>Attach to a new GameObject and call Init once before the first
        /// OnStateUpdated. Identified by PlayerId, never by seat index (HeroPlateView
        /// precedent — a networked seat-1 local renders at visual bottom).</summary>
        public void Init(PlayerId playerId, Transform powerAnchor, Transform weaponAnchor,
                         ICardDatabase db, IArtProvider art,
                         IUiSpriteProvider uiSprites = null)
        {
            _playerId = playerId;
            _db       = db;
            _art      = art;
            _mpb      = new MaterialPropertyBlock();
            _tabletop = powerAnchor != null && powerAnchor.GetComponentInParent<RoyalCouncilBoard>() != null;

#if UNITY_EDITOR
            uiSprites ??= new EditorUiSpriteProvider();
#else
            uiSprites ??= new AddressableUiSpriteProvider();
#endif

            if (powerAnchor != null)
            {
                _powerRing = MakeFrame(powerAnchor, "PowerFrame", uiSprites, PowerFrameKey);
                _powerArt = MakeSocketArt(powerAnchor, "PowerArt", SocketDiameter,
                                           PowerApertureOffset);
                if (!_tabletop) _powerCost = MakeStat(powerAnchor, "PowerCost", PowerCostAt, Color.white, DigitEmFrac);
                if (_tabletop)
                {
                    _powerRing.enabled = false;
                    FitTableArt(_powerArt);
                }
            }

            if (weaponAnchor != null)
            {
                _weaponRoot = new GameObject("Weapon");
                _weaponRoot.transform.SetParent(weaponAnchor, false);
                _weaponRoot.transform.localPosition = Vector3.zero;

                Transform wt = _weaponRoot.transform;
                _weaponRing = MakeFrame(wt, "WeaponFrame", uiSprites, WeaponFrameKey);
                _weaponArt = MakeSocketArt(wt, "WeaponArt", SocketDiameter,
                                            WeaponApertureOffset);

                _weaponAttack     = MakeStat(wt, "Attack",     WeaponAtkAt,  AttackText, WeaponDigitEmFrac);
                _weaponDurability = MakeStat(wt, "Durability", WeaponDuraAt, DuraText,   WeaponDigitEmFrac);
                if (_tabletop)
                {
                    _weaponRing.enabled = false;
                    FitTableArt(_weaponArt);
                    FitTableDigit(_weaponAttack, new Vector3(-0.32f, -0.3f, -0.18f));
                    FitTableDigit(_weaponDurability, new Vector3(0.32f, -0.3f, -0.18f));
                }

                _weaponRoot.SetActive(false);
            }
        }

        /// <summary>Makes the equipped weapon inspectable through the match's reveal controller.
        /// Optional: a mode without one (the decision review) simply never gets a trigger.</summary>
        internal void AttachReveal(CardRevealController controller) => _reveal = controller;

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

            CardInstance w = ps.Weapon;
            if (w == null) { SetWeapon(false, 0, 0, null, 0, 0); ClearWeaponReveal(); return; }

            // Base values so a buffed or damaged weapon reads as such — carried over from
            // CardPresenter.SetWeaponStats, which used to render the equipped weapon as a card.
            CardDefinition wdef = _db?.Get(w.DefinitionId);
            int durability = w.CurrentDurability ?? 0;
            SetWeapon(true, w.CurrentAttack, durability, wdef?.ArtId,
                      wdef?.Attack ?? w.CurrentAttack, wdef?.Durability ?? durability);
            SyncWeaponReveal(w, wdef);
        }

        private void SyncWeaponReveal(CardInstance w, CardDefinition def)
        {
            if (_reveal == null || _weaponRoot == null || def == null) { ClearWeaponReveal(); return; }
            if (_weaponTrigger != null && !_weaponId.Equals(w.Id)) ClearWeaponReveal();

            if (_weaponTrigger == null)
            {
                _weaponId   = w.Id;
                _weaponLive = new CardLiveState(w.Id, equippedWeapon: true);
                _weaponLive.UpdateFrom(w);

                var go = new GameObject("WeaponReveal");
                go.transform.SetParent(_weaponRoot.transform, false);
                go.transform.localPosition = Vector3.zero;

                // Sized to the drawn ring's aperture, like the hero power's tap target, and pushed
                // BEHIND the hero plate's box (z -0.05..0.05) so the plate keeps the strip where the
                // ring laps onto it. The collider carries no ClickableCard, ClickableHero or
                // ClickableHeroPower descriptor, so InputRouter.Probe walks straight past it:
                // pressing, dragging and targeting behave exactly as before, and only
                // BoardPointerRelay — which the reveal trigger listens to — sees it.
                var col = go.AddComponent<BoxCollider>();
                col.size   = new Vector3(HitDiameter, HitDiameter, 0.1f);
                col.center = new Vector3(WeaponApertureOffset.x, WeaponApertureOffset.y, 0.1f);
                if (_tabletop) { col.size = new Vector3(1f, 1f, 0.1f); col.center = Vector3.zero; }

                _weaponTrigger = go.AddComponent<CardRevealTrigger>();
                _weaponTrigger.InitEquippedWeapon(w.Id, def, _weaponLive, _reveal);
            }
            else
            {
                // Attack and durability change under a raised panel; the focus card is bound to
                // this state and repaints from its Changed event.
                _weaponLive.UpdateFrom(w);
            }
        }

        // Destroying the trigger's object is what closes an open reveal: its OnDestroy unregisters
        // from the controller, which cancels the model when this weapon was the one on screen.
        private void ClearWeaponReveal()
        {
            if (_weaponTrigger != null)
            {
                GameObject go = _weaponTrigger.gameObject;
                if (Application.isPlaying) Destroy(go);
                else DestroyImmediate(go);
            }
            _weaponTrigger = null;
            _weaponLive    = null;
            _weaponId      = default;
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

        public void SetWeapon(bool present, int attack, int durability, string artId,
                              int baseAttack, int baseDurability)
        {
            if (_weaponRoot == null) return;
            _weaponRoot.SetActive(present);
            if (!present) return;

            if (_weaponAttack != null)
            {
                _weaponAttack.text  = attack.ToString();
                _weaponAttack.color = attack > baseAttack ? BuffColor
                                    : attack < baseAttack ? DamageColor
                                    : AttackText;
            }
            if (_weaponDurability != null)
            {
                _weaponDurability.text  = durability.ToString();
                // Durability only ever falls, so under base always means worn, never buffed.
                _weaponDurability.color = durability < baseDurability ? DamageColor : DuraText;
            }
            LoadInto(_weaponArt, artId, ref _weaponArtId);
        }

        // ── Build helpers ───────────────────────────────────────────────────────────────
        private static void FitTableArt(Renderer art)
        {
            art.transform.localPosition = Vector3.back * 0.015f;
            art.transform.localScale = new Vector3(RoyalCouncilBoard.SocketDiameter, RoyalCouncilBoard.SocketDiameter, 1f);
        }

        private static void FitTableDigit(TextMeshPro label, Vector3 position)
        {
            label.transform.localPosition = position;
            label.fontSize = 4.2f;
            label.rectTransform.sizeDelta = new Vector2(0.7f, 0.55f);
        }

        private static SpriteRenderer MakeFrame(Transform parent, string goName,
                                                IUiSpriteProvider uiSprites, string key)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            var sr = go.AddComponent<SpriteRenderer>();
            // Per-texture material: see SpriteMaterials. These frames are several different
            // textures on one shared material, which is exactly the case that draws them all
            // as whichever one wins the texture bind.
            SpriteMaterials.Set(sr, uiSprites?.Get(key));
            sr.sortingOrder = FrameOrder;
            if (sr.sprite != null)
            {
                Vector2 native = sr.sprite.bounds.size;
                float scale = FrameSize / Mathf.Max(native.x, native.y);
                go.transform.localScale = new Vector3(scale, scale, 1f);
            }
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
        private Renderer MakeSocketArt(Transform parent, string goName, float diameter,
                                       Vector2 apertureOffset)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(apertureOffset.x, apertureOffset.y, -0.01f);
            go.transform.localScale    = new Vector3(diameter, diameter, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = DiscMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sortingOrder = ArtOrder;
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
        /// centre in canvas fractions with a top-left origin, which is how both the splitter and
        /// the alignment run report it; y flips because world space grows upward.</summary>
        private static TextMeshPro MakeStat(Transform parent, string goName, Vector2 frac,
                                            Color color, float emFrac)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(
                (frac.x - 0.5f) * FrameSize,
                (0.5f - frac.y) * FrameSize,
                -0.03f);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.alignment        = TextAlignmentOptions.Center;
            tmp.color            = color;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.fontStyle        = FontStyles.Bold;   // ApplyUiFont swaps this for the bold CUT
            BoardController.ApplyUiFont(tmp);
            if (tmp.font != null)
            {
                if (s_statMaterial == null || s_statMaterial.mainTexture != tmp.font.material.mainTexture)
                {
                    s_statMaterial = new Material(tmp.font.material) { name = "HeroSocketStat" };
                    s_statMaterial.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);
                    s_statMaterial.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.25f);
                    s_statMaterial.EnableKeyword(ShaderUtilities.Keyword_Outline);
                }
                tmp.fontSharedMaterial = s_statMaterial;
            }

            // SAME RECIPE AS CardPresenter.PlaceDigit, deliberately: autosizing OFF, a rect
            // roomier than the glyph, and fontSize as em-height x 10.
            //
            // Autosizing plus a rect clamped to the boss was the bug — TMP was free to shrink a
            // digit to fit a 0.265-unit box, so the numerals rendered between a half and a
            // quarter of the size of the digits on the cards (Sam spotted it on screen,
            // 2026-07-25). A numeral is one or two characters; it never needs to reflow.
            tmp.enableAutoSizing = false;
            tmp.fontSize         = emFrac * FrameSize * 10f;
            tmp.rectTransform.sizeDelta = new Vector2(emFrac * FrameSize * 2f, emFrac * FrameSize * 1.4f);

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
