using Kingdom.Core;
using Kingdom.Core.Contracts;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// World-space mana tray for one player (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1 ManaTrayAnchor, hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/design-system.md §4.4).
    ///
    /// Renders a vertical two-column by five-row grid at ManaTrayAnchor. SG_ManaFill draws all ten
    /// carved sockets on one quad; the grid fills from the battlefield outward for either seat.
    /// The four ManaPool states (Available / Spent / Locked / Temporary) go onto a
    /// MaterialPropertyBlock as 0..1 ratios.
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
        // Each cell is 0.46 x 0.46 world units: RailW/2 == RailH/5. At _SocketRadius 0.47 an orb
        // is 0.432 units across, 28% larger than the old 0.338 horizontal rail orb, with a
        // 0.028-unit gap so adjacent bezels remain separate.
        // Grown 40% on 2026-10-02 (Sam: larger), from 0.92 x 2.30: cells are now 0.644 and an orb
        // 0.605 across. The shader sizes sockets from the cell, so nothing else follows.
        internal const float RailW = 1.288f;
        internal const float RailH = 3.22f;
        internal const int GridColumns = 2;
        internal const int GridRows = 5;
        private const float SurfaceRollDeg = 22f;

        private static readonly int FillLevelId   = Shader.PropertyToID("_FillLevel");
        private static readonly int SpentLevelId  = Shader.PropertyToID("_SpentLevel");
        private static readonly int LockedLevelId = Shader.PropertyToID("_LockedLevel");
        private static readonly int TempLevelId   = Shader.PropertyToID("_TempLevel");
        private static readonly int CrystalCountId= Shader.PropertyToID("_CrystalCount");
        private static readonly int GridColumnsId = Shader.PropertyToID("_GridColumns");
        private static readonly int FlipVerticalId= Shader.PropertyToID("_FlipVertical");

        private PlayerId _playerId;
        private Renderer _quad;
        private MaterialPropertyBlock _mpb;

        /// <summary>
        /// Attach to a new GameObject and call Init once before first OnStateUpdated.
        /// Identified by PlayerId, never by seat/visual index (see HeroPlateView.Init).
        /// </summary>
        public void Init(PlayerId playerId, Transform anchor, bool isLocalSeat)
        {
            _playerId = playerId;
            transform.SetParent(anchor, false);
            transform.localPosition = Vector3.zero;
            // Keep the procedural sockets round in their own quad, then rotate the complete tray
            // to follow the table's side rail instead of leaving a perfectly vertical UI grid over
            // angled furniture. The table is a trapezoid, so its two rails lean OPPOSITE ways: the
            // local tray's top leans left along the right rail, the opponent's leans right along
            // the left one (was one shared 8 degrees; 22 and mirrored, Sam 2026-10-02).
            transform.localRotation = Quaternion.Euler(0f, 0f, isLocalSeat ? SurfaceRollDeg : -SurfaceRollDeg);

            // One quad, ten procedural cells. The shader derives cell aspect from this transform,
            // so the sockets stay round and no per-orb hierarchy is needed.
            var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = "ManaTrayQuad";
            quadGo.transform.SetParent(transform, false);
            quadGo.transform.localScale    = new Vector3(RailW, RailH, 1f);
            quadGo.transform.localPosition = Vector3.zero;
            // Remove the primitive collider in either edit mode or play mode.
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
            _mpb.SetFloat(GridColumnsId, GridColumns);
            // Every portrait is upright. The local/bottom grid therefore starts at its top row
            // (toward the battlefield), while the opponent/top grid starts at its bottom row.
            _mpb.SetFloat(FlipVerticalId, isLocalSeat ? 1f : 0f);
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
