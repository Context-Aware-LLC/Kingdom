// SG_PackWrapper — turns any 2:3 illustration into the printed face of a sealed booster.
//
// This shader is intentionally STATIC. PackWrapperBaker renders it once into a cached texture for
// UI Toolkit, whose background-image API cannot accept a custom Material. Motion belongs to the
// pack-opening animation around the image; the wrapper cues must remain stable enough to read at
// the main menu's 54 x 82 miniature.
Shader "Emberfall/SG_PackWrapper"
{
    Properties
    {
        _MainTex          ("Pack Art", 2D) = "white" {}
        _SourceRect       ("Source Rect", Vector) = (0, 0, 1, 1)
        [HideInInspector] _PieceMode ("Piece Mode", Float) = 0
        _SealFraction     ("Heat Seal Height", Range(0.06, 0.18)) = 0.105
        _CrimpCount       ("Crimp Ridge Count", Range(12, 48)) = 30
        _CrimpStrength    ("Crimp Strength", Range(0, 0.5)) = 0.28
        _FoilStrength     ("Foil Highlight", Range(0, 0.5)) = 0.20
        _EdgeDarkening    ("Edge Fold Strength", Range(0, 0.6)) = 0.34
        _SerrationTeeth   ("Cut Teeth", Range(8, 40)) = 24
        _SerrationDepth   ("Cut Depth", Range(0, 0.025)) = 0.009
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _SourceRect;
                float  _PieceMode;
                float  _SealFraction;
                float  _CrimpCount;
                float  _CrimpStrength;
                float  _FoilStrength;
                float  _EdgeDarkening;
                float  _SerrationTeeth;
                float  _SerrationDepth;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = saturate(IN.uv);

                // Whole, torn top and torn bottom are three renders of this same material. The
                // piece textures carry only their slice of the original 2:3 wrapper, so moving the
                // UI elements apart reveals a genuine separation instead of stretching two copies.
                float topPiece = step(0.5, _PieceMode) * (1.0 - step(1.5, _PieceMode));
                float bottomPiece = step(1.5, _PieceMode);
                float wholePiece = 1.0 - topPiece - bottomPiece;
                float fullY = uv.y;
                fullY = lerp(fullY, lerp(7.0 / 12.0, 1.0, uv.y), topPiece);
                fullY = lerp(fullY, lerp(0.0, 2.0 / 3.0, uv.y), bottomPiece);
                float2 fullUv = float2(uv.x, fullY);

                // Leave a small transparent gutter so even a rectangular UI element carries a
                // wrapper silhouette. The serrated machine cut is applied only to the outer ends;
                // the heat seams sit just inside it.
                const float left = 0.045;
                const float right = 0.955;
                float localX = saturate((uv.x - left) / (right - left));
                float tooth = abs(frac(localX * _SerrationTeeth) * 2.0 - 1.0);
                float cut = _SerrationDepth * (1.0 - tooth);

                float aaX = max(fwidth(fullUv.x) * 1.25, 0.001);
                float aaY = max(fwidth(fullUv.y) * 1.25, 0.001);
                float sideMask = smoothstep(left - aaX, left + aaX, fullUv.x)
                               * (1.0 - smoothstep(right - aaX, right + aaX, fullUv.x));
                float bottomEnd = smoothstep(cut - aaY, cut + aaY, fullUv.y);
                float topEnd = 1.0 - smoothstep(1.0 - cut - aaY,
                                                1.0 - cut + aaY,
                                                fullUv.y);
                float wholeEnds = bottomEnd * topEnd;

                // One full-space tear line drives both masks, so the edges are complementary even
                // though the source crops overlap. Two frequencies keep it torn, not scalloped.
                float tearWave = sin(localX * 17.0 * TWO_PI) * 0.010
                               + sin((localX * 7.0 + 0.23) * TWO_PI) * 0.007;
                float tearY = 0.625 + tearWave;
                float topTear = smoothstep(tearY - aaY, tearY + aaY, fullUv.y);
                float bottomTear = 1.0 - smoothstep(tearY - aaY, tearY + aaY, fullUv.y);
                float pieceEnds = wholePiece * wholeEnds
                                + topPiece * topEnd * topTear
                                + bottomPiece * bottomEnd * bottomTear;
                float shape = sideMask * pieceEnds;

                // The complete source illustration is compressed into the printed body. The seal
                // bands repeat the nearest printed edge under their foil treatment, which keeps a
                // pack's palette continuous without inventing replacement art.
                float bodyY = saturate((fullUv.y - _SealFraction) /
                                       max(1.0 - 2.0 * _SealFraction, 0.001));
                float2 artUv = float2(localX, bodyY);
                float2 sourceUv = _SourceRect.xy + artUv * _SourceRect.zw;
                half4 art = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sourceUv);

                float bottomSeal = 1.0 - step(_SealFraction, fullUv.y);
                float topSeal = step(1.0 - _SealFraction, fullUv.y);
                float sealMask = saturate(bottomSeal + topSeal);

                // Pillow body: dark rolled sides, a gentle convex highlight in the middle, and
                // narrow folded flashes just inside each edge. It reads as flexible foil without
                // washing out the illustration.
                float centered = localX * 2.0 - 1.0;
                float edge = pow(saturate(abs(centered)), 2.4);
                float bodyLight = 1.04 - edge * _EdgeDarkening;
                float edgeFlash = exp2(-pow((localX - 0.105) * 31.0, 2.0))
                                + exp2(-pow((localX - 0.895) * 31.0, 2.0));

                // Broad diagonal reflection, static by design. A moving gloss at thumbnail scale
                // looks like screen glare; this should read as a material property of the pack.
                float diagonal = localX * 0.82 + fullUv.y * 0.42;
                float foilSweep = exp2(-pow((diagonal - 0.69) * 8.0, 2.0));
                half3 color = art.rgb * bodyLight;
                color += (edgeFlash * 0.10 + foilSweep * _FoilStrength) * art.a;

                // Machine crimp: repeated vertical compression ridges, alternating a highlight
                // and a shadow. The printed edge remains visible below the metallic lift, matching
                // real printed foil rather than drawing an unrelated silver cap.
                float ridgePhase = localX * _CrimpCount * TWO_PI;
                float ridge = sin(ridgePhase);
                float ridgeCrown = pow(saturate(ridge * 0.5 + 0.5), 5.0);
                float ridgeShade = pow(saturate(-ridge * 0.5 + 0.5), 4.0);
                float luminance = dot(art.rgb, half3(0.2126, 0.7152, 0.0722));
                half3 sealBase = lerp(art.rgb, luminance.xxx + 0.12, 0.25);
                sealBase += ridgeCrown * _CrimpStrength;
                sealBase *= 1.0 - ridgeShade * _CrimpStrength * 0.52;
                color = lerp(color, sealBase, sealMask);

                // A dark compressed trough with a fine hot edge makes the boundary look melted,
                // rather than merely painted as a different-coloured stripe.
                float seamDistance = min(abs(fullUv.y - _SealFraction),
                                         abs(fullUv.y - (1.0 - _SealFraction)));
                float seamTrough = 1.0 - smoothstep(0.002, 0.010, seamDistance);
                float seamGlint = 1.0 - smoothstep(0.010, 0.017, seamDistance);
                color *= 1.0 - seamTrough * 0.32;
                color += seamGlint * (1.0 - seamTrough) * 0.09;

                // A slim perimeter catch-light separates the wrapper from dark UI plates. The
                // torn pieces include that catch on the freshly exposed irregular edge as well.
                float perimeter = 1.0 - smoothstep(0.0, 0.022,
                    min(min(fullUv.x - left, right - fullUv.x),
                        min(fullUv.y - cut, 1.0 - cut - fullUv.y)));
                float tornEdge = (topPiece + bottomPiece)
                               * (1.0 - smoothstep(0.0, 0.012, abs(fullUv.y - tearY)));
                perimeter = saturate(perimeter + tornEdge);
                color += saturate(perimeter) * shape * 0.08;

                return half4(saturate(color), art.a * shape);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
