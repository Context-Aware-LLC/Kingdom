// SG_ManaFill — mana-crystal tray, all four ManaPool states.
//
// Supersedes SG_ManaFill.shadergraph (single-quad, _FillLevel only, unmet
// _CrystalMask dependency). Authored as hand-HLSL on 2026-07-18 because the
// signed-off design (ui/design-system.md §4.4, ui/match-hud.md §2) and the live
// binder (ManaTrayView.cs) both carry _Spent/_Locked/_Temp levels the graph
// never implemented — and the graph could not be safely edited without the
// Shader Graph editor. See ui/visual-contract.md §Contradictions 1.
//
// Model: the tray is 10 crystals derived procedurally from UV.x (no mask
// texture). Each crystal is colored by cumulative bands, left→right:
//   Available (blue) → Spent (dim) → Locked (overload) → Temporary → Empty.
// Each level is a 0..1 ratio (count / cap) pushed per-instance via MPB.
// Opaque, ZWrite On, single variant — matches SG_ManaFill.md cost profile.
Shader "Emberfall/SG_ManaFill"
{
    Properties
    {
        [HDR] _FilledColor ("Filled (Available)", Color) = (0.056, 0.248, 0.792, 1)
        _EmptyColor  ("Empty Socket", Color)   = (0.12, 0.12, 0.14, 1)
        _SpentColor  ("Spent", Color)          = (0.25, 0.25, 0.35, 1)
        [HDR] _LockedColor ("Locked (Overload)", Color) = (0.55, 0.18, 0.20, 1)
        [HDR] _TempColor   ("Temporary", Color)         = (0.30, 0.85, 0.45, 1)

        _FillLevel   ("Fill Level", Range(0,1))   = 0
        _SpentLevel  ("Spent Level", Range(0,1))  = 0
        _LockedLevel ("Locked Level", Range(0,1)) = 0
        _TempLevel   ("Temp Level", Range(0,1))   = 0

        _CrystalCount ("Crystal Count", Float)        = 10
        _Gap          ("Inter-crystal Gap", Range(0,0.5)) = 0.12
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float4 _FilledColor;
                float4 _EmptyColor;
                float4 _SpentColor;
                float4 _LockedColor;
                float4 _TempColor;
                float  _FillLevel;
                float  _SpentLevel;
                float  _LockedLevel;
                float  _TempLevel;
                float  _CrystalCount;
                float  _Gap;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float n     = max(1.0, _CrystalCount);
                float p     = saturate(IN.uv.x);
                float slot  = floor(p * n);
                float idx   = (slot + 0.5) / n;   // crystal center, 0..1 — band test point
                float local = frac(p * n);        // position within this crystal

                // Cumulative thresholds. Innermost (idx < t1) wins, so precedence is
                // Available > Spent > Locked > Temp > Empty.
                float t1 = _FillLevel;
                float t2 = t1 + _SpentLevel;
                float t3 = t2 + _LockedLevel;
                float t4 = t3 + _TempLevel;

                half3 col = _EmptyColor.rgb;
                col = (idx < t4) ? _TempColor.rgb   : col;
                col = (idx < t3) ? _LockedColor.rgb : col;
                col = (idx < t2) ? _SpentColor.rgb  : col;
                col = (idx < t1) ? _FilledColor.rgb : col;

                // Gaps between crystals read as dark sockets so the 10 segments separate.
                float halfGap   = _Gap * 0.5;
                float inCrystal = step(halfGap, local) * step(local, 1.0 - halfGap);
                col = lerp(_EmptyColor.rgb * 0.35, col, inCrystal);

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
