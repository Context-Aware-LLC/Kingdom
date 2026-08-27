using Emberfall.Core;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// World-space mana tray for one player (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1 ManaTrayAnchor, hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/design-system.md §4.4).
    ///
    /// Renders a quad at ManaTrayAnchor whose SG_ManaFill shader draws MatchEngine.MaxMana orbs,
    /// each a socket with a crystal seated in it. The four ManaPool states (Available / Spent /
    /// Locked / Temporary) go onto a MaterialPropertyBlock as 0..1 ratios.
    ///
    /// Every level is a fraction of the TEN SOCKETS, never of ManaPool.Max — see OnStateUpdated.
    ///
    /// The rail carries no numeric read-out. The "X / Y" TMP label was an M1 placeholder from
    /// when the tray was a uniform-tint quad and the count was not otherwise readable; once the
    /// orbs became countable it was saying the same thing twice (Sam, 2026-07-25).
    ///
    /// Created procedurally by NetworkedGameBootstrap — one instance per player.
    /// </summary>
    public sealed class ManaTrayView : MonoBehaviour
    {
        // hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1 mana rail: 4.03 wide (x +2.5..+6.53) x 0.32 tall.
        private const float RailW = 4.03f;

        // #234: THE ORB'S SIZE IS THIS NUMBER, NOT THE PITCH. SG_ManaFill normalises its radii to
        // the quad's HEIGHT — `r = 0.5` touches the rail's top and bottom — so the drawn socket is
        // `2 x _SocketRadius x RailH` = 0.94 x RailH and nothing else. The ticket read the orb off
        // the pitch and called it 0.40 units / 2.9 mm; the orb was really 0.3008 units, 35.3 px,
        // 2.16 mm on a 1080p phone. It was even smaller than the complaint said.
        //
        // 0.32 -> 0.36 takes the orb to 0.3384 units, 39.7 px, 2.43 mm. Vertically that is nearly
        // free — the rail is centred at |y| 4.00 and the baseline it must not cross is 4.45, so
        // there is room for RailH up to about 0.9 — but HORIZONTALLY it is nearly spent:
        //
        //   gap between adjacent orbs = RailW/10 - 2 * _SocketRadius * RailH = 0.4030 - 0.94 * RailH
        //     RailH 0.32 (before)  ->  0.1022 units, 12.0 px
        //     RailH 0.36 (now)     ->  0.0646 units,  7.6 px
        //     RailH 0.4287         ->  0, the orbs touch and the rail reads as a solid bar
        //
        // SO THE ORB IS PITCH-LIMITED, NOT HEIGHT-LIMITED, AND #234's "nearly free" IS WRONG.
        // Past this the rail has to WIDEN, and it cannot: it owns x +2.5..+6.53 between the hand
        // fan and the deck pile, the deck pile's outer edge is 7.83 against a 7.88 design
        // half-width, and the hand fan wants to spread the other way. Every one of those is the
        // same shared budget. Widening it is #235's decision (the minimum supported aspect), not a
        // tuning call here.
        //
        // RETIREMENT TRIGGER: if #235 raises the minimum aspect or moves the deck pile outward,
        // re-derive this against the new RailW — the gap arithmetic above is the whole constraint
        // and it is written down nowhere else.
        private const float RailH = 0.36f;

        private static readonly int FillLevelId   = Shader.PropertyToID("_FillLevel");
        private static readonly int SpentLevelId  = Shader.PropertyToID("_SpentLevel");
        private static readonly int LockedLevelId = Shader.PropertyToID("_LockedLevel");
        private static readonly int TempLevelId   = Shader.PropertyToID("_TempLevel");
        private static readonly int CrystalCountId= Shader.PropertyToID("_CrystalCount");

        private PlayerId _playerId;
        private Renderer _quad;
        private MaterialPropertyBlock _mpb;

        /// <summary>
        /// Attach to a new GameObject and call Init once before first OnStateUpdated.
        /// Identified by PlayerId, never by seat/visual index (see HeroPlateView.Init).
        /// </summary>
        public void Init(PlayerId playerId, Transform anchor)
        {
            _playerId = playerId;
            transform.SetParent(anchor, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;

            // Tray quad — the full mana rail of hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1: 4.03 wide, spanning
            // x +2.5..+6.53. SG_ManaFill derives all ten orbs procedurally from UV.x, so
            // the rail needs no per-orb GameObjects: at this width the pitch falls out at
            // 0.403, and the orb's diameter is 0.94 x RailH — §1.1's "0.4 pitch, ~0.32Ø" was
            // that pairing at the original RailH 0.32. The shader circularises against the
            // quad's own scale, so the orbs survive a resize.
            // Widening this is a shared budget with the hand fan — see §1.1 before changing it.
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = "ManaTrayQuad";
            quadGo.transform.SetParent(transform, false);
            quadGo.transform.localScale    = new Vector3(RailW, RailH, 1f);
            quadGo.transform.localPosition = Vector3.zero;
            // Edit-mode safe: ManaTrayPreview builds this view outside play mode, and plain
            // Destroy there logs an error and leaves the collider behind. The same
            // CreatePrimitive-then-Destroy pattern is in several other Presentation views; a view
            // is converted once an editor tool constructs it.
            var collider = quadGo.GetComponent<MeshCollider>();
            if (collider != null)
            {
                if (Application.isPlaying) Destroy(collider);
                else DestroyImmediate(collider);
            }

            _quad = quadGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(_quad, MaterialCatalog.ManaFill);
            _mpb  = new MaterialPropertyBlock();

            // Socket count is pushed rather than left to the material so it cannot drift from
            // the divisor OnStateUpdated uses — the two are the same number by construction.
            _quad.GetPropertyBlock(_mpb);
            _mpb.SetFloat(CrystalCountId, MatchEngine.MaxMana);
            _quad.SetPropertyBlock(_mpb);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_quad == null) return;

            var mana = state.Players[state.SeatOf(_playerId)].Mana;

            // Divide by the SOCKET COUNT, not by mana.Max. The rail always draws ten sockets, so
            // a level is "what fraction of the ten does this pool own" — dividing by Max instead
            // rescaled every pool to fill the whole rail, and a turn-one 1/1 player saw ten lit
            // crystals. Max is then legible as the number of non-empty sockets, which is the read
            // the empty socket exists to give.
            const float Sockets = MatchEngine.MaxMana;

            // Temporary crystals are a SUBSET of Available, not a fifth pool: GAIN_MANA's default
            // branch raises Temporary and Available together (ActionHandlers.cs). Counting both
            // would light one orb per temporary crystal twice. Spending is not attributed back to
            // a pool, so clamp to Available — once you have spent past your permanent crystals the
            // temporary ones are gone too, and this stops the rail overrunning its ten sockets.
            int temporary = Mathf.Min(mana.Temporary, mana.Available);
            int permanent = mana.Available - temporary;

            _quad.GetPropertyBlock(_mpb);
            _mpb.SetFloat(FillLevelId,   permanent    / Sockets);
            _mpb.SetFloat(SpentLevelId,  mana.Spent   / Sockets);
            _mpb.SetFloat(LockedLevelId, mana.Locked  / Sockets);
            _mpb.SetFloat(TempLevelId,   temporary    / Sockets);
            _quad.SetPropertyBlock(_mpb);
        }
    }
}
