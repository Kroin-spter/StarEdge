using UnityEngine;

[ExecuteInEditMode]
[AddComponentMenu("Image Effects/Grayscale")]
public class GrayscaleEffect : ImageEffectBase {
	public Texture  textureRamp;
	public float    rampOffset;

	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination) {
		material.SetTexture("_RampTex", textureRamp);
		material.SetFloat("_RampOffset", rampOffset);
		Graphics.Blit (source, destination, material);
	}
}
