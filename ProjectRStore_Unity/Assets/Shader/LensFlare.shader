Shader "LensFlare" {
	Properties {
		[Header(Color)] [Space(5)] _BaseColor ("Base color", Vector) = (1,1,1,1)
		_BaseMap ("Texture", 2D) = "white" {}
		_Brightness ("Brightness", Float) = 1
		_Opacity ("Opacity", Range(0, 1)) = 1
		[Header(LensFlare)] [Space(5)] _LensFlareFalloffDistance ("Falloff Distance", Float) = 5
		[Header(Rendering)] [Space(5)] [Tooltip(Changes the depth value. Negative values are closer to the camera)] _Offset ("Offset", Float) = 0
		[Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
		[Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
		[Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
		[Enum(None, 0, Alpha, 1, Red, 8, Green, 4, Blue, 2, RGB, 14, RGBA, 15)] _ColorMask ("Color Mask", Float) = 14
		[Header(Stencil)] [Space(5)] [EightBit] _Stencil ("Stencil ID", Float) = 0
		[Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil Comparison", Float) = 0
		[Enum(UnityEngine.Rendering.StencilOp)] _StencilOp ("Stencil Operation", Float) = 0
		[Enum(UnityEngine.Rendering.StencilOp)] _StencilFail ("Stencil Fail", Float) = 0
		[Enum(UnityEngine.Rendering.StencilOp)] _StencilZFail ("Stencil ZFail", Float) = 0
		[EightBit] _ReadMask ("ReadMask", Float) = 255
		[EightBit] _WriteMask ("WriteMask", Float) = 255
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