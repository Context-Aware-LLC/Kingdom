// Painted Aegis shell. Preserve the PNG's alpha, including its transparent centre;
// the binder animates tint alpha and intensity independently for gain/break.
Shader "Kingdom/AegisDome"
{
    Properties
    {
        _BaseMap ("Golden Dome", 2D) = "black" {}
        // Not [HDR]: GammaTuned.hlsl says why.
        _AegisColor ("Aegis Tint", Color) = (1, 1, 1, 0.9)
        _AegisIntensity ("Aegis Intensity", Float) = 1.1
        _PulseAmount ("Pulse Amount", Range(0, 1)) = 0.12
        _PulseSpeed ("Pulse Speed", Float) = 0.8
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
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/Functions/GammaTuned.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _AegisColor;
                float _AegisIntensity;
                float _PulseAmount;
                float _PulseSpeed;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 dome = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                float pulse = 1.0 + _PulseAmount * sin(_Time.y * _PulseSpeed * 6.2831853);
                // Intensity and pulse were tuned by eye in Gamma (GammaTuned.hlsl).
                half3 lit = TunedIn(dome.rgb) * TunedIn(_AegisColor.rgb) * _AegisIntensity * pulse;
                // The painted shell is a glow over the card, so its alpha takes the glow curve.
                // The curve keeps 0 at 0, so the hollow centre stays hollow.
                dome.a = TunedGlowAlpha(dome.a);
                return half4(TunedOut(lit), dome.a * _AegisColor.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
