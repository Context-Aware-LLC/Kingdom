Shader "Kingdom/Royal Council Glass"
{
    Properties
    {
        _BaseColor ("Tint", Color) = (0.6,0.85,1,0.18)
        _EmissionColor ("Glow", Color) = (0,0,0,0)
        _Rim ("Rim", Range(0,1)) = 0.45
        _CloudColor ("Cloud colour", Color) = (0.48,0.82,1,1)
        _Cloudiness ("Cloudiness", Range(0,1)) = 0
        _CloudSpeed ("Cloud drift", Float) = 0.06
        _CloudPhase ("Cloud phase", Float) = 0
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
            struct Varyings { float4 positionCS:SV_POSITION; float3 normalWS:TEXCOORD0; float3 viewWS:TEXCOORD1; float3 positionOS:TEXCOORD2; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EmissionColor;
                half _Rim;
                half4 _CloudColor;
                half _Cloudiness;
                float _CloudSpeed, _CloudPhase;
            CBUFFER_END
            float Hash(float3 p)
            {
                p=frac(p*0.1031);
                p+=dot(p,p.yzx+33.33);
                return frac((p.x+p.y)*p.z);
            }
            float CloudNoise(float3 p)
            {
                float3 cell=floor(p), f=frac(p);
                f=f*f*(3-2*f);
                return lerp(lerp(lerp(Hash(cell),Hash(cell+float3(1,0,0)),f.x),
                    lerp(Hash(cell+float3(0,1,0)),Hash(cell+float3(1,1,0)),f.x),f.y),
                    lerp(lerp(Hash(cell+float3(0,0,1)),Hash(cell+float3(1,0,1)),f.x),
                    lerp(Hash(cell+float3(0,1,1)),Hash(cell+float3(1,1,1)),f.x),f.y),f.z);
            }
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
                o.positionOS=input.positionOS.xyz;
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
                half alpha=_BaseColor.a+fresnel*_Rim+spec*0.6;
                if (_Cloudiness>0.001)
                {
                    // Object-space mist needs no UVs and remains inside the small glass core.
                    // A slow twist produces cloudy depth without a refraction camera or VFX objects.
                    float3 q=normalize(i.positionOS)*2.5;
                    float drift=_Time.y*_CloudSpeed+_CloudPhase;
                    float angle=q.z*0.7+drift;
                    float s=sin(angle),c=cos(angle);
                    q.xy=float2(q.x*c-q.y*s,q.x*s+q.y*c);
                    float broad=CloudNoise(q+float3(0,drift*0.3,_CloudPhase));
                    float cloud=CloudNoise(q*1.8+broad*1.4);
                    half mist=smoothstep(0.32,0.76,broad*0.6+cloud*0.4)*_Cloudiness;
                    color=lerp(color,_CloudColor.rgb+_EmissionColor.rgb,mist*0.8);
                    alpha+=mist*0.32;
                }
                return half4(color,saturate(alpha));
            }
            ENDHLSL
        }
    }
}
