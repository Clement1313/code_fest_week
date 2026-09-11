Shader "UI/Chabway Surfer/White Logo Outline"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _OutlineColor ("Outline Color", Color) = (1,1,1,1)
        _OutlineUV ("Outline UV", Vector) = (0.01,0.01,0,0)
        _OutlinePixels ("Outline Pixels", Vector) = (2,2,0,0)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _MainTex;
            fixed4 _OutlineColor;
            float4 _OutlineUV;
            float4 _OutlinePixels;

            v2f vert(appdata_t input)
            {
                v2f output;
                float2 direction = sign(input.texcoord - 0.5);
                input.vertex.xy += direction * _OutlinePixels.xy;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.texcoord = input.texcoord + direction * _OutlineUV.xy;
                output.color = input.color;
                return output;
            }

            fixed IsInsideSprite(float2 uv)
            {
                return step(0.0, uv.x) * step(uv.x, 1.0)
                    * step(0.0, uv.y) * step(uv.y, 1.0);
            }

            fixed SampleSpriteAlpha(float2 uv)
            {
                return tex2D(_MainTex, saturate(uv)).a * IsInsideSprite(uv);
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 offset = _OutlineUV.xy;
                fixed insideSprite = IsInsideSprite(input.texcoord);
                fixed4 source = tex2D(_MainTex, saturate(input.texcoord)) * input.color * insideSprite;

                fixed maxAlpha = 0.0;
                fixed sampleAlpha = SampleSpriteAlpha(input.texcoord + float2( offset.x, 0.0));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2(-offset.x, 0.0));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2(0.0,  offset.y));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2(0.0, -offset.y));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2( offset.x,  offset.y));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2(-offset.x,  offset.y));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2( offset.x, -offset.y));
                maxAlpha = max(maxAlpha, sampleAlpha);
                sampleAlpha = SampleSpriteAlpha(input.texcoord + float2(-offset.x, -offset.y));
                maxAlpha = max(maxAlpha, sampleAlpha);

                // Keep the original logo untouched and draw only outside its alpha.
                // The thresholds prevent isolated semi-transparent pixels from creating spikes.
                fixed solidNeighbour = smoothstep(0.35, 0.8, maxAlpha);
                fixed transparentCentre = 1.0 - smoothstep(0.05, 0.45, source.a);
                fixed outlineAlpha = solidNeighbour * transparentCentre * _OutlineColor.a * input.color.a;
                fixed finalAlpha = source.a + outlineAlpha * (1.0 - source.a);
                fixed3 premultipliedColor = source.rgb * source.a
                    + _OutlineColor.rgb * outlineAlpha * (1.0 - source.a);
                fixed3 finalColor = premultipliedColor / max(finalAlpha, 0.0001);

                return fixed4(finalColor, finalAlpha);
            }
            ENDCG
        }
    }
}
