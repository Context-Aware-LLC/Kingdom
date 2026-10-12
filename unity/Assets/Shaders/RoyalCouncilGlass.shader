Shader "Kingdom/Royal Council Glass"
{
    Properties
    {
        _BaseColor ("Tint", Color) = (0.6,0.85,1,0.18)
        _EmissionColor ("Glow", Color) = (0,0,0,0)
        _Rim ("Rim", Range(0,1)) = 0.45
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS:SV_POSITION; float3 normalWS:TEXCOORD0; float3 viewWS:TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
                half _Rim;
            CBUFFER_END
            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input,o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p=GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS=p.positionCS;
                o.normalWS=TransformObjectToWorldNormal(input.normalOS);
                o.viewWS=GetWorldSpaceViewDir(p.positionWS);
                return o;
            }
            half4 frag(Varyings i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                half3 n=normalize(i.normalWS), v=normalize(i.viewWS);
                n *= dot(n,v) < 0 ? -1 : 1;
                half fresnel=pow(1-saturate(dot(n,v)),3);
                // A broad window reflection and a small highlight read at phone scale without
                // a scene-colour copy, refraction pass, or a separate reflection camera.
                half3 light=normalize(half3(-0.4,0.65,-0.8));
                half spec=pow(saturate(dot(n,normalize(v+light))),52);
                half sheen=pow(saturate(dot(reflect(-v,n),light)),10)*0.22;
                half3 color=_BaseColor.rgb+_EmissionColor.rgb+half3(0.7,0.85,1)*fresnel*_Rim+spec+sheen;
                return half4(color,saturate(_BaseColor.a+fresnel*_Rim+spec*0.6));
            }
            ENDHLSL
        }
    }
}
