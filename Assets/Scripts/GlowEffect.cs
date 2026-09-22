using UnityEngine;

// 辉光使用透明度通道表示额外亮度。
// 旧版内置着色器在该通道输出基础纹理与颜色透明度的乘积，
// 再叠加高光强度与高光颜色透明度的乘积。
// 通常可将基础纹理的透明度设为零，
// 或将颜色透明度设为零；旧版默认值为 0.5。
 
[ExecuteInEditMode]
[RequireComponent (typeof(Camera))]
[AddComponentMenu("Image Effects/Glow")]
public class GlowEffect : MonoBehaviour
{
	/// 辉光亮度，大于 1 时额外增强。
	public float glowIntensity = 1.5f;
	
	/// 模糊迭代次数，数值越大越模糊。
	public int blurIterations = 3;
	
	/// 每次迭代的模糊扩散范围。较小值
	/// 通常效果更细腻，但需要更多迭代
	/// 才能实现大范围模糊，常用范围为 0.5 到 1.0。
	public float blurSpread = 0.7f;
	
	/// 辉光染色，透明度会对整个画面增加辉光。
	public Color glowTint = new Color(1,1,1,0);
	
	
	// --------------------------------------------------------
	// 最终合成着色器：
	// 将辉光颜色、透明度与强度的乘积叠加到原图。
	// 合成阶段的辉光强度限制为 0 到 1，超出部分
	// 在模糊阶段提前施加。
	
    public Shader compositeShader;
    Material m_CompositeMaterial = null;
	protected Material compositeMaterial {
		get {
			if (m_CompositeMaterial == null) {
                m_CompositeMaterial = new Material(compositeShader);
				m_CompositeMaterial.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_CompositeMaterial;
		} 
	}
	
	
	// --------------------------------------------------------
	// 执行模糊迭代的着色器。
	// 对四个纹理采样求平均。
	// 重复执行并逐步扩大采样位置间距，
	// 得到高斯模糊的近似效果。
	// 对四个采样求平均时，_Color 的透明度通常为 0.25；
	// 辉光强度大于 1 时增加该值。

    public Shader blurShader;
    Material m_BlurMaterial = null;
	protected Material blurMaterial {
		get {
			if (m_BlurMaterial == null) {
                m_BlurMaterial = new Material(blurShader);
                m_BlurMaterial.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_BlurMaterial;
		} 
	}
	
	
	// --------------------------------------------------------
	// 各亮度模式使用的降采样着色器。
	// 由于实现复杂且使用 Cg，着色器保存在外部资产中。
	public Shader downsampleShader;
	Material m_DownsampleMaterial = null;
	protected Material downsampleMaterial {
		get {
			if (m_DownsampleMaterial == null) {
				m_DownsampleMaterial = new Material( downsampleShader );
				m_DownsampleMaterial.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_DownsampleMaterial;
		} 
	}
	
	
	// --------------------------------------------------------
	// 辉光效果运行逻辑。
	
	protected void OnDisable()
	{
		if( m_CompositeMaterial ) {
			DestroyImmediate( m_CompositeMaterial );
		}
		if( m_BlurMaterial ) {
			DestroyImmediate( m_BlurMaterial );
		}
		if( m_DownsampleMaterial )
			DestroyImmediate( m_DownsampleMaterial );
	}
	
	protected void Start()
	{
		// 不支持图像效果时禁用组件。
		if (!SystemInfo.supportsImageEffects)
		{
			enabled = false;
			return;
		}
		
		// 未设置降采样着色器时禁用效果。
		if( downsampleShader == null )
		{
			Debug.Log ("No downsample shader assigned! Disabling glow.");
			enabled = false;
		}
		// 任一着色器不受显卡支持时禁用效果。
		else
		{		
			if( !blurMaterial.shader.isSupported )
				enabled = false;
			if( !compositeMaterial.shader.isSupported )
				enabled = false;
			if( !downsampleMaterial.shader.isSupported )
				enabled = false;
		}
	}
	
	// 执行一次模糊迭代。
	public void FourTapCone (RenderTexture source, RenderTexture dest, int iteration)
	{
		float off = 0.5f + iteration*blurSpread;
		Graphics.BlitMultiTap (source, dest, blurMaterial,
            new Vector2( off, off),
			new Vector2(-off, off),
            new Vector2( off,-off),
            new Vector2(-off,-off)
		);
	}
	
	// 将纹理降采样到四分之一分辨率。
	private void DownSample4x (RenderTexture source, RenderTexture dest)
	{
		downsampleMaterial.color = new Color( glowTint.r, glowTint.g, glowTint.b, glowTint.a/4.0f );
		Graphics.Blit (source, dest, downsampleMaterial);
	}
	
	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination)
	{
		// 将参数限制在有效范围内。
		glowIntensity = Mathf.Clamp( glowIntensity, 0.0f, 10.0f );
		blurIterations = Mathf.Clamp( blurIterations, 0, 30 );
		blurSpread = Mathf.Clamp( blurSpread, 0.5f, 1.0f );
		
		RenderTexture buffer = RenderTexture.GetTemporary(source.width/4, source.height/4, 0);
		RenderTexture buffer2 = RenderTexture.GetTemporary(source.width/4, source.height/4, 0);
		
		// 将源图像复制到降采样纹理。
		DownSample4x (source, buffer);
		
		// 对缩小后的纹理进行模糊处理。
		float extraBlurBoost = Mathf.Clamp01( (glowIntensity - 1.0f) / 4.0f );
		blurMaterial.color = new Color( 1F, 1F, 1F, 0.25f + extraBlurBoost );
		
		bool oddEven = true;
		for(int i = 0; i < blurIterations; i++)
		{
			if( oddEven )
				FourTapCone (buffer, buffer2, i);
			else
				FourTapCone (buffer2, buffer, i);
			oddEven = !oddEven;
		}
		Graphics.Blit(source,destination);
				
		if( oddEven )
			BlitGlow(buffer, destination);
		else
			BlitGlow(buffer2, destination);
		
		RenderTexture.ReleaseTemporary(buffer);
		RenderTexture.ReleaseTemporary(buffer2);
	}
	
	public void BlitGlow( RenderTexture source, RenderTexture dest )
	{
		compositeMaterial.color = new Color(1F, 1F, 1F, Mathf.Clamp01(glowIntensity));
		Graphics.Blit (source, dest, compositeMaterial);
	}	
}
