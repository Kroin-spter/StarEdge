using UnityEngine;
using System.Collections;

[ExecuteInEditMode]
[AddComponentMenu("Image Effects/Color Correction (Ramp)")]
public class ColorCorrectionEffect : ImageEffectBase {
	public Texture  textureRamp;

	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination) {
		material.SetTexture ("_RampTex", textureRamp);
		Graphics.Blit (source, destination, material);
	}
}
