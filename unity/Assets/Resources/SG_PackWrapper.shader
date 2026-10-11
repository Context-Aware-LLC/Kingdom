// SG_PackWrapper — cuts a complete authored 2:3 wrapper into complementary torn pieces.
//
// This shader is intentionally STATIC. PackWrapperBaker renders it once into a cached texture for
// UI Toolkit, whose background-image API cannot accept a custom Material. The source already has
// its foil, seals and transparent silhouette; sampling it unchanged keeps the tear matched to the
// intact sprite. Motion belongs to the pack-opening animation around these pieces.
Shader "Kingdom/SG_PackWrapper"
{
    Properties
    {
        _MainTex          ("Pack Art", 2D) = "white" {}
        _SourceRect       ("Source Rect", Vector) = (0, 0, 1, 1)
        [HideInInspector] _PieceMode ("Piece Mode", Float) = 0
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

                // Each piece carries only its slice of the original 2:3 wrapper. The overlapping
                // crop bounds match the tear-half geometry; one shared mask separates the art.
                float topPiece = step(0.5, _PieceMode) * (1.0 - step(1.5, _PieceMode));
                float bottomPiece = step(1.5, _PieceMode);
                float wholePiece = 1.0 - topPiece - bottomPiece;
                float fullY = uv.y;
                fullY = lerp(fullY, lerp(7.0 / 12.0, 1.0, uv.y), topPiece);
                fullY = lerp(fullY, lerp(0.0, 2.0 / 3.0, uv.y), bottomPiece);
                float2 fullUv = float2(uv.x, fullY);

                float localX = saturate((uv.x - 0.045) / 0.91);
                float aaY = max(fwidth(fullUv.y) * 1.25, 0.001);

                // One full-space tear line drives both masks, so the edges are complementary even
                // though the source crops overlap. Two frequencies keep it torn, not scalloped.
                float tearWave = sin(localX * 17.0 * TWO_PI) * 0.010
                               + sin((localX * 7.0 + 0.23) * TWO_PI) * 0.007;
                float tearY = 0.625 + tearWave;
                float topTear = smoothstep(tearY - aaY, tearY + aaY, fullUv.y);
                float bottomTear = 1.0 - topTear;
                float shape = wholePiece + topPiece * topTear + bottomPiece * bottomTear;

                // Preserve the source UVs, colors and outer alpha exactly. The authored image is
                // already a finished wrapper, so adding seals or squeezing its body would make the
                // art jump when the intact sprite is replaced by these pieces.
                float2 sourceUv = _SourceRect.xy + fullUv * _SourceRect.zw;
                half4 art = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, sourceUv);
                return half4(art.rgb, art.a * shape);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
