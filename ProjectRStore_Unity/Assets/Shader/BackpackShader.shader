Shader "BackpackShader" {
	Properties {
		[Header(Test)] [Space(5)] _TestSlider ("Test Slider", Range(0, 1)) = 0.5
		_TestValue ("Test Value", Float) = 1
		_SpookinessLocal ("Spookiness Local (Additional)", Range(-1, 1)) = 0
		[Header(Surface)] [Space(5)] [KeywordEnum(UV, Triplanar)] _SurfaceTextureMode ("Surface Texture Mode", Float) = 0
		[Toggle] _UseVertexColor ("Use Vertex Color", Float) = 0
		_BaseColor ("Base color", Vector) = (1,1,1,1)
		_BaseMap ("Texture", 2D) = "white" {}
		[HideInInspector] _Cutoff ("Alpha Clipping", Range(0, 1)) = 0.5
		[Header(UVScroll)] [Space(5)] [Toggle(USE_UVSCROLL)] _UseUVScroll ("Use UV Scroll", Float) = 0
		_UVScrollSpeed ("UV Scroll Speed", Vector) = (1,0,0,0)
		[Header(Emission)] [Space(5)] [HDR] _EmissionColor ("Emission Color", Vector) = (0,0,0,1)
		_EmissionMap ("Emission Map", 2D) = "white" {}
		[Toggle] _InvertEmissionFresnel ("Invert Fresnel Emission", Range(0, 1)) = 0
		_UseEmissionFresnel ("Use Fresnel Emission", Range(0, 1)) = 0
		_EmissionFresnelPower ("Emission Fresnel Power", Float) = 1
		[Header(Normal)] [Space(5)] [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
		_BumpScale ("Scale", Float) = 2
		[Normal] _DetailNormalMap ("Normal Map (Detail)", 2D) = "bump" {}
		_DetailNormalMapScale ("Scale (Detail)", Float) = 3
		[Header(AO)] [Space(5)] _OcclusionMap ("Ambient Occlusion Map", 2D) = "white" {}
		_OcclusionStrength ("Ambient Occlusion Intensity", Range(0, 5)) = 1
		[Header(Posterize)] [Space(5)] _PosterizeEdgeBright ("Posterize Edge Bright", Range(0, 1)) = 0.3
		_PosterizeEdgeDark ("Posterize Edge Dark", Range(0, 1)) = 0.05
		_PosterizeBrightness ("Posterize Brightness", Range(0, 1)) = 0.7
		[Header(Outline)] [Space(5)] [Toggle(USE_OUTLINE_FLAT_SURFACE_REMOVAL)] _UseOutlineFlatSurfaceRemoval ("Use Outline Flat Surface Removal", Float) = 0
		_OutlineColor ("Outline Color", Vector) = (0,0,0,1)
		_OutlineWidth ("Outline Width", Float) = 1
		_OutlineWidthMax ("Outline Width Max", Float) = 2
		_OutlineThreshold ("Outline Threshold", Float) = 0.0035
		[Header(CinematicLighting)] [Space(5)] [Toggle] _UseCinematicLighting ("Use Cinematic Lighting", Range(0, 1)) = 1
		_FrontSpotIntensity ("Front Spot Intensity", Range(0, 1)) = 0.35
		_DistanceToDecrease ("Distance To Decrease", Float) = 5
		[Header(Highlight)] [Space(5)] _HighlightIntensity ("Highlight Intensity", Range(0, 1)) = 1
		_HighlightRange ("Highlight Range", Range(0, 1)) = 0.5
		_HighlightDir ("Highlight Direction", Vector) = (-7,3,-5,1)
		[Header(Hatching)] [Space(5)] [KeywordEnum(Triplanar_OS, Triplanar_WS, ScreenSpace)] _TextureMode ("Texture Mode", Float) = 1
		_HatchingTex2 ("Hatching Texture (Outdoors)", 2D) = "white" {}
		_HatchingTex ("Hatching Texture (Caves)", 2D) = "white" {}
		_HatchingTexTile ("Hatching Texture Tile", Float) = 1
		[Header(Rendering)] [Space(5)] [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
		[Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
		[Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
		[Header(Blending)] [Space(5)] [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Blend mode Source", Float) = 1
		[Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Blend mode Destination", Float) = 0
	}
	//DummyShaderTextExporter
	SubShader{
		Tags { "RenderType" = "Opaque" }
		LOD 200

		Pass
		{
			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			float4x4 unity_ObjectToWorld;
			float4x4 unity_MatrixVP;

			struct Vertex_Stage_Input
			{
				float4 pos : POSITION;
			};

			struct Vertex_Stage_Output
			{
				float4 pos : SV_POSITION;
			};

			Vertex_Stage_Output vert(Vertex_Stage_Input input)
			{
				Vertex_Stage_Output output;
				output.pos = mul(unity_MatrixVP, mul(unity_ObjectToWorld, input.pos));
				return output;
			}

			float4 frag(Vertex_Stage_Output input) : SV_TARGET
			{
				return float4(1.0, 1.0, 1.0, 1.0); // RGBA
			}

			ENDHLSL
		}
	}
}