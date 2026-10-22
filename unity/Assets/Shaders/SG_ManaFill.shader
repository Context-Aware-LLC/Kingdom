// SG_ManaFill — mana-crystal tray, all four ManaPool states.
//
// Supersedes SG_ManaFill.shadergraph (single-quad, _FillLevel only, unmet
// _CrystalMask dependency). Authored as hand-HLSL on 2026-07-18 because the
// signed-off design (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/design-system.md §4.4, hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/match-hud.md §2) and the live
// binder (ManaTrayView.cs) both carry _Spent/_Locked/_Temp levels the graph
// never implemented — and the graph could not be safely edited without the
// Shader Graph editor. See hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/visual-contract.md §Contradictions 1.
//
// Model: the tray is 10 ORBS derived procedurally from a configurable UV grid
// (no mask texture), each a carved socket with a gem seated in it. The shipping
// layout is two columns by five rows. Each orb is colored by cumulative bands:
//   Available (blue) → Spent (dim) → Locked (backdraft) → Temporary → Empty.
// Each level is a 0..1 ratio (count / cap) pushed per-instance via MPB.
//
// Round, not rectangular: hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §1.1 specifies "~0.32Ø" — a diameter,
// not a cell. The cell is 0.403 wide and 0.32 tall, so orbs are circularised by
// the aspect the vertex stage reads off the object-to-world scale. That derives
// from the transform rather than a pushed constant, so the orb cannot go oval
// if the rail is ever resized.
//
// Transparent, ZWrite Off: the sockets are round, so the corners of every cell
// must let BoardBackdrop (z +5, sortingOrder -1000) through. An opaque quad
// would punch a black rectangle over the board plate.
Shader "Kingdom/SG_ManaFill"
{
    Properties
    {
        // No [HDR]: GammaTuned.hlsl says why.
        _FilledColor ("Filled (Available)", Color) = (0.056, 0.248, 0.792, 1)
        _EmptyColor  ("Empty Socket", Color)   = (0.12, 0.12, 0.14, 1)
        _SpentColor  ("Spent", Color)          = (0.25, 0.25, 0.35, 1)
        _LockedColor ("Locked (Backdraft)", Color) = (0.55, 0.18, 0.20, 1)
        _TempColor   ("Temporary", Color)         = (0.30, 0.85, 0.45, 1)
        _SocketColor ("Socket Stone", Color)   = (0.165, 0.152, 0.138, 1)

        _FillLevel   ("Fill Level", Range(0,1))   = 0
        _SpentLevel  ("Spent Level", Range(0,1))  = 0
        _LockedLevel ("Locked Level", Range(0,1)) = 0
        _TempLevel   ("Temp Level", Range(0,1))   = 0

        _CrystalCount ("Crystal Count", Float)             = 10
        _GridColumns  ("Grid Columns", Float)              = 2
        _FlipVertical ("Flip Vertical Fill", Float)        = 0
        _SocketRadius ("Socket Radius", Range(0.1, 0.5))   = 0.470
        _GemRadius    ("Gem Radius", Range(0.05, 0.5))     = 0.355
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/Functions/GammaTuned.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float  aspect      : TEXCOORD1;  // quad world width / world height
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _FilledColor;
                float4 _EmptyColor;
                float4 _SpentColor;
                float4 _LockedColor;
                float4 _TempColor;
                float4 _SocketColor;
                float  _FillLevel;
                float  _SpentLevel;
                float  _LockedLevel;
                float  _TempLevel;
                float  _CrystalCount;
                float  _GridColumns;
                float  _FlipVertical;
                float  _SocketRadius;
                float  _GemRadius;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;

                // Read the rail's proportions off the transform rather than trusting a
                // pushed constant — the orbs stay circular through any rail resize.
                float sx = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                float sy = length(float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21));
                OUT.aspect = sx / max(sy, 1e-5);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float n = max(1.0, _CrystalCount);
                float cols = clamp(round(_GridColumns), 1.0, n);
                float rows = ceil(n / cols);
                float px = saturate(IN.uv.x);
                float py = saturate(IN.uv.y);

                // Clamp before floor so UV == 1.0 stays in the last column/row.
                float column = floor(min(px * cols, cols - 1e-4));
                float visualRow = floor(min(py * rows, rows - 1e-4));
                float flowRow = _FlipVertical > 0.5 ? rows - 1.0 - visualRow : visualRow;
                float slot = flowRow * cols + column;
                float idx = (slot + 0.5) / n; // crystal center, 0..1 — band test point

                float localX = px * cols - column;
                float localY = py * rows - visualRow;

                // Cell coordinates in units of CELL HEIGHT. Width/cols divided by height/rows
                // gives this aspect, which keeps every socket circular when the whole grid is
                // resized or rotated.
                float cellAspect = IN.aspect * rows / cols;
                float2 q = float2((localX - 0.5) * cellAspect, localY - 0.5);
                float  r = length(q);

                // Cumulative thresholds. Assigned innermost-last, so precedence is
                // Available > Spent > Locked > Temp > Empty.
                float t1 = _FillLevel;
                float t2 = t1 + _SpentLevel;
                float t3 = t2 + _LockedLevel;
                float t4 = t3 + _TempLevel;

                // `live` is how lit the gem is — it drives the core, the specular and the
                // halo together, so a spent orb reads as the same crystal gone dark rather
                // than as a differently-shaped thing. `hasGem` is the harder distinction:
                // above the mana cap there is no crystal at all, only the bare socket, so
                // "spent this turn" and "never had it" cannot be confused.
                // Every constant below was tuned by eye in Gamma (GammaTuned.hlsl).
                float3 c      = TunedIn(_EmptyColor.rgb);
                float  live   = 0.0;
                float  hasGem = 0.0;
                if (idx < t4) { c = TunedIn(_TempColor.rgb);   live = 0.85; hasGem = 1.0; }
                if (idx < t3) { c = TunedIn(_LockedColor.rgb); live = 0.55; hasGem = 1.0; }
                if (idx < t2) { c = TunedIn(_SpentColor.rgb);  live = 0.16; hasGem = 1.0; }
                if (idx < t1) { c = TunedIn(_FilledColor.rgb); live = 1.00; hasGem = 1.0; }
                float3 socketStone = TunedIn(_SocketColor.rgb);

                float aa = max(fwidth(r), 1e-5);
                float topness = q.y / max(r, 1e-4);   // +1 at the top of the orb, -1 at the bottom

                // ── Gem ──────────────────────────────────────────────────────────────
                // Falloff stays gentle and the rim light sits hard against the edge: a
                // broad mid-gem rim light reads as a concentric ring — a button, not a gem.
                float  d   = saturate(r / max(_GemRadius, 1e-4));
                float3 gem = c * (0.34 + 0.42 * live);
                gem *= 1.0 - 0.22 * d * d;
                gem += c * live * pow(saturate(1.0 - d), 2.0) * 1.05;       // core glow
                gem += c * live * smoothstep(0.88, 1.0, d) * 0.90;          // thin lit edge

                // Specular blob, upper-left — one light for the whole rail.
                float2 hq   = (q - float2(-0.105, 0.108)) / float2(0.095, 0.070);
                float  spec = saturate(1.0 - dot(hq, hq));
                gem += (0.10 + 0.85 * live) * spec * spec;

                // Empty socket: no crystal, just the floor of the well, shadowed under
                // the near wall. Replaces the gem body wholesale rather than dimming it —
                // a dimmed gem still reads as a shiny black sphere.
                float3 well = socketStone * (0.26 - 0.12 * topness);
                gem = lerp(well, gem, hasGem);

                // ── Socket ───────────────────────────────────────────────────────────
                // Two zones: a raised bezel lit from above on the outside, and the well
                // wall inside it, which is shadowed at the top and catches light at the
                // bottom. The reversal between them is what reads as "carved".
                float  ringT    = saturate((_SocketRadius - r) / max(_SocketRadius - _GemRadius, 1e-4));
                float  wall     = smoothstep(0.30, 0.70, ringT);
                float  lightFac = lerp(1.05 + 0.80 * topness, 0.80 - 0.50 * topness, wall);
                float3 socket   = socketStone * saturate(lightFac);
                socket += c * live * 0.20;                 // a lit gem spills onto its own rim

                // ── Composite ────────────────────────────────────────────────────────
                float gemMask = 1.0 - smoothstep(_GemRadius - aa, _GemRadius + aa, r);
                float orbMask = 1.0 - smoothstep(_SocketRadius - aa, _SocketRadius + aa, r);

                float3 col = lerp(socket, gem, gemMask);

                // Halo just outside the socket, live orbs only. Clipped by the quad edge
                // top and bottom by design — it reads as bleed off the rail, not a ring.
                float halo = live * exp2(-max(r - _SocketRadius, 0.0) * 26.0) * (1.0 - orbMask);
                halo = TunedGlowAlpha(saturate(halo) * 0.55);

                col = lerp(c * 1.2, col, orbMask);
                float alpha = saturate(orbMask + halo);

                return half4(TunedOut(col), alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
