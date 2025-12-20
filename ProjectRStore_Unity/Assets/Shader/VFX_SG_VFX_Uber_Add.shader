Shader "VFX/SG/VFX_Uber_Add" {
	Properties {
		[ToggleUI] Boolean_USE_DotBlend ("USE_DotBlend", Float) = 0
		_00_DotScale ("00_DotScale", Float) = 120
		_00_DotBlendSpeed ("00_DotBlendSpeed", Float) = 0.25
		_00_CellDensity ("00_CellDensity", Float) = 2
		[ToggleUI] Boolean_Toggle_USE_Refine ("Toggle_USE_Refine", Float) = 0
		_01_Refine ("01_Refine", Vector) = (1,1,1,1)
		_01_MainTex_Saturation ("01_MainTex_Saturation", Range(0, 1)) = 1
		_01_MainTexIntensity ("01_MainTexIntensity", Float) = 1
		[HDR] _01_MainTexColor ("01_MainTexColor", Vector) = (1,1,1,0)
		_01_MainTexPower ("01_MainTexPower", Float) = 1
		_01_MainTexAlphaADD ("01_MainTexAlphaADD", Float) = 0
		_HueShift ("01_MainTexHueShift", Float) = 0
		[NoScaleOffset] _01_MainTex ("01_MainTex", 2D) = "white" {}
		_01_MainTex_Tiling_Offset ("01_MainTex_Tiling_Offset", Vector) = (1,1,0,0)
		[ToggleUI] Boolean_ALPHA_Depthfade ("ALPHA_USEAlphaDepthfade", Float) = 0
		_ALPHA_Depthfade ("ALPHA_Depthfade", Float) = 5
		[ToggleUI] Boolean_USE_FadeBaseOnViewAngle ("USE_FadeBaseOnViewAngle", Float) = 0
		_ALPHA_FadeBasedOnViewAngle ("ALPHA_FadeBasedOnViewAngle", Float) = 0.5
		_FadeBasedOnViewAngleIntensity ("FadeBasedOnViewAngleIntensity", Float) = 1
		Vector1_581da4ffc3484f5680b823a5f6616fee ("ALPHA_InversFade", Range(0, 1)) = 0
		[ToggleUI] Boolean_2a0b3641d8ba4bc8879a9236dd9994a4 ("USE_Twinkle", Float) = 0
		_Twinkle_R_Speed_G_Min_B_MaxA_Mul ("Twinkle_R_Speed_G_Min_B_MaxA_Mul", Vector) = (20,0.5,1,1)
		[ToggleUI] Boolean_USE_DissolveTex ("USE_DissolveTex", Float) = 0
		[NoScaleOffset] _02_DissolveTex ("02_DissolveTex", 2D) = "white" {}
		_02_DissolveTex_Tiling_Offset ("02_DissolveTex_Tiling_Offset", Vector) = (1,1,0,0)
		_02_DissolveSharpness ("02_DissolveSharpness", Float) = 2
		_02_DissolveDebug ("02_DissolveDebug", Float) = 0
		[ToggleUI] Boolean_c3c96277cf8d4f10a132a2a6ad8ecee1 ("USE_MaskTex", Float) = 0
		[NoScaleOffset] _03_MaskTex ("03_MaskTex", 2D) = "white" {}
		_03_MaskTex_Tiling_Offset ("03_MaskTex_Tiling_Offset", Vector) = (1,1,0,0)
		[ToggleUI] Boolean_150eceaa8f7f48908496fc5a47502b1e ("USE_DistortTex", Float) = 0
		[NoScaleOffset] Texture2D_608bcc14a42f4b4e8297ef0864da5f0a ("04_DistortTex", 2D) = "white" {}
		_04_DistortTex_Tiling_Offset ("04_DistortTex_Tiling_Offset", Vector) = (1,1,0,0)
		_04_DistortIntensity ("04_DistortIntensity", Float) = 0.05
		[ToggleUI] Boolean_USE_ColorTexHUE ("USE_ColorTexHUE", Float) = 0
		[NoScaleOffset] _05_ColorTex ("05_ColorTex", 2D) = "white" {}
		_05_ColorTexHUE ("05_ColorTexHUE", Float) = 0
		[ToggleUI] Boolean_USE_VertexOffset ("USE_VertexOffset", Float) = 0
		_06_VertexTexIntensity ("06_VertexTexIntensity", Float) = 0.2
		_06_VertexSpeed ("06_VertexSpeed", Float) = 0.2
		[Toggle] _BOOLEAN_TOGGLE_USE_RGBA_CHOICE ("Toggle_Use_RGBA_Choice", Float) = 0
		[KeywordEnum(R, G, B, A)] _RGBA_CHOICE ("RGBA_Choice", Float) = 0
		[KeywordEnum(Default, Panner, CustomData)] ENUM_01_TOGGLE_USE_UV_CONTROL ("01_Toggle_Use_UV_Control", Float) = 0
		[KeywordEnum(R, G, C, A)] ENUM_02_MASKTEX_CHOICE ("03_MaskTex_Choice", Float) = 0
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