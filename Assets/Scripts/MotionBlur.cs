using UnityEngine;

// 实现简单的残影式运动模糊。
// 开启额外模糊时，会降低图像分辨率，
// 因此场景始终带有轻微模糊。
// 效果通过在累积纹理中保存前几帧
// 并与当前帧混合实现。
[ExecuteInEditMode]
[AddComponentMenu("Image Effects/Motion Blur (Color Accumulation)")]
[RequireComponent(typeof(Camera))]

public class MotionBlur : ImageEffectBase
{
	public float blurAmount = 0.8f;
	public bool extraBlur = false;
	
	private RenderTexture accumTexture;
	
	override protected void Start()
	{
		if(!SystemInfo.supportsRenderTextures)
		{
			enabled = false;
			return;
		}
		base.Start();
	}
	
	override protected void OnDisable()
	{
		base.OnDisable();
		DestroyImmediate(accumTexture);
	}

	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination)
	{
		// 创建帧累积纹理。
		if (accumTexture == null || accumTexture.width != source.width || accumTexture.height != source.height)
		{
			DestroyImmediate(accumTexture);
			accumTexture = new RenderTexture(source.width, source.height, 0);
			accumTexture.hideFlags = HideFlags.HideAndDontSave;
			Graphics.Blit( source, accumTexture );
		}
		
		// 开启额外模糊时，对纹理进行降采样。
		if (extraBlur)
		{
			RenderTexture blurbuffer = RenderTexture.GetTemporary(source.width/4, source.height/4, 0);
			Graphics.Blit(accumTexture, blurbuffer);
			Graphics.Blit(blurbuffer,accumTexture);
			RenderTexture.ReleaseTemporary(blurbuffer);
		}
		
		// 限制运动模糊参数，避免画面产生永久残影。
		blurAmount = Mathf.Clamp( blurAmount, 0.0f, 0.92f );
		
		// 设置着色器使用的纹理和浮点参数。
		material.SetTexture("_MainTex", accumTexture);
		material.SetFloat("_AccumOrig", 1.0F-blurAmount);
		
		// 使用运动模糊着色器渲染图像。
		Graphics.Blit (source, accumTexture, material);
		Graphics.Blit (accumTexture, destination);
	}
}
