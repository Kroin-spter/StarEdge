Shader "Mine/Weathered Equipment"
{
    Properties
    {
        _BaseMap("Original color atlas", 2D) = "white" {}
        _BaseColor("Original tint", Color) = (1,1,1,1)
        _PartKind("0 Paint, 1 Track, 2 Bucket, 3 Hardware, 4 Hose", Float) = 0
        _Weathering("Weathering", Range(0,1)) = 0.4
        _DetailStrength("Surface detail", Range(0,1)) = 0.7
        _Cull("Cull", Float) = 2
        _Cutoff("Cutoff", Float) = 0.5
        [HideInInspector] _IsBody("Cab region", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" "UniversalMaterialType"="Lit" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        // All passes share one material layout so shadows/AO remain SRP-batchable.
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            float _PartKind, _Weathering, _DetailStrength, _Cull, _Cutoff;
            float4x4 _ObjectToLink;
            float _IsBody;
        CBUFFER_END
        ENDHLSL
        Pass
        {
            Name "EquipmentForward"
            Tags { "LightMode"="UniversalForwardOnly" }
            Cull [_Cull]
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_local_fragment _ _CLEARCOAT
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float3 positionLink : TEXCOORD3;
                float fog : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };
            float Hash(float3 p)
            {
                p = frac(p * .1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }
            float Noise(float3 p)
            {
                float3 i = floor(p), f = frac(p);
                f = f * f * (3 - 2 * f);
                return lerp(lerp(lerp(Hash(i), Hash(i + float3(1,0,0)), f.x),
                    lerp(Hash(i + float3(0,1,0)), Hash(i + float3(1,1,0)), f.x), f.y),
                    lerp(lerp(Hash(i + float3(0,0,1)), Hash(i + float3(1,0,1)), f.x),
                    lerp(Hash(i + float3(0,1,1)), Hash(i + 1), f.x), f.y), f.z);
            }
            Varyings Vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v,o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.positionLink = mul(_ObjectToLink, v.positionOS).xyz;
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.fog = ComputeFogFactor(p.positionCS.z);
                o.shadowCoord = GetShadowCoord(p);
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 original = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                float3 p = i.positionLink;
                float coarse = Noise(p * 3.7), fine = Noise(p * 70);
                float detailFade = 1 - smoothstep(.02, .09, length(fwidth(p)));
                float paint = _PartKind < .5 ? saturate((original.r - original.g) * 6) : 0;
                float track = _PartKind > .5 && _PartKind < 1.5 ? 1 : 0;
                float bucket = _PartKind > 1.5 && _PartKind < 2.5 ? 1 : 0;
                float hardware = _PartKind > 2.5 && _PartKind < 3.5 ? 1 : 0;
                float hose = _PartKind > 3.5 ? 1 : 0;
                float wear = bucket * smoothstep(.65, 1.25, p.z) * smoothstep(.25,.7,p.y);
                half3 steel = lerp(original, half3(.19,.21,.23), track * .35 + bucket * .65);
                half3 albedo = lerp(steel, original, paint);
                // Dark cab/engine panels are coated metal, not bare metallic-black.
                albedo = lerp(albedo,max(albedo,half3(.032,.038,.043)),_IsBody * (1-paint));
                albedo = lerp(albedo, half3(.36,.39,.41), wear * (.65 + fine * .25));
                albedo *= 1 + (fine - .5) * .09 * _DetailStrength * detailFade;
                // Stable link-space dirt avoids a texture that slides when a joint rotates.
                float lowerDust = 1 - smoothstep(.2, 1.1, p.y);
                float dust = _Weathering * (.09 + .4 * lowerDust + .18 * track) *
                    smoothstep(.15,.85,coarse) * (1 - wear * .75) * (1 - hardware) * (1 - hose);
                albedo = lerp(albedo, half3(.21,.145,.085), dust);
                float metallic = .06 + track * .34 + bucket * .44;
                metallic = lerp(metallic,.82,wear + hardware * (1-wear));
                metallic = lerp(metallic,0,hose);
                metallic *= 1 - dust;
                float smoothness = lerp(.24,.38,paint) + (fine-.5)*.09*_DetailStrength*detailFade;
                smoothness = lerp(smoothness,.5,wear);
                smoothness = lerp(smoothness,.57,hardware);
                smoothness = lerp(smoothness,.2,hose);
                smoothness *= 1 - dust * .5;
                // The original cab windows share the atlas with its frame. Keep the
                // bounded upper, forward cab region reflective while its border stays dark.
                float glass = _IsBody * (1-paint) * step(.9,p.y) * step(p.y,2.18) *
                    step(-.15,p.z) * step(p.z,1.6) * step(p.x,-.4);
                albedo = lerp(albedo,half3(.035,.075,.09),glass);
                metallic = lerp(metallic,.12,glass);
                smoothness = lerp(smoothness,.88,glass);
                float3 n = normalize(i.normalWS);
                float3 dx = ddx(i.positionWS), dy = ddy(i.positionWS);
                float3 r1 = cross(dy,n), r2 = cross(n,dx);
                float determinant = dot(dx,r1);
                float height = fine * .00035 * _DetailStrength * detailFade * (1-glass);
                if (abs(determinant) > 1e-10)
                    n = normalize(abs(determinant)*n - sign(determinant)*(ddx(height)*r1 + ddy(height)*r2));
                InputData input = (InputData)0;
                input.positionWS = i.positionWS;
                input.positionCS = i.positionCS;
                input.normalWS = n;
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
                input.shadowCoord = i.shadowCoord;
                input.fogCoord = i.fog;
                // Use URP's current ambient-probe path, matching its Lit pass.
                input.bakedGI = SampleSHPixel(half3(0,0,0),n);
                input.vertexLighting = VertexLighting(i.positionWS,n);
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                input.shadowMask = half4(1,1,1,1);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = albedo;
                surface.metallic = saturate(metallic);
                surface.smoothness = saturate(smoothness);
                surface.normalTS = half3(0,0,1);
                surface.occlusion = 1;
                surface.alpha = 1;
                surface.clearCoatMask = glass;
                surface.clearCoatSmoothness = .9;
                half4 color = UniversalFragmentPBR(input,surface);
                color.rgb = MixFog(color.rgb,i.fog);
                return color;
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection, _LightPosition;
            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            float4 ShadowVert(ShadowAttributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                float3 p = TransformObjectToWorld(v.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(v.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 direction = normalize(_LightPosition - p);
                #else
                    float3 direction = _LightDirection;
                #endif
                return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(p,n,direction)));
            }
            half4 ShadowFrag() : SV_TARGET { return 0; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            float4 DepthVert(DepthAttributes v) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(v);
                return TransformObjectToHClip(v.positionOS.xyz);
            }
            half DepthFrag(float4 positionCS : SV_POSITION) : SV_TARGET { return positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormalsOnly" }
            Cull [_Cull]
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalsVert
            #pragma fragment NormalsFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            struct NormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct NormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            NormalsVaryings NormalsVert(NormalsAttributes v)
            {
                NormalsVaryings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }
            half4 NormalsFrag(NormalsVaryings i) : SV_TARGET
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 packed = saturate(PackNormalOctQuadEncode(n)*.5+.5);
                    return half4(PackFloat2To888(packed),0);
                #else
                    return half4(n,0);
                #endif
            }
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
