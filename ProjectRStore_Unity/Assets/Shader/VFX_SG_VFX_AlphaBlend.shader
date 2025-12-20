Shader "VFX/SG/VFX_AlphaBlend" {
	Properties {
		Vector1_e9d45b1eea6e4a92949287a1ccc91137 ("Saturation", Float) = 1
		[HDR] _01_MainTexColor ("01_MainTexColor", Vector) = (1,1,1,1)
		_01_Alpha_Intensity ("01_Alpha_Intensity", Float) = 1
		[NoScaleOffset] _01_MainTex ("01_MainTex", 2D) = "white" {}
		_01_MainTex_Tiling_Offset ("01_MainTex_Tiling_Offset", Vector) = (1,1,0,0)
		[ToggleUI] Boolean_110a8b0713fe4ae98946a2282a6301a6 ("Toggle_Use_DepthFade", Float) = 0
		_DepthFade ("DepthFade", Float) = 1
		[Toggle] BOOLEAN_TOGGLE_USE_RGB_CHOICE ("01_Toggle_Use_RGB_Choice", Float) = 0
		[KeywordEnum(R, G, B, A)] _RGBA_CHOICE ("01_RGBA_Choice", Float) = 0
		[HideInInspector] _QueueOffset ("_QueueOffset", Float) = 0
		[HideInInspector] _QueueControl ("_QueueControl", Float) = -1
		[HideInInspector] [NoScaleOffset] unity_Lightmaps ("unity_Lightmaps", 2DArray) = "" {}
		[HideInInspector] [NoScaleOffset] unity_LightmapsInd ("unity_LightmapsInd", 2DArray) = "" {}
		[HideInInspector] [NoScaleOffset] unity_ShadowMasks ("unity_ShadowMasks", 2DArray) = "" {}
		[HideInInspector] _BUILTIN_QueueOffset ("Float", Float) = 0
		[HideInInspector] _BUILTIN_QueueControl ("Float", Float) = -1
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
	Fallback "Hidden/Shader Graph/FallbackError"
	//CustomEditor "UnityEditor.ShaderGraph.GenericShaderGraphMaterialGUI"
}