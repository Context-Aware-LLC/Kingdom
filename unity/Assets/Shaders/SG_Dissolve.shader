// SG_Dissolve — the death burn. Eats a whole card away, ember-edged, over 0.40s.
//
// Supersedes SG_Dissolve.shadergraph, which no material references. Authored as hand-HLSL
// on 2026-07-25 because the graph did not compile then: its SGF_DissolveCore subgraph
// named the noise node's type `SimpleNoiseNode`, a class Shader Graph does not have (it
// is `NoiseNode`), so no code was generated for the node and its output was undeclared
// wherever it was read. The type name was corrected on 2026-10-08. The orphaned
// Assets/Shaders/Functions/SGF_DissolveCore.hlsl documents itself as a Custom Function
// node the subgraph does not contain — its clip/edge-band logic is reimplemented below,
// which is the only copy that now runs.
//
// Property names and semantics are unchanged from the graph's contract, so the C# binding
// layer (MaterialCatalog.Dissolve, ShaderIds, CardDeathDissolve) needed no edits.
//
// WHAT IT IS FED. _BaseMap is not card art: CardDeathDissolve snapshots the dying card's
// entire visual subtree — body, illustration, frame, gem, keyword icons, six TMP labels —
// into one RenderTexture and hands it here. The burn therefore crosses the composite as a
// single surface, which is the whole reason the effect can exist: no per-renderer material
// swap dissolves 13 renderers across 3 material families coherently.
//
// TRANSPARENT, NOT OPAQUE+ALPHACLIP. The snapshot carries the card's own antialiased
// silhouette — its corners are rounded and the backdrop shows through them. Clipping that
// at a 0.5 alpha threshold would make the card visibly POP to jagged corners on the frame
// the burn starts, so _DissolveAmount = 0 would not match the live card it replaces. Alpha
// blending keeps that first frame pixel-faithful (which is exactly what
// Kingdom/Preview Card Dissolve's live-reference render checks) while the burn holes stay
// hard-edged, because they come from a step() and not from the blend. Safe against the
// depth-vs-sort-order trap this project keeps paying for: board cards never overlap, and a
// minion only ever dies on the board.
Shader "Kingdom/SG_Dissolve"
{
    Properties
    {
        [MainTexture] _BaseMap ("Card Snapshot", 2D) = "white" {}

        _DissolveAmount ("Dissolve Amount", Range(0,1)) = 0
        // Width of the ember band trailing the burn front, in noise units. 0.08 at
        // _NoiseScale 12 is a band a few pixels wide at true board scale — enough to read
        // as heat, narrow enough not to wash the card out.
        _EdgeWidth      ("Edge Width", Range(0, 0.3)) = 0.08
        // Noise cells across the card's SHORT axis. Deliberately not the graph's 20: a card
        // is 137x194 px on screen (ortho 4.6, ~117 px/world unit), so 20 leaves ~7 px per
        // cell and the burn reads as fizz rather than as fire.
        _NoiseScale     ("Noise Scale", Float) = 12
        [HDR] _EdgeColor ("Ember Edge", Color) = (4.0, 1.2, 0.15, 1)
        // Per-card slide through the noise field, set by CardDeathDissolve from the card's world
        // position. Without it every card burns through holes in exactly the same places, which a
        // single death hides completely and a board wipe makes obvious and cheap-looking.
        _NoiseOffset ("Noise Offset", Vector) = (0, 0, 0, 0)
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
            // Both faces: the quad is planted at the dead card's world pose and nothing
            // guarantees which side of it the board camera ends up on.
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float  aspect      : TEXCOORD1;  // quad world width / world height
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _EdgeColor;
                float  _DissolveAmount;
                float  _EdgeWidth;
                float  _NoiseScale;
                float4 _NoiseOffset;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);

                // Aspect off the transform, not a pushed constant (SG_ManaFill precedent):
                // noise cells stay square whatever the card's proportions are, so the burn
                // never reads as vertical streaking on a 1.17 x 1.65 quad.
                float sx = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                float sy = length(float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21));
                OUT.aspect = sx / max(sy, 1e-5);
                return OUT;
            }

            float Hash (float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            // Smoothed value noise. Cheap enough to run per-pixel on mobile: two lerps and a
            // smoothstep, no derivatives, no texture fetch.
            float ValueNoise (float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 src = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);

                // Square cells: scale x by the aspect so _NoiseScale counts cells across the
                // short axis and the long axis gets proportionally more.
                float2 p = float2(IN.uv.x * IN.aspect, IN.uv.y) * _NoiseScale + _NoiseOffset.xy;

                // Two octaves. One alone gives a blobby, obviously-procedural front; the
                // finer octave is what makes the burn edge ragged at the pixel level.
                float n = 0.66 * ValueNoise(p) + 0.34 * ValueNoise(p * 2.3 + 17.0);

                // Value noise clusters hard around 0.5, so an unstretched threshold does
                // almost nothing until 0.4 and then takes the whole card at once. The
                // stretch is what makes the sweep read as a burn at EVERY amount rather
                // than as a card that survives, blinks, and is gone.
                n = saturate((n - 0.5) * 1.9 + 0.5);

                // Pushed just past both ends. The stretch above lets n reach exactly 0 and 1, so
                // an untouched threshold leaves surviving speckles at amount 1 and clips a few
                // pixels at amount 0 — the two frames that have to be absolutely clean, because
                // one has to match the live card and the other has to be empty.
                float t     = _DissolveAmount * 1.02 - 0.01;
                float front = n - t;
                float alive = step(0.0, front);

                // Ember band, hottest right at the front and falling off behind it.
                float edge = alive * (1.0 - smoothstep(0.0, max(_EdgeWidth, 1e-5), front));
                // Gated on having actually started: at amount 0 every pixel with n < EdgeWidth
                // would otherwise glow, and a card sitting at rest on the board would wear a
                // permanent ember rash.
                edge *= saturate(_DissolveAmount / 0.05);

                half3 col = src.rgb + edge * _EdgeColor.rgb;
                return half4(col, src.a * alive);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
