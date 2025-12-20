Shader "Fusion/Fusion Stats Graph" {
	Properties {
		_BaseColor ("Base Color", Vector) = (0,1,0,1)
		_AverageColor ("Average Color", Vector) = (1,1,1,0)
		_Threshold1Color ("Threshold 1 Color", Vector) = (1,1,0,1)
		_Threshold2Color ("Threshold 2 Color", Vector) = (1,0.5,0,1)
		_Threshold3Color ("Threshold 3 Color", Vector) = (1,0,0,1)
		_FadeColorIntensity ("Fade Color Intensity", Float) = 1
		_PointsThickness ("Points Thickness", Float) = 1
		_LinesThickness ("Lines Thickness", Float) = 1
		_SideFalloff ("Side Falloff", Float) = 1
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