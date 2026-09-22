using UnityEngine;
using System.Collections;

[ExecuteInEditMode]
[AddComponentMenu("Image Effects/Blur")]
public class BlurEffect : MonoBehaviour
{	
	/// 模糊迭代次数，数值越大越模糊。
	public int iterations = 3;
	
	/// 每次迭代的模糊扩散范围。较小值
	/// 通常效果更细腻，但需要更多迭代
	/// 才能实现大范围模糊，常用范围为 0.5 到 1.0。
	public float blurSpread = 0.6f;
	
	
	// --------------------------------------------------------
	// 执行模糊迭代的着色器。
	// 对四个纹理采样求平均。
	// 重复执行并逐步扩大采样位置间距，
	// 得到高斯模糊的近似效果。
	 
	public Shader blurShader = null;	

	// 保留旧版模糊材质实现的位置。

	static Material m_Material = null;
	protected Material material {
		get {
			if (m_Material == null) {
				m_Material = new Material(blurShader);
				m_Material.hideFlags = HideFlags.DontSave;
			}
			return m_Material;
		} 
	}
	
	protected void OnDisable() {
		if( m_Material ) {
			DestroyImmediate( m_Material );
		}
	}	
	
	// --------------------------------------------------------
	
	protected void Start()
	{
		// 不支持图像效果时禁用组件。
		if (!SystemInfo.supportsImageEffects) {
			enabled = false;
			return;
		}
		// 显卡不支持该着色器时禁用组件。
		if (!blurShader || !material.shader.isSupported) {
			enabled = false;
			return;
		}
	}
	
	// 执行一次模糊迭代。
	public void FourTapCone (RenderTexture source, RenderTexture dest, int iteration)
	{
		float off = 0.5f + iteration*blurSpread;
		Graphics.BlitMultiTap (source, dest, material,
			new Vector2(-off, -off),
			new Vector2(-off,  off),
			new Vector2( off,  off),
			new Vector2( off, -off)
		);
	}
	
	// 将纹理降采样到四分之一分辨率。
	private void DownSample4x (RenderTexture source, RenderTexture dest)
	{
		float off = 1.0f;
		Graphics.BlitMultiTap (source, dest, material,
			new Vector2(-off, -off),
			new Vector2(-off,  off),
			new Vector2( off,  off),
			new Vector2( off, -off)
		);
	}
	
	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination) {		
		RenderTexture buffer = RenderTexture.GetTemporary(source.width/4, source.height/4, 0);
		RenderTexture buffer2 = RenderTexture.GetTemporary(source.width/4, source.height/4, 0);
		
		// 将源图像复制到降采样纹理。
		DownSample4x (source, buffer);
		
		// 对缩小后的纹理进行模糊处理。
		bool oddEven = true;
		for(int i = 0; i < iterations; i++)
		{
			if( oddEven )
				FourTapCone (buffer, buffer2, i);
			else
				FourTapCone (buffer2, buffer, i);
			oddEven = !oddEven;
		}
		if( oddEven )
			Graphics.Blit(buffer, destination);
		else
			Graphics.Blit(buffer2, destination);
		
		RenderTexture.ReleaseTemporary(buffer);
		RenderTexture.ReleaseTemporary(buffer2);
	}	
}
