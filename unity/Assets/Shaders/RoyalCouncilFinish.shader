Shader "Kingdom/Royal Council Finish"
{
    Properties
    {
        _BaseColor ("Colour", Color) = (0.8,0.7,0.5,1)
        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.3
        _Grain ("Fine grain", Range(0,0.5)) = 0.08
        _GrainScale ("Grain scale", Float) = 200
        _EdgeShade ("Card edge seams", Range(0,1)) = 0
        _TurnedRings ("Engraved cap rings", Range(0,1)) = 0
        _CapRadius ("Cap radius", Float) = 0.08364
        [HideInInspector] _BaseMap ("Base map", 2D) = "white" {}
        [HideInInspector] _Cutoff ("Cutoff", Float) = 0.5
        [HideInInspector] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 edge : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionOS : TEXCOORD2;
                float2 edge : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Metallic, _Smoothness, _Grain, _EdgeShade, _TurnedRings;
                float _GrainScale, _CapRadius;
            CBUFFER_END

            float Grain(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }
            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input,o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.positionOS = input.positionOS.xyz;
                o.edge = input.edge;
                return o;
            }
            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float3 grainPosition = i.positionOS * _GrainScale;
                half grain = Grain(floor(grainPosition));
                float3 footprint = fwidth(grainPosition);
                half grainStrength = _Grain * rsqrt(1 + dot(footprint,footprint));
                half3 colour = _BaseColor.rgb * (1 + (grain - 0.5) * grainStrength);
                // UV1 travels with each individual card through CombineMeshes. Its X marks
                // thin edges and Y runs across that card's thickness, regardless of rack pose.
                float aa = max(0.025, fwidth(i.edge.y) * 0.5);
                half centre = smoothstep(0.07-aa,0.07+aa,i.edge.y)
                    * (1-smoothstep(0.93-aa,0.93+aa,i.edge.y));
                colour *= 1 - i.edge.x * _EdgeShade * (1-centre);

                float radius = length(i.positionOS.xz) / max(_CapRadius,0.001);
                float ringAA = max(fwidth(radius),0.002);
                float ringDistance = min(abs(radius-0.69),abs(radius-0.82));
                half rings = 1-smoothstep(0.005,0.005+ringAA,ringDistance);
                colour *= 1 - rings * _TurnedRings * 0.65;
                colour *= 1 + _TurnedRings * smoothstep(0.91,0.99,radius) * 0.14;

                InputData lighting = (InputData)0;
                lighting.positionWS = i.positionWS;
                lighting.normalWS = normalize(i.normalWS);
                lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                lighting.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                lighting.bakedGI = SampleSH(lighting.normalWS);
                lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                lighting.shadowMask = half4(1,1,1,1);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = colour;
                surface.metallic = _Metallic;
                surface.specular = half3(0.2,0.2,0.2);
                surface.smoothness = saturate(_Smoothness + (grain-0.5)*grainStrength*0.2);
                surface.normalTS = half3(0,0,1);
                surface.occlusion = 1;
                surface.alpha = 1;
                return UniversalFragmentPBR(lighting,surface);
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }
}
