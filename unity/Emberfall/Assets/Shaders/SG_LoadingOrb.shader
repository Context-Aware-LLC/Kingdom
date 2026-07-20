// SG_LoadingOrb — the loading-screen mana orb (the "heartbeat" indicator).
//
// Renders Art/Loading/Orb_StormGlass.png with live interior motion so a frozen
// orb reads as a hang (techart/loading-screen-plate.md §4 orb well, §5 anchoring).
// Authored as hand-HLSL per the SG_ManaFill precedent: the motion is UV-space
// resampling of the baked art — differential rotation (the storm core spins
// faster than the glass rim, which stays fixed), a small value-noise churn for
// the cloudy boil, and a heartbeat emission pulse. Every frame samples the
// original texture, so the art never accumulates smear.
//
// The orb art is deliberately constant across expansion plates (Sam, 2026-07-19):
// plates are hot-swappable skins, the orb is the brand mark. No recolor params.
Shader "Emberfall/SG_LoadingOrb"
{
    Properties
    {
        _MainTex     ("Orb Texture", 2D) = "black" {}
        _SwirlSpeed  ("Swirl Cycle Speed (Hz)", Range(0, 1)) = 0.12
        _SwirlAmount ("Max Twist (radians at core)", Range(0, 2)) = 0.6
        _SwirlFalloff("Swirl Falloff (core->rim)", Range(0.5, 4)) = 1.6
        _ChurnStrength ("Churn Strength (UV)", Range(0, 0.05)) = 0.012
        _ChurnScale  ("Churn Scale", Range(1, 16)) = 6
        _PulseRate   ("Pulse Rate (Hz)", Range(0.1, 3)) = 0.8
        _PulseAmp    ("Pulse Amplitude", Range(0, 1)) = 0.35
        _RimBoost    ("Rim Boost", Range(0, 2)) = 0.5
        _Progress    ("Load Progress", Range(0, 1)) = 1
        _Intensity   ("Overall Intensity", Range(0, 3)) = 1
        _Fade        ("Overlay Fade", Range(0, 1)) = 1
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
                float  _SwirlSpeed;
                float  _SwirlAmount;
                float  _SwirlFalloff;
                float  _ChurnStrength;
                float  _ChurnScale;
                float  _PulseRate;
                float  _PulseAmp;
                float  _RimBoost;
                float  _Progress;
                float  _Intensity;
                float  _Fade;
            CBUFFER_END

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 2-octave value noise, cheap enough for a single full-screen-ish quad.
            float vnoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = hash21(i);
                float b = hash21(i + float2(1, 0));
                float c = hash21(i + float2(0, 1));
                float d = hash21(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float  t     = _Time.y;
                float  pulse = 0.5 + 0.5 * sin(TWO_PI * _PulseRate * t);
                pulse = pulse * pulse * (3.0 - 2.0 * pulse); // eased heartbeat

                // Centered disc space. r=0 core, r=1 glass edge.
                float2 c = IN.uv * 2.0 - 1.0;
                float  r = length(c);

                // Breath: the whole interior swells ~1.5% with the pulse.
                c *= 1.0 - 0.015 * _PulseAmp * (pulse - 0.5) * 2.0;

                // Differential swirl, flowmap-phase style: two layers, each with a
                // BOUNDED twist (max _SwirlAmount at the core, zero at the rim) that
                // resets only while its crossfade weight is zero. Continuous motion,
                // shear never accumulates — unbounded `t * speed` twist degrades into
                // moiré rings within minutes (verified the hard way, 2026-07-19).
                float core = pow(saturate(1.0 - r), _SwirlFalloff);
                float ph = t * _SwirlSpeed;
                float p1 = frac(ph);
                float p2 = frac(ph + 0.5);
                float w1 = 1.0 - abs(2.0 * p1 - 1.0); // peaks mid-phase, 0 at reset
                float a1 = (p1 - 0.5) * _SwirlAmount * core;
                float a2 = (p2 - 0.5) * _SwirlAmount * core;

                float s1, k1, s2, k2;
                sincos(a1, s1, k1);
                sincos(a2, s2, k2);
                float2 rc1 = float2(k1 * c.x - s1 * c.y, s1 * c.x + k1 * c.y);
                float2 rc2 = float2(k2 * c.x - s2 * c.y, s2 * c.x + k2 * c.y);

                // Cloudy churn: low-amplitude noise wobble, interior only, bounded.
                float2 np = c * _ChurnScale + t * 0.07;
                float2 wob = float2(vnoise(np) - 0.5, vnoise(np + 17.17) - 0.5);
                float2 churn = wob * (_ChurnStrength * 2.0) * core;

                float2 uv1 = (rc1 + churn) * 0.5 + 0.5;
                float2 uv2 = (rc2 + churn) * 0.5 + 0.5;
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv1) * w1
                          + SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv2) * (1.0 - w1);

                // Heartbeat emission + rim answer-glow.
                float brightness = _Intensity * (1.0 - 0.5 * _PulseAmp + _PulseAmp * pulse);
                float rimMask = smoothstep(0.78, 0.96, r) * step(r, 1.02);
                half3 col = tex.rgb * brightness;
                col += tex.rgb * rimMask * _RimBoost * pulse;

                // Dimmer while loading starts, full presence as progress completes.
                col *= lerp(0.55, 1.0, saturate(_Progress));

                return half4(col, tex.a * _Fade);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
