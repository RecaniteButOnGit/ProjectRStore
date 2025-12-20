Shader "WalkieTalkieShader" {
	Properties {
		[Header(Test)] [Space(5)] _TestSlider ("Test Slider", Range(0, 1)) = 0.5
		_TestValue ("Test Value", Float) = 1
		[Header(Surface)] [Space(5)] _BaseColor ("Base color", Vector) = (1,1,1,1)
		_BaseMap ("Texture", 2D) = "white" {}
		[Header(Emission)] [Space(5)] [HDR] _EmissionColor ("Emission Color", Vector) = (0,0,0,1)
		_EmissionMap ("Screen Texture", 2D) = "black" {}
		[Header(Screen)] [Space(5)] _SliderTalkOn ("Talk On", Range(0, 1)) = 0
		_BatteryLevel ("Battery Level", Range(0, 1)) = 1
		_HPLevel ("HP Level", Range(0, 1)) = 1
		_SignalStrength ("Signal Strength", Range(0, 1)) = 1
		_SignalAnimSpeed ("Signal Animation Speed", Float) = 2
		_SignalAnimFreq ("Signal Animation Frequency", Float) = 12
		[Header(Normal)] [Space(5)] [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
		_BumpScale ("Scale", Float) = 2
		[Header(Outline)] [Space(5)] [Toggle(USE_OUTLINE)] _UseOutline ("Use Outline", Float) = 1
		_OutlineColor ("Outline Color", Vector) = (0,0,0,1)
		_OutlineWidth ("Outline Width", Float) = 2
		_OutlineWidthMax ("Outline Width Max (Pixel)", Float) = 16
		_OutlineThreshold ("Outline Threshold", Range(0.0001, 0.002)) = 0.0015
		[Header(Ligne claire)] [Space(5)] [KeywordEnum(UV1, UV2, Triplanar, ScreenSpace)] _TextureMode ("Texture Mode", Float) = 2
		[KeywordEnum(OS, WS)] _TriplanarSpace ("Triplanar Space", Float) = 0
		_LineTex ("Line Texture", 2D) = "white" {}
		_LineTexTile ("Line Texture Tile", Float) = 4
		[Header(Rendering)] [Space(5)] [Enum(UnityEngine.Rendering.CullMode)] _Culling ("Cull Mode", Float) = 2
		[Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
		[Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
		[Header(Blending)] [Space(5)] [Enum(UnityEngine.Rendering.BlendMode)] _BlendSrc ("Blend mode Source", Float) = 1
		[Enum(UnityEngine.Rendering.BlendMode)] _BlendDst ("Blend mode Destination", Float) = 0
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