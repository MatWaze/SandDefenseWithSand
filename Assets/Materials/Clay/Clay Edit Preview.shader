Shader "Sand Defense/Clay Edit Preview"
{
    Properties
    {
        [HideInInspector] _ZTest ("Depth Test", Float) = 8
        [HideInInspector] _Cull ("Cull", Float) = 0
        [HideInInspector] _SolidFill ("Solid Fill", Float) = 0
        [HideInInspector] _FillColor ("Fill Color", Color) = (1, 0, 0, 1)
        [HideInInspector] _Dashed ("Dashed", Float) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        Cull [_Cull]
        ZWrite Off
        ZTest [_ZTest]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            half _SolidFill;
            half4 _FillColor;
            half _Dashed;
            float4x4 _ContactWorldToBox;
            float3 _ContactBoxScale;
            float _ContactFeather;

            struct Varyings
            {
                float4 position : SV_POSITION;
                half4 color : COLOR;
                float3 uv : TEXCOORD0;
                half faceShade : TEXCOORD1;
                float3 contactBoxPosition : TEXCOORD2;
            };

            Varyings Vert(float4 position : POSITION, half4 color : COLOR, float3 uv : TEXCOORD0, half3 normal : NORMAL)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(position);
                output.color = color;
                output.uv = uv;
                output.faceShade = dot(normal * normal, half3(0.85h, 1.0h, 0.72h));
                output.contactBoxPosition = 0.0;
                if (_SolidFill > 0.5h)
                {
                    output.contactBoxPosition = mul(_ContactWorldToBox, mul(unity_ObjectToWorld, position)).xyz;

                    // Give nearly coplanar clay priority without moving the preview's screen position.
                    float3 viewPosition = UnityObjectToViewPos(position);
                    viewPosition.z -= _ContactFeather * 0.3;
                    float4 farther = mul(UNITY_MATRIX_P, float4(viewPosition, 1.0));
                    if (farther.w > 0.0001)
                        output.position.z = farther.z * output.position.w / farther.w;
                }
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                if (_SolidFill > 0.5h)
                {
                    float3 outsideBox = (abs(input.contactBoxPosition) - 0.5) * _ContactBoxScale;
                    float clearance = length(max(outsideBox, 0.0));
                    half coverage = smoothstep(_ContactFeather * 0.4, _ContactFeather, clearance);
                    return half4(_FillColor.rgb * input.faceShade, coverage);
                }

                half edge = min(input.uv.y, 1.0h - input.uv.y);
                half stripe = smoothstep(0.12h, 0.28h, edge);
                half3 dark = half3(0.022h, 0.012h, 0.009h);
                half opacity = smoothstep(0.0h, 0.10h, edge);
                if (_Dashed + input.uv.z > 0.5h)
                    opacity *= (1.0h - smoothstep(0.54h, 0.64h, frac(input.uv.x))) * 0.85h;
                return half4(lerp(dark, input.color.rgb, stripe), opacity);
            }
            ENDCG
        }
    }
}
