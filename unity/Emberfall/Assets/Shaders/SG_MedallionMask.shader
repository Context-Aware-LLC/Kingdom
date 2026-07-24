// SG_MedallionMask — circular alpha for a versus-screen hero medallion.
//
// The versus plate (techart/versus-screen.md §4.3) paints the medallion FRAME into
// the landscape, leaving a circular aperture at Ø1150. The hero art itself is a
// rectangular 1024² portrait; dropped on the plate as a quad its corners would cover
// the painted frame. This unlit shader masks the portrait to a feathered disc that
// sits inside the aperture, so the painted ring reads as the frame around it.
//
// Deliberately dumb: no motion, no lighting. The orb is the live element on this
// screen (SG_LoadingOrb); the medallions are still. _Fade rides the overlay's
// reveal/dismiss alongside the plate and orb.
Shader "Emberfall/SG_MedallionMask"
{
    Properties
    {
        _MainTex ("Hero Portrait", 2D) = "black" {}
        _Radius  ("Disc Radius (uv)", Range(0.1, 0.5)) = 0.5
        _Feather ("Edge Feather (uv)", Range(0.0, 0.1)) = 0.012
        _Fade    ("Overlay Fade", Range(0, 1)) = 1
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
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float  _Radius;
                float  _Feather;
                float  _Fade;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 rgb = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).rgb;

                // Distance from the quad centre in uv space; solid inside _Radius,
                // fading to zero across _Feather.
                float r = length(IN.uv - 0.5);
                float a = 1.0 - smoothstep(_Radius - _Feather, _Radius, r);

                return half4(rgb, a * _Fade);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
