Shader "DbtArt/Dargon Body" {
	Properties {
		[Header(Base)] _MainTex ("基础纹理", 2D) = "white" {}
		_BumpMap ("法线纹理", 2D) = "bump" {}
		_Color ("颜色", Vector) = (1,1,1,1)
		_DarkCol ("暗部颜色", Vector) = (0,0,0,1)
		_ColorScale ("亮度", Range(0, 3)) = 0.35
		_BumpScale ("凹凸强度", Range(0, 3)) = 1
		[Header(Dark)] _ShadowCol ("毛线黑边", Vector) = (0,0,0,1)
		_DrakRange ("暗部范围", Range(0, 1)) = 0
		_DrakSoft ("暗部羽化", Range(0, 1)) = 1
		[Header(Clip)] [Toggle(_ENABLE_CLIP)] _EnableClip ("启用裁剪", Float) = 0
		_Clip ("裁剪Z", Range(0, 100)) = 0
		[Header(VFX)] [Toggle] _EnableBoom ("爆炸效果", Float) = 0
		_BoomMap ("爆炸纹理", 2D) = "white" {}
		_BoomCol01 ("爆炸颜色01", Vector) = (1,1,1,1)
		_BoomCol02 ("爆炸颜色02", Vector) = (1,1,1,1)
		[Header(Infect)] _Color2 ("侵染目标色", Vector) = (1,1,1,1)
		_DarkCol2 ("侵染暗部", Vector) = (0,0,0,1)
		_ShadowCol2 ("侵染黑边", Vector) = (0,0,0,1)
		_InfectProgress ("侵染进度", Range(0, 1)) = 0
		_InfectCenter ("侵染中心(对象空间)", Vector) = (0,1,0,0)
		_InfectRadius ("侵染最大半径", Float) = 2.5
		_InfectSoft ("侵染边缘羽化", Float) = 0.2
	}
	//DummyShaderTextExporter
	SubShader{
		Tags { "RenderType"="Opaque" }
		LOD 200

		Pass
		{
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			float4x4 unity_ObjectToWorld;
			float4x4 unity_MatrixVP;
			float4 _MainTex_ST;

			struct Vertex_Stage_Input
			{
				float4 pos : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct Vertex_Stage_Output
			{
				float2 uv : TEXCOORD0;
				float4 pos : SV_POSITION;
			};

			Vertex_Stage_Output vert(Vertex_Stage_Input input)
			{
				Vertex_Stage_Output output;
				output.uv = (input.uv.xy * _MainTex_ST.xy) + _MainTex_ST.zw;
				output.pos = mul(unity_MatrixVP, mul(unity_ObjectToWorld, input.pos));
				return output;
			}

			Texture2D<float4> _MainTex;
			SamplerState sampler_MainTex;
			float4 _Color;

			struct Fragment_Stage_Input
			{
				float2 uv : TEXCOORD0;
			};

			float4 frag(Fragment_Stage_Input input) : SV_TARGET
			{
				return _MainTex.Sample(sampler_MainTex, input.uv.xy) * _Color;
			}

			ENDHLSL
		}
	}
}