// 使用 Roberts 交叉算子实现边缘检测。

using UnityEngine;

[ExecuteInEditMode]
[AddComponentMenu("Image Effects/Edge Detection (Color)")]
public class EdgeDetectEffect : ImageEffectBase
{
	public float threshold = 0.2F;
	
	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination)
	{
		material.SetFloat ("_Treshold", threshold * threshold);
		Graphics.Blit (source, destination, material);
	}
}
