Shader "Fleet/Unit Idle"
{
    Properties
    {
        _HasViewEffects ("View Specific Effects", Float) = 0
        _Nozzle0 ("Nozzle 0 XY Radii", Vector) = (0,0,0,0)
        _Nozzle1 ("Nozzle 1 XY Radii", Vector) = (0,0,0,0)
        _Nozzle2 ("Nozzle 2 XY Radii", Vector) = (0,0,0,0)
        _NozzleParts ("Nozzle Rig Indices", Vector) = (0,0,0,0)
        _NozzleColor ("Nozzle Color", Color) = (.08,.52,1,1)
        _GlowColor ("Lamp Color", Color) = (.1,.63,1,1)
        _MainTex ("Unit RGBA", 2D) = "white" {}
        _EffectMask ("Effect Mask", 2D) = "black" {}
        _Canvas ("Canvas Pixels", Vector) = (1536,1536,0,0)
        _UnitKind ("Unit Kind", Float) = 0
        _HoverPx ("Hover Pixels", Range(0,24)) = 10
        _LoopDuration ("Loop Seconds", Float) = 6
        _IdleTime ("Playback Seconds", Float) = 0
        _RigAtlas ("Moving Parts RGBA", 2D) = "black" {}
        _RigMaskAtlas ("Moving Parts Mask", 2D) = "black" {}
        _MechanicalAmount ("Mechanical Motion Amplitude", Range(0,1.4)) = 1
        _SourceOffset ("Source Offset Pixels", Vector) = (141,141,0,0)
        _Drill ("Drill XY Radius Height", Vector) = (0,0,0,0)
        _PartCount ("Moving Part Count", Float) = 1
        _Engine0 ("Engine 0 XY Length Width", Vector) = (0,0,0,0)
        _Engine1 ("Engine 1 XY Length Width", Vector) = (0,0,0,0)
        _Engine2 ("Engine 2 XY Length Width", Vector) = (0,0,0,0)
        _Direction0 ("Engine 0 Direction Phase", Vector) = (-1,0,0,0)
        _Direction1 ("Engine 1 Direction Phase", Vector) = (-1,0,1.7,0)
        _Direction2 ("Engine 2 Direction Phase", Vector) = (-1,0,3.4,0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One OneMinusSrcAlpha
        Cull Off ZWrite Off
        Pass
        {
            // Omit LightMode: Built-in uses its unlit default; URP uses SRPDefaultUnlit.
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _EffectMask, _RigAtlas, _RigMaskAtlas;
            float4 _Canvas, _Engine0, _Engine1, _Engine2, _Direction0, _Direction1, _Direction2;
            float _UnitKind, _HoverPx, _LoopDuration, _IdleTime;
            float4 _SourceOffset,_Drill,_PartSource[12],_PartAtlas[12],_PartInfo[12];
            float _PartCount,_MechanicalAmount;
            float4 _PartMotionA[12],_PartMotionB[12],_PartMotionC[12];
            float4 _Nozzle0,_Nozzle1,_Nozzle2,_NozzleParts,_NozzleColor,_GlowColor;
            float _HasViewEffects;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v)
            {
                v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o;
            }
            float4 ReadArt(float2 p)
            {
                float2 uv=p/_Canvas.xy;
                if(any(uv<0.0)||any(uv>1.0)) return float4(0,0,0,0);
                float4 c=tex2D(_MainTex,float2(uv.x,1.0-uv.y));
                #ifndef UNITY_COLORSPACE_GAMMA
                c.rgb=LinearToGammaSpace(c.rgb);
                #endif
                return c;
            }
            float4 ReadMask(float2 p)
            {
                float2 uv=p/_Canvas.xy;
                if(any(uv<0.0)||any(uv>1.0)) return float4(0,0,0,0);
                return tex2D(_EffectMask,float2(uv.x,1.0-uv.y));
            }
            float4 ReadRigAtlas(float2 p)
            {
                float2 uv=p/2048.0;
                float4 c=tex2Dlod(_RigAtlas,float4(uv.x,1.0-uv.y,0,0));
                #ifndef UNITY_COLORSPACE_GAMMA
                c.rgb=LinearToGammaSpace(c.rgb);
                #endif
                return c;
            }
            float4 ReadRigMaskAtlas(float2 p)
            {
                float2 uv=p/2048.0;
                return tex2Dlod(_RigMaskAtlas,float4(uv.x,1.0-uv.y,0,0));
            }
            #include "FleetIdleRig.hlsl"
            #include "FleetIdleCommon.hlsl"
            float4 frag(v2f i) : SV_Target
            {
                float4 c=IdlePixel(float2(i.uv.x,1.0-i.uv.y),_IdleTime);
                #ifndef UNITY_COLORSPACE_GAMMA
                c.rgb=GammaToLinearSpace(c.rgb/max(c.a,.0001))*c.a;
                #endif
                return c;
            }
            ENDCG
        }
    }
}
